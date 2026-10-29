// Experimental debugger-based writer tracing. AMD64 Windows only.
// No custom DLL injection, game code patching, or game function calls.
// DebugBreakProcess requests a breakpoint in the target for setup/cleanup.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WowBCtmCapture
{
    internal sealed class WatchTarget
    {
        public ulong Triple, Address;
        public string Description = "";
    }
    internal static class WriteTracer
    {
        private const uint ContextFlags = 0x00100013; // AMD64: control, integer, debug registers
        private const uint DebugRegs = 0x00100010;
        private const uint ThreadGetContext = 0x0008, ThreadSetContext = 0x0010;
        private const uint CreateThreadEvent = 2, CreateProcessEvent = 3, ExitThreadEvent = 4,
            ExitProcessEvent = 5, LoadDllEvent = 6, ExceptionEvent = 1;
        private const uint Breakpoint = 0x80000003, SingleStep = 0x80000004;
        private const uint Continue = 0x00010002, NotHandled = 0x80010001;
        private const int ErrorTimeout = 121, MaxHits = 3000, MaxDetail = 200, DurationSeconds = 90;
        // Native AMD64 CONTEXT and DEBUG_EVENT sizes/offsets; x64 Windows only.
        private const int ContextSize = 1232, EventSize = 176;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcess(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcessStop(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugSetProcessKillOnExit(bool kill);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugBreakProcess(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WaitForDebugEvent(IntPtr data, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
        private static ulong U(IntPtr p) => unchecked((ulong)p.ToInt64());
        private static IntPtr P(ulong p) => new IntPtr(unchecked((long)p));
        private static ulong Get64(IntPtr p, int at) => unchecked((ulong)Marshal.ReadInt64(p, at));
        private static uint Get32(IntPtr p, int at) => unchecked((uint)Marshal.ReadInt32(p, at));
        private static void Put64(IntPtr p, int at, ulong v) => Marshal.WriteInt64(p, at, unchecked((long)v));
        private static string Hex(ulong x) => "0x" + x.ToString("X", CultureInfo.InvariantCulture);
        private sealed class Context : IDisposable
        {
            private readonly IntPtr storage;
            public readonly IntPtr Ptr;
            public Context(uint flags)
            {
                storage = Marshal.AllocHGlobal(ContextSize + 15);
                Ptr = new IntPtr((storage.ToInt64() + 15) & ~15L);
                Marshal.Copy(new byte[ContextSize], 0, Ptr, ContextSize);
                Marshal.WriteInt32(Ptr, 48, unchecked((int)flags));
            }
            public void Dispose() => Marshal.FreeHGlobal(storage);
        }
        private sealed class ThreadState
        {
            public readonly ulong[] OldDr = new ulong[4];
            public readonly int[] TargetBySlot = { -1, -1, -1, -1 };
            public ulong OldDr6, OldDr7, SetMask;
        }
        private static bool Arm(uint tid, List<WatchTarget> targets, Dictionary<uint, ThreadState> states,
            StringBuilder report)
        {
            if (states.ContainsKey(tid)) return true;
            IntPtr thread = OpenThread(ThreadGetContext | ThreadSetContext, false, tid);
            if (thread == IntPtr.Zero) { report.AppendLine("arm failed tid=" + tid + " OpenThread=" + Marshal.GetLastWin32Error()); return false; }
            try
            {
                using var ctx = new Context(ContextFlags);
                if (!GetThreadContext(thread, ctx.Ptr)) { report.AppendLine("arm failed tid=" + tid + " GetThreadContext=" + Marshal.GetLastWin32Error()); return false; }
                var state = new ThreadState();
                for (int i = 0; i < 4; i++) state.OldDr[i] = Get64(ctx.Ptr, 72 + 8 * i);
                state.OldDr6 = Get64(ctx.Ptr, 104); state.OldDr7 = Get64(ctx.Ptr, 112);
                ulong dr7 = state.OldDr7; int n = 0;
                for (int slot = 0; slot < 4 && n < targets.Count; slot++)
                {
                    // Do not take a slot already enabled locally or globally.
                    if ((dr7 & (3UL << (slot * 2))) != 0) continue;
                    state.TargetBySlot[slot] = n;
                    Put64(ctx.Ptr, 72 + slot * 8, targets[n].Address);
                    dr7 &= ~(15UL << (16 + slot * 4));
                    dr7 |= 13UL << (16 + slot * 4); // RW=01 write, LEN=11 four bytes
                    dr7 |= 1UL << (slot * 2); // local enable
                    state.SetMask |= 1UL << slot;
                    n++;
                }
                if (n == 0) { report.AppendLine("arm skipped tid=" + tid + " (no free hardware slots)"); return false; }
                Put64(ctx.Ptr, 112, dr7);
                Put64(ctx.Ptr, 104, state.OldDr6 & ~state.SetMask);
                Marshal.WriteInt32(ctx.Ptr, 48, unchecked((int)DebugRegs));
                if (!SetThreadContext(thread, ctx.Ptr)) { report.AppendLine("arm failed tid=" + tid + " SetThreadContext=" + Marshal.GetLastWin32Error()); return false; }
                states.Add(tid, state);
                if (n < targets.Count) report.AppendLine("tid=" + tid + " only " + n + " free debug slots; other targets not watched on this thread");
                return true;
            }
            finally { CloseHandle(thread); }
        }
        private static int Restore(Dictionary<uint, ThreadState> states, StringBuilder report)
        {
            int errors = 0;
            foreach (var pair in states)
            {
                IntPtr thread = OpenThread(ThreadGetContext | ThreadSetContext, false, pair.Key);
                if (thread == IntPtr.Zero) { errors++; report.AppendLine("restore failed tid=" + pair.Key + " OpenThread=" + Marshal.GetLastWin32Error()); continue; }
                try
                {
                    using var ctx = new Context(ContextFlags);
                    if (!GetThreadContext(thread, ctx.Ptr)) { errors++; report.AppendLine("restore failed tid=" + pair.Key + " GetThreadContext=" + Marshal.GetLastWin32Error()); continue; }
                    // Restore only slots we took; preserve state for other slots.
                    ulong dr7 = Get64(ctx.Ptr, 112);
                    for (int i = 0; i < 4; i++)
                    {
                        if ((pair.Value.SetMask & (1UL << i)) == 0) continue;
                        Put64(ctx.Ptr, 72 + 8 * i, pair.Value.OldDr[i]);
                        ulong mask = (3UL << (i * 2)) | (15UL << (16 + i * 4));
                        dr7 = (dr7 & ~mask) | (pair.Value.OldDr7 & mask);
                    }
                    Put64(ctx.Ptr, 112, dr7);
                    ulong dr6 = Get64(ctx.Ptr, 104);
                    Put64(ctx.Ptr, 104, (dr6 & ~pair.Value.SetMask) | (pair.Value.OldDr6 & pair.Value.SetMask));
                    Marshal.WriteInt32(ctx.Ptr, 48, unchecked((int)DebugRegs));
                    if (!SetThreadContext(thread, ctx.Ptr)) { errors++; report.AppendLine("restore failed tid=" + pair.Key + " SetThreadContext=" + Marshal.GetLastWin32Error()); }
                }
                finally { CloseHandle(thread); }
            }
            return errors;
        }
        private static string DescribeAddress(ulong addr, ulong imageBase, ulong imageSize)
            => addr >= imageBase && addr - imageBase < imageSize ? Hex(addr) + " (wowb+rva=" + Hex(addr - imageBase) + ")" : Hex(addr);
        private static byte[] Read(IntPtr process, ulong addr, int size)
        {
            var b = new byte[size];
            if (!ReadProcessMemory(process, P(addr), b, new UIntPtr((uint)size), out UIntPtr read) || read.ToUInt64() != (ulong)size)
                return Array.Empty<byte>();
            return b;
        }
        private static bool LogHit(uint tid, IntPtr process, ulong imageBase, ulong imageSize, List<WatchTarget> targets,
            Dictionary<uint, ThreadState> states, string phase, StringBuilder report, ref int hits,
            Dictionary<string, int> signatures, ref int detailed)
        {
            if (!states.TryGetValue(tid, out ThreadState? state)) return false;
            IntPtr thread = OpenThread(ThreadGetContext | ThreadSetContext, false, tid);
            if (thread == IntPtr.Zero) { report.AppendLine("hit context unavailable tid=" + tid); return false; }
            try
            {
                using var ctx = new Context(ContextFlags);
                if (!GetThreadContext(thread, ctx.Ptr)) { report.AppendLine("hit context failed tid=" + tid); return false; }
                ulong dr6 = Get64(ctx.Ptr, 104), hitMask = dr6 & state.SetMask;
                if (hitMask == 0) return false;
                ulong rip = Get64(ctx.Ptr, 248), rsp = Get64(ctx.Ptr, 152);
                hits++;
                string signature = phase + " tid=" + tid + " RIP-after-write=" + DescribeAddress(rip, imageBase, imageSize) + " slots=" + Hex(hitMask);
                bool first = !signatures.TryGetValue(signature, out int count);
                signatures[signature] = count + 1;
                if (first && detailed < MaxDetail)
                {
                    detailed++;
                    report.AppendLine("HIT " + hits + " phase=" + phase + " tid=" + tid + " dr6=" + Hex(dr6) +
                        " RIP-after-write=" + DescribeAddress(rip, imageBase, imageSize));
                    for (int i = 0; i < 4; i++)
                        if ((hitMask & (1UL << i)) != 0)
                        {
                            WatchTarget t = targets[state.TargetBySlot[i]];
                            byte[] value = Read(process, t.Address, 4);
                            report.AppendLine("  slot=" + i + " watched=" + Hex(t.Address) + " triple=" + Hex(t.Triple) +
                                " newBytes=" + (value.Length == 4 ? Convert.ToHexString(value) : "unreadable"));
                        }
                    report.AppendLine("  RAX=" + Hex(Get64(ctx.Ptr, 120)) + " RCX=" + Hex(Get64(ctx.Ptr, 128)) +
                        " RDX=" + Hex(Get64(ctx.Ptr, 136)) + " RBX=" + Hex(Get64(ctx.Ptr, 144)) +
                        " RSP=" + Hex(rsp) + " RBP=" + Hex(Get64(ctx.Ptr, 160)) +
                        " RSI=" + Hex(Get64(ctx.Ptr, 168)) + " RDI=" + Hex(Get64(ctx.Ptr, 176)));
                    for (int i = 0; i < 8; i++) report.Append("  R" + (i + 8) + "=" + Hex(Get64(ctx.Ptr, 184 + 8 * i)));
                    report.AppendLine();
                    byte[] code = rip >= 32 ? Read(process, rip - 32, 48) : Array.Empty<byte>();
                    report.AppendLine("  bytes[RIP-32..RIP+15]=" + (code.Length == 48 ? Convert.ToHexString(code) : "unreadable"));
                    byte[] stack = Read(process, rsp, 128);
                    if (stack.Length == 128)
                    {
                        report.Append("  raw stack qwords (not a validated unwind):");
                        for (int i = 0; i < 16; i++) report.Append(" " + DescribeAddress(BitConverter.ToUInt64(stack, i * 8), imageBase, imageSize));
                        report.AppendLine();
                    }
                    else report.AppendLine("  raw stack unreadable");
                }
                Put64(ctx.Ptr, 104, dr6 & ~hitMask);
                Marshal.WriteInt32(ctx.Ptr, 48, unchecked((int)DebugRegs));
                if (!SetThreadContext(thread, ctx.Ptr)) { report.AppendLine("  WARNING: could not clear DR6, error=" + Marshal.GetLastWin32Error()); return false; }
                // Preserve other simultaneous breakpoint/single-step causes for the client.
                return (dr6 & (0xE00FUL & ~state.SetMask)) == 0;
            }
            finally { CloseHandle(thread); }
        }
        public static bool Run(uint pid, IntPtr process, ulong imageBase, ulong imageSize,
            List<WatchTarget> targets, string path)
        {
            var report = new StringBuilder();
            report.AppendLine("WOWB CTM HARDWARE WATCH TRACE v1 -- EXPERIMENTAL");
            report.AppendLine("Debugger attachment suspends client threads at events; DebugBreakProcess requests target breakpoints. No custom DLL or game-code patch. Candidates and raw stack are not proof of CTM.");
            report.AppendLine("A data watchpoint traps AFTER the write; RIP is the following instruction, not necessarily the writer entry. Raw stack is not an unwind.");
            report.AppendLine("pid=" + pid + " imageBase=" + Hex(imageBase) + " imageSize=" + Hex(imageSize) + " maxSeconds=" + DurationSeconds + " maxHits=" + MaxHits);
            for (int i = 0; i < targets.Count; i++) report.AppendLine("target " + i + " " + targets[i].Description);
            bool attached = false, restored = false, detached = false, ok = true, haveInitialBreak = false;
            bool forcedBreak = false, stop = false, active = false, pendingArm = false; double forcedAt = 0; int hits = 0, detailed = 0, armFailures = 0, restoreFailures = 0;
            string reason = "unknown", phase = "unlabelled";
            var states = new Dictionary<uint, ThreadState>();
            var threadIds = new HashSet<uint>();
            var signatures = new Dictionary<string, int>();
            var input = new ConcurrentQueue<string>();
            IntPtr evt = Marshal.AllocHGlobal(EventSize);
            var watch = Stopwatch.StartNew();
            try
            {
                if (!DebugActiveProcess(pid)) { report.AppendLine("attach failed win32=" + Marshal.GetLastWin32Error()); return false; }
                attached = true;
                if (!DebugSetProcessKillOnExit(false))
                { report.AppendLine("DebugSetProcessKillOnExit(false) failed win32=" + Marshal.GetLastWin32Error()); ok = false; return false; }
                var reader = new Thread(() =>
                {
                    try { while (true) { string? s = Console.ReadLine(); if (s == null) { input.Enqueue("q"); break; } input.Enqueue(s.Trim().ToLowerInvariant()); } }
                    catch { input.Enqueue("q"); }
                }) { IsBackground = true, Name = "Trace console input" };
                reader.Start();
                Console.WriteLine("Debugger attached. Wait for READY before clicking. Target may pause frequently.");
                while (true)
                {
                    while (input.TryDequeue(out string? command))
                    {
                        if (command == "q") { stop = true; reason = "user requested stop"; }
                        else if (command == "c" || command == "d" || command == "k")
                        { phase = command; report.AppendLine("phase label=" + phase + " elapsed=" + watch.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
                            if (!active) { pendingArm = true; Console.WriteLine("Starting watchpoints; wait for ARMED."); }
                            else Console.WriteLine("Phase " + phase.ToUpperInvariant() + " labelled; return to game."); }
                    }
                    if (!stop && watch.Elapsed.TotalSeconds >= DurationSeconds) { stop = true; reason = "time limit"; }
                    if (!stop && hits >= MaxHits) { stop = true; reason = "hit limit"; }
                    if (!WaitForDebugEvent(evt, 200))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorTimeout) { report.AppendLine("WaitForDebugEvent failed win32=" + error); ok = false; reason = "wait failed"; break; }
                        if ((stop || pendingArm) && !forcedBreak)
                        {
                            if (!DebugBreakProcess(process)) { report.AppendLine("DebugBreakProcess failed win32=" + Marshal.GetLastWin32Error()); ok = false; break; }
                            forcedBreak = true; forcedAt = watch.Elapsed.TotalSeconds; report.AppendLine("Requested debugger break to arm or clean up watchpoints.");
                        }
                        if (forcedBreak && watch.Elapsed.TotalSeconds >= forcedAt + 15) { ok = false; reason = "cleanup break timed out"; break; }
                        continue;
                    }
                    uint code = Get32(evt, 0), eventPid = Get32(evt, 4), tid = Get32(evt, 8);
                    uint status = Continue;
                    bool finishThisEvent = false;
                    try
                    {
                        if (eventPid != pid) { report.AppendLine("Unexpected debug event pid=" + eventPid); ok = false; }
                        if (code == CreateProcessEvent || code == CreateThreadEvent)
                        {
                            threadIds.Add(tid);
                            if (active && !Arm(tid, targets, states, report)) armFailures++;
                            if (code == CreateProcessEvent)
                            {
                                IntPtr file = Marshal.ReadIntPtr(evt, 16);
                                if (file != IntPtr.Zero && file != new IntPtr(-1)) CloseHandle(file);
                            }
                        }
                        else if (code == LoadDllEvent)
                        {
                            IntPtr file = Marshal.ReadIntPtr(evt, 16);
                            if (file != IntPtr.Zero && file != new IntPtr(-1)) CloseHandle(file);
                        }
                        else if (code == ExitThreadEvent) { states.Remove(tid); threadIds.Remove(tid); }
                        else if (code == ExitProcessEvent) { report.AppendLine("Target exited during trace."); reason = "target exited"; stop = true; finishThisEvent = true; }
                        else if (code == ExceptionEvent)
                        {
                            uint exception = Get32(evt, 16);
                            if (exception == Breakpoint && !haveInitialBreak)
                            {
                                haveInitialBreak = true;
                                threadIds.Add(tid);
                                report.AppendLine("Initial debugger breakpoint reached; totalThreads=" + threadIds.Count);
                                Console.WriteLine("READY. In this console type C then Enter, click new ground destination C; D then Enter, click another destination; K then Enter, move with keyboard only; Q then Enter ends. Automatic stop after 90s or 3000 hits.");
                            }
                            else if (exception == Breakpoint && forcedBreak) { forcedBreak = false; if (stop) finishThisEvent = true; }
                            else if (exception == SingleStep && LogHit(tid, process, imageBase, imageSize, targets, states, phase, report, ref hits, signatures, ref detailed)) { }
                            else status = NotHandled; // never swallow client exceptions we don't own
                        }
                        if (pendingArm && !active && haveInitialBreak && !forcedBreak && !stop && code != ExitProcessEvent)
                        {
                            foreach (uint initialTid in threadIds)
                                if (!Arm(initialTid, targets, states, report)) armFailures++;
                            active = true; pendingArm = false;
                            report.AppendLine("watchpoints armedThreads=" + states.Count + " totalThreads=" + threadIds.Count);
                            if (states.Count == 0)
                            {
                                ok = false; reason = "no threads armed"; finishThisEvent = true;
                                Console.WriteLine("No threads could be armed; ending trace.");
                            }
                            else Console.WriteLine("ARMED. Phase " + phase.ToUpperInvariant() + ": return to game now.");
                        }
                        if (stop && !forcedBreak) finishThisEvent = true;
                        if (hits >= MaxHits && !forcedBreak) { reason = "hit limit"; finishThisEvent = true; }
                        if (finishThisEvent && code != ExitProcessEvent)
                        {
                            restoreFailures = Restore(states, report); restored = restoreFailures == 0;
                        }
                    }
                    catch (Exception e) { report.AppendLine("event processing error: " + e); ok = false; reason = "event processing error"; finishThisEvent = true; restoreFailures = Restore(states, report); restored = restoreFailures == 0; }
                    finally
                    {
                        if (!ContinueDebugEvent(eventPid, tid, status))
                        { report.AppendLine("ContinueDebugEvent failed win32=" + Marshal.GetLastWin32Error()); ok = false; finishThisEvent = true; }
                    }
                    if (finishThisEvent) break;
                }
            }
            catch (Exception e) { report.AppendLine("trace error: " + e); ok = false; }
            finally
            {
                if (attached)
                {
                    detached = DebugActiveProcessStop(pid);
                    if (!detached) report.AppendLine("DebugActiveProcessStop failed win32=" + Marshal.GetLastWin32Error());
                }
                watch.Stop();
                report.AppendLine("Distinct writer signatures (one detailed record per signature, capped at " + MaxDetail + "):");
                foreach (var pair in signatures) report.AppendLine("  count=" + pair.Value + " " + pair.Key);
                report.AppendLine("reason=" + reason + " elapsed=" + watch.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) +
                    " hits=" + hits + " armFailures=" + armFailures + " restoreFailures=" + restoreFailures +
                    " restored=" + restored + " detached=" + detached + " initialBreakpoint=" + haveInitialBreak);
                report.AppendLine("Threads without an available slot, unobserved writes, address reuse, asynchronous phase labels and scan filtering can produce false negatives.");
                report.AppendLine("If restored or detached is false, or tracing ended unexpectedly, restart the client before continuing.");
                try { File.WriteAllText(path, report.ToString(), Encoding.UTF8); Console.WriteLine("Trace report saved: " + path); }
                catch (Exception e) { Console.Error.WriteLine("Could not write trace report: " + e.Message); }
                Marshal.FreeHGlobal(evt);
            }
            return ok && detached && restored;
        }
    }
}
