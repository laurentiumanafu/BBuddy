using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GreenMagic
{
    /// <summary>The Lua value types of this client's VM (WoW 1.12-era Lua).</summary>
    public enum LuaType : byte
    {
        Nil = 0,
        Boolean = 1,
        LightUserData = 2,
        Number = 3,
        String = 4,
        Table = 5,
        Function = 6,
        UserData = 7,
        Thread = 8,
    }

    /// <summary>One value returned by a Lua API function.</summary>
    public struct LuaValue
    {
        public LuaType Type;

        /// <summary>
        /// The raw type word before masking. WoW sets flag bits on secure values
        /// (UnitHealth returns 0x103 for a number), so always compare
        /// <c>(RawType &amp; 0xFF) == Type</c> and never RawType itself.
        /// </summary>
        public int RawType;

        public double Number;
        public bool Boolean;
        public string? Text;
        public ulong Pointer;

        public override string ToString()
        {
            switch (Type)
            {
                case LuaType.Nil: return "nil";
                case LuaType.Boolean: return Boolean ? "true" : "false";
                case LuaType.Number: return Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case LuaType.String: return "\"" + (Text ?? string.Empty) + "\"";
                default: return "<" + Type + " 0x" + Pointer.ToString("X") + ">";
            }
        }
    }

    /// <summary>
    /// The bridge could not take the client's Lua thread over (or could not give it back).
    /// Distinct from a Lua-level refusal: nothing in the client's VM was touched, and the
    /// caller must not report it as "the client's Lua refused this".
    /// </summary>
    public class LuaTakeoverException : InvalidOperationException
    {
        public LuaTakeoverException(string message) : base(message) { }
    }

    /// <summary>
    /// Where commands execute. <see cref="ServerThread"/> is the injected resident thread
    /// (proven for reads: 1600+ calls). <see cref="LuaThreadHijack"/> runs each command on the
    /// client's OWN Lua thread by suspending it at a quiet moment, pointing its RIP at the
    /// payload and restoring the saved context afterwards - required for anything that raises
    /// FrameScript events, because the client asserts the caller is the Lua thread.
    /// </summary>
    public enum LuaExecutionMode
    {
        ServerThread = 0,
        LuaThreadHijack = 1,
    }

    /// <summary>
    /// A resident bridge into the Lua VM of a NATIVE 64-BIT World of Warcraft client
    /// (wowb.exe build 70291).
    ///
    /// Why it looks like this: every call path in this client's VM ends by handing its
    /// return address to the protector at module+0x1D8E80, which range-checks it,
    /// requires the `E8` call shape and otherwise dereferences null. A thread started at
    /// a warm `jmp qword ptr [rcx]` gadget inside the module, with lua_pcall entered
    /// through a two-hop return trampoline (see <see cref="TrampolineWords"/>), passes
    /// that check; the protocol below is the one proven live in hunt45/hunt46.
    ///
    /// ONE injected thread serves every call:
    /// <list type="bullet">
    /// <item><description>the driver writes a command block into a page it allocated in the target;</description></item>
    /// <item><description>the payload reads the command, runs getfield(globals,name) + pushlstring(arg) + lua_pcall, writes the results;</description></item>
    /// <item><description>the payload copies SEQ into ACK, which is how the driver knows the results are complete;</description></item>
    /// <item><description>when idle the payload calls kernel32!Sleep, and it exits by itself on a deadline, so a driver that dies can never leave a spinning thread behind.</description></item>
    /// </list>
    ///
    /// Measured on build 70291 (hunt46): 180 calls through one thread in 0.73 s
    /// (p50 1.06 ms), 0.78 % of one core while idle-polling at ~650 Hz.
    /// </summary>
    public sealed class LuaBridge64 : IDisposable
    {
        // ---------------------------------------------------------------- page layout
        private const int PageSize = 0x2000;
        private const int OffPayload = 0x800;
        private const int OffCtrl = 0x1000;
        private const int OffName = 0x1200;          // ctrl + 0x200
        private const int OffArg = 0x1280;           // ctrl + 0x280

        // command block fields (the payload reads/writes exactly these)
        private const int B_L = 0x08;
        private const int B_NamePtr = 0x10;
        private const int B_ArgPtr = 0x18;
        private const int B_Nargs = 0x20;
        private const int B_Nresults = 0x28;
        private const int B_SkipPcall = 0x30;
        private const int B_ArgLen = 0x38;
        private const int B_Variant = 0x40;
        private const int B_NameLen = 0x48;
        private const int B_RawR8 = 0x50;
        private const int B_TrampX = 0x58;
        private const int B_TrampMode = 0x60;
        private const int B_TopAfter = 0x68;
        private const int B_Stage = 0x70;
        private const int B_Func = 0x78;
        private const int B_PushLString = 0x80;
        private const int B_PCall = 0x88;
        private const int B_TopBefore = 0x90;
        private const int B_PcallStatus = 0x98;
        private const int B_Res1 = 0xA0;
        private const int B_Res1Type = 0xA8;
        private const int B_Res2 = 0xB0;
        private const int B_Res2Type = 0xB8;
        private const int B_Top1 = 0xC0;
        private const int B_Cmd = 0xC8;
        private const int B_Seq = 0xD0;
        private const int B_Ack = 0xD8;
        private const int B_Calls = 0xE0;
        private const int B_TickFn = 0xF8;
        private const int B_SleepFn = 0x100;
        private const int B_Deadline = 0x108;
        private const int B_SpinBudget = 0x110;
        private const int B_Idles = 0x118;
        private const int B_SleepMs = 0x124;

        // stage stamps
        private const ulong StageResident = 0x4100;
        private const ulong StageComplete = 0x4020;
        private const ulong StageExit = 0x40FF;
        private const ulong StageRaw = 0xDEAD;

        private const uint CmdRun = 1;
        private const uint CmdExit = 0xFF;
        private const uint MagicExitCode = 0x4200CAFE;

        // ---------------------------------------------------------------- known RVAs (build 70291)
        /// <summary>Global holding the lua_State pointer.</summary>
        private const ulong RvaLuaStatePtr = 0x7B76AB8;
        private const ulong RvaGetField = 0x471A370;
        private const ulong RvaPushLString = 0x471BC20;
        private const ulong RvaPCall = 0x471B5C0;
        /// <summary>The `ret` that ends lua_gettop - the trampoline's return address.</summary>
        private const ulong RvaTrampolineX = 0x471A983;

        /// <summary>
        /// Warm `jmp qword ptr [rcx]` gadgets in module code. The thread start address
        /// must be a module address (the protector checks the address a call returns to,
        /// and the client's own thread bookkeeping expects module code), and [rcx] is
        /// where the payload lives.
        /// </summary>
        private static readonly uint[] GadgetRvas =
        {
            0x7932CD, 0xBD15E3, 0xB125, 0x10A341, 0xFBF06D, 0x143A7DA,
            0x1778AE3, 0x1C1BFDD, 0x23D5878, 0x2539BED, 0x28092F3, 0x280C94A,
        };

        /// <summary>The lua_gettop prologue, used as a "the Lua cluster is decrypted and warm" canary.</summary>
        private const string CanaryHex = "488b512848b8abaaaaaaaaaaaa2a482b";

        /// <summary>
        /// The resident payload (641 bytes, assembled from hunt46). It is position
        /// independent except for one `lea rbp, [rip+...]` that points at its own
        /// trampoline continuation, which is why it must be copied as a whole.
        /// </summary>
        private const string PayloadHex =
            "4889cb4883ec2848c783e00000000000000048c783180100000000000048c783d80000000000000048c783c80000" +
            "000000000048c7437000410000488b83c80000004885c00f84d4010000483dff0000000f841802000048c74370" +
            "014100004c8b73084d8b7e284c89bb9000000048c783c00000000000000048837b40020f832d0100004c89f1ba" +
            "eed8ffff4c8b431048837b4001750c4c8b4b48488b4378ffd0eb06488b4378ffd048c74370024000004d8b6e28" +
            "4c89abc0000000498b45e8488983a0000000410fb745f0668983a800000048837b30000f850c01000048c74370" +
            "0340000048837b200074144c89f1488b53184c8b4338488b8380000000ffd048c74370044000004c8bab8800" +
            "00004c89f1488b53204c8b43284531c94989e44883e4f048c743704a00040048837b6000750541ffd5eb1548" +
            "83ec28488d2d0a000000554c8b5358415241ffe54c89e448c743704b0004004889839800000048c74370054000" +
            "004d8b6e284c89abc000000048837b28017c70498b45e8488983a0000000410fb745f0668983a800000048837b" +
            "28027c52498b45d0488983b0000000410fb745d8668983b8000000eb3948c74370104000004c89f148837b4003" +
            "740a488b53104c8b4348eb08488b53184c8b4350488b4378ffd0488983a000000048c7437011400000498b4628" +
            "4889436848c7437020400000488b83d0000000488983d800000048ff83e000000048c783c800000000000000e9" +
            "1cfeffff48ff8318010000488b83f80000004885c0740dffd0483b83080100007332eb10488b8318010000483b" +
            "83100100007320488b83000100004885c0740d8b8b24010000ffd0e9d3fdfffff390e9ccfdffff48c74370ff40" +
            "00004883c428b8feca0042c3";

        // ---------------------------------------------------------------- P/Invoke
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, IntPtr size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ulong VirtualQueryEx(IntPtr h, IntPtr addr, byte[] buf, ulong len);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr sa, IntPtr stackSize, IntPtr start, IntPtr param, uint flags, out uint tid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr h, out uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetModuleHandleA(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SuspendThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", EntryPoint = "GetThreadContext", SetLastError = true)]
        private static extern bool GetThreadContextRaw(IntPtr thread, IntPtr context);

        [DllImport("kernel32.dll", EntryPoint = "SetThreadContext", SetLastError = true)]
        private static extern bool SetThreadContextRaw(IntPtr thread, IntPtr context);

        /// <summary>
        /// The raw call behind SetThreadContext. Returns an NTSTATUS, which is the ONLY way to
        /// see why a context write failed when the Win32 wrapper leaves the last error at 0 -
        /// exactly what the 2026-10-13 run printed ("SetThreadContext failed: 0"). The third
        /// argument is the context flags; the two-argument form ignores it.
        /// </summary>
        [DllImport("ntdll.dll", SetLastError = false)]
        private static extern int NtSetContextThreadRaw(IntPtr thread, IntPtr context, uint contextFlags);

        private const uint ThreadSuspendResume = 0x0002;
        private const uint ThreadGetContext = 0x0008;
        /// <summary>
        /// 0x0010, NOT 0x0040: 0x0040 is THREAD_QUERY_INFORMATION. 0.7.0 asked for the wrong
        /// right, so OpenThread SUCCEEDED and handed back a handle that may read a context but
        /// never write one - every takeover then died at SetThreadContext (2026-10-13).
        /// </summary>
        private const uint ThreadSetContext = 0x0010;
        private const uint ThreadQueryInformation = 0x0040;
        /// <summary>THREAD_ALL_ACCESS - the fallback when the narrow mask is refused.</summary>
        private const uint ThreadAllAccess = 0x1FFFFF;
        /// <summary>CONTEXT_AMD64 | CONTEXT_CONTROL - fills Rip/Rsp/segments/EFlags.</summary>
        private const uint ContextAmd64Control = 0x00100000u | 0x01u;
        /// <summary>
        /// CONTEXT_AMD64 | CONTROL | INTEGER - the takeover minimum: Rip/Rsp/segments AND the
        /// integer file, which is where the control-block pointer travels (Rcx).
        /// </summary>
        private const uint ContextAmd64ControlInteger = 0x00100000u | 0x01u | 0x02u;
        /// <summary>CONTEXT_AMD64 | CONTROL | INTEGER | FLOATING_POINT - everything a takeover has to give back.</summary>
        private const uint ContextAmd64Full = 0x00100000u | 0x01u | 0x02u | 0x08u;
        private const int ContextSize = 0x4D0;
        private const int ContextOffsetRip = 0xF8;
        private const int ContextOffsetRsp = 0x98;
        private const int ContextOffsetRcx = 0x80;

        // ---- Lua-thread hijack mode (see the region at the end of this file) ----
        private const int HijackStubOffset = 0x1F00;      // in our page, past the control block
        private const int HijackStackSize = 0x10000;      // 64 KB private stack for the hijacked thread
        private static readonly byte[] HijackStub = { 0xF3, 0x90, 0xEB, 0xFC };   // pause; jmp $-2

        // ---------------------------------------------------------------- state
        private readonly object _gate = new object();
        private IntPtr _process;
        private int _pid;
        private ulong _base;
        private ulong _moduleSize;
        private ulong _luaState;
        private ulong _x;
        private ulong _gadget;
        private ulong _page;
        private IntPtr _thread;
        private uint _threadId;
        private ulong _seq;
        private bool _stuck;
        private bool _started;

        // ---- the Lua-thread guard (see GuardEnter) ----
        private IntPtr _luaThread = IntPtr.Zero;      // handle: THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT
        private uint _luaThreadId;
        private List<IntPtr> _guardThreads = new List<IntPtr>();   // every thread seen executing Lua (normally one)
        private List<uint> _guardThreadIds = new List<uint>();
        private ulong _restCi;                        // L->ci while the VM is quiet (base_ci, stable per lua_State)
        private bool _guardHeld;
        private long _guardWaits;                     // times we had to wait for the VM to go quiet
        private long _guardRetries;                   // times a re-check after suspending failed
        private long _guardSuspends;
        private double _guardSuspendMs;
        private IntPtr _ctxBuf;                       // 16-byte aligned CONTEXT buffer for GetThreadContext
        private IntPtr _ctxBufRaw;                    // what AllocHGlobal returned (the aligned pointer is freed through this)
        private long _suspendStartTicks;
        private byte[] _block = new byte[0x200];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastCallMs;

        // ---- Lua-thread hijack mode ----
        private IntPtr _hijackThread = IntPtr.Zero;   // handle: SUSPEND_RESUME | GET_CONTEXT | SET_CONTEXT
        private IntPtr _savedCtxBuf = IntPtr.Zero;    // the client's own context, restored after every command
        private IntPtr _savedCtxRaw = IntPtr.Zero;    // what AllocHGlobal returned (the aligned pointer is freed through this)
        private IntPtr _verifyBuf;                    // scratch buffer for the read-back that proves a write took effect
        private string _hijackReport = "";            // what the handle open + self-test saw (the probe prints it)
        private string _hijackHow = "";               // how the last takeover wrote the context (API + flags)
        private ulong _hijackClientRip;               // where the client's thread was when we took it over
        private int _hijackAttempts;                  // hijack attempts spent on the last command
        private int _hijackSkippedBusyMoments;        // times a moment was skipped because RIP was inside the VM
        private int _hijackSkippedWaitMoments;        // ... and because RIP was outside the game module (parked thread)
        private ulong _stackPage;                     // our private stack for the hijacked thread
        private ulong _stubVa;                        // where the payload's final `ret` parks the thread
        private bool _hijackActive;                   // true while the client's Lua thread is inside our payload
        private bool _stoppedFrozen;                  // a wedge left the client's thread frozen/parked; nothing may be freed
        private bool _guardFrozenStuck;               // the guard is deliberately still holding the freeze

        public LuaBridge64()
        {
            GuardEnabled = true;
            HijackTimeoutMs = 1000;
            HijackWindowMs = 5000;
        }

        /// <summary>Size of the embedded resident payload: 641 bytes for build 70291.</summary>
        public static int PayloadSize { get { return PayloadHex.Length / 2; } }

        public bool IsStarted { get { return _started && !_stuck; } }

        /// <summary>
        /// Where commands run. Set before <see cref="Start"/>: <see cref="LuaExecutionMode.ServerThread"/>
        /// (default) uses the injected resident thread and freezes the client's Lua thread around each
        /// call; <see cref="LuaExecutionMode.LuaThreadHijack"/> runs each command on the client's own
        /// Lua thread, which is the only way to call anything that raises a FrameScript event - the
        /// client asserts FrameScript_IsLuaThread() and terminates itself otherwise (2026-10-13,
        /// MoveForwardStart), and running on that thread also removes the cross-thread VM race.
        /// </summary>
        public LuaExecutionMode ExecutionMode { get; set; }

        /// <summary>True while the client's Lua thread is inside our payload (hijack mode).</summary>
        public bool HijackActive { get { return _hijackActive; } }

        /// <summary>
        /// What the hijack setup saw: which rights the thread handle got, and whether a full
        /// context round-trip (suspend -> read -> write the same context back -> read -> resume)
        /// succeeded on a thread that was still parked. A takeover is only attempted when this
        /// is green, so a machinery failure can no longer be mistaken for a Lua refusal.
        /// </summary>
        public string HijackReport { get { return _hijackReport; } }

        /// <summary>How the last takeover wrote the context (which API, which flags) - "" before the first one.</summary>
        public string HijackHow { get { return _hijackHow; } }

        /// <summary>Quiet moments the last takeover consumed (a takeover that worked is normally 1).</summary>
        public int HijackAttempts { get { return _hijackAttempts; } }

        /// <summary>True when a wedge left the client's thread frozen or parked - the page must not be freed.</summary>
        public bool StoppedFrozen { get { return _stoppedFrozen || _guardFrozenStuck; } }

        /// <summary>How long a hijacked command may take before the bridge gives up (default 1000 ms).</summary>
        public int HijackTimeoutMs { get; set; }

        /// <summary>
        /// How long the takeover may look for a moment with RIP inside the game module (default
        /// 5000 ms). It must be user code: a thread parked in a kernel wait does not get the whole
        /// context applied, which is the 2026-10-13 crash (the payload ran with a stale RCX and
        /// faulted on its own first write).
        /// </summary>
        public int HijackWindowMs { get; set; }

        /// <summary>
        /// Freeze the client's Lua-owning thread for the duration of every call. On by default:
        /// without it a call from our thread races the client's own Lua execution, and the
        /// client dies when both touch the same lua_State (2026-10-13 crash report: the fault is
        /// inside the Lua cluster on our thread, and the 0xC0000028 the client reports is only
        /// its unwinder failing to walk our trampoline, which has no unwind data).
        /// </summary>
        public bool GuardEnabled { get; set; }

        /// <summary>The main Lua thread the guard freezes (0 when it could not be identified).</summary>
        public uint GuardThreadId { get { return _luaThreadId; } }
        /// <summary>Every thread the guard freezes - more than one only if the client ran Lua on several.</summary>
        public string GuardThreadIds
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < _guardThreadIds.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(_guardThreadIds[i]);
                }
                return sb.ToString();
            }
        }
        public long GuardWaits { get { return _guardWaits; } }
        public long GuardRetries { get { return _guardRetries; } }
        public long GuardSuspends { get { return _guardSuspends; } }
        public double GuardSuspendMilliseconds { get { return _guardSuspendMs; } }
        public int ProcessId { get { return _pid; } }
        public ulong ImageBase { get { return _base; } }
        public ulong LuaState { get { return _luaState; } }
        public ulong Page { get { return _page; } }

        /// <summary>
        /// True when the client's copy of this address is readable right now. The protector maps
        /// every page it has not decrypted yet as NOACCESS, so "unreadable" is the cold-page test
        /// the hunt scripts have used since hunt37 - and a cold handler still holds ciphertext, so
        /// calling it executes garbage (the decoder is not driven by a protection change).
        /// </summary>
        public bool CanRead(ulong address, int count = 16)
        {
            return Read(address, count) != null;
        }

        /// <summary>The first bytes at an address as lower-case hex, or null when the page is cold.</summary>
        public string? ReadHex(ulong address, int count = 16)
        {
            byte[]? b = Read(address, count);
            if (b == null) return null;
            StringBuilder sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// A dump-friendly read: page-sized chunks, so one NOACCESS page in the middle of a range
        /// does not lose the rest of it. Bytes that could not be read come back as 0 and are
        /// counted in <paramref name="unreadable"/> - a cold page therefore shows up as zeros,
        /// which is exactly what a live dump wants to record.
        /// </summary>
        public byte[] ReadDump(ulong address, int count, out int unreadablePages, out int unreadableBlocks)
        {
            byte[] result = new byte[count];
            unreadablePages = 0;
            unreadableBlocks = 0;
            int off = 0;
            while (off < count)
            {
                // chunks end on 4 KB boundaries, so one cold page in the middle of a range can no
                // longer lose the rest of it (the 0.8.0 dump failed every range for exactly that
                // reason - and a range that crosses a page boundary fails as a whole)
                int toPage = 0x1000 - (int)((address + (ulong)off) & 0xFFF);
                int chunk = Math.Min(toPage, count - off);
                byte[]? b = Read(address + (ulong)off, chunk);
                if (b != null)
                {
                    Buffer.BlockCopy(b, 0, result, off, chunk);
                    off += chunk;
                    continue;
                }
                if (chunk == 0x1000) unreadablePages++;
                // a page that refused as a whole may still be readable in pieces (the protector
                // decrypts per function, not per page): try 64-byte blocks before giving up
                for (int sub = 0; sub < chunk; sub += 64)
                {
                    int take = Math.Min(64, chunk - sub);
                    byte[]? piece = Read(address + (ulong)(off + sub), take);
                    if (piece == null) unreadableBlocks++;
                    else Buffer.BlockCopy(piece, 0, result, off + sub, take);
                }
                off += chunk;
            }
            return result;
        }

        public ulong Gadget { get { return _gadget; } }
        public double LastCallMilliseconds { get { return _lastCallMs; } }
        /// <summary>Commands the payload has actually served (from the payload's own counter).</summary>
        public ulong ServedCalls { get { return ReadU64(_page + OffCtrl + (ulong)B_Calls); } }

        /// <summary>The address the payload must be entered from: a warm `jmp qword ptr [rcx]`.</summary>
        public ulong TrampolineAddress { get { return _x; } }

        // ---------------------------------------------------------------- attach
        /// <summary>Finds the first process whose name starts with <paramref name="processName"/>.</summary>
        public static int FindProcessId(string processName)
        {
            Process[] list = Process.GetProcesses();
            try
            {
                for (int i = 0; i < list.Length; i++)
                {
                    try
                    {
                        if (list[i].ProcessName.StartsWith(processName, StringComparison.OrdinalIgnoreCase))
                            return list[i].Id;
                    }
                    catch
                    {
                        // a process that died while we enumerated it
                    }
                }
            }
            finally
            {
                for (int i = 0; i < list.Length; i++)
                {
                    try { list[i].Dispose(); } catch { }
                }
            }
            return 0;
        }

        /// <summary>Opens the target and verifies that this really is a warm build 70291 Lua cluster.</summary>
        public void Attach(int pid)
        {
            if (pid <= 0) throw new ArgumentOutOfRangeException("pid");
            IntPtr h = OpenProcess(0x1F0FFF, false, pid);
            if (h == IntPtr.Zero)
                throw new InvalidOperationException("OpenProcess(" + pid + ") failed: " + Marshal.GetLastWin32Error());
            _process = h;
            _pid = pid;
            _guardSearchFailed = false;         // a fresh attach may search for the Lua thread again

            Process target = Process.GetProcessById(pid);
            _base = (ulong)target.MainModule.BaseAddress.ToInt64();
            _moduleSize = (ulong)target.MainModule.ModuleMemorySize;
            if (_moduleSize < 0x8000000)
                throw new InvalidOperationException("module size 0x" + _moduleSize.ToString("X") + " does not look like build 70291");

            // canary: the Lua cluster must be decrypted (the shipped pages are ciphertext)
            byte[] canary = Read(_base + 0x471A960, 16);
            if (canary == null || ToHex(canary) != CanaryHex)
                throw new InvalidOperationException(
                    "the Lua cluster is not warm (lua_gettop at module+0x471A960 does not match the decoded dump) - " +
                    "log the character in and run this again");

            _luaState = ReadU64(_base + RvaLuaStatePtr);
            if (_luaState < 0x10000 || _luaState > 0x00007FFFFFFF0000)
                throw new InvalidOperationException("lua_State at module+0x7B76AB8 is 0x" + _luaState.ToString("X"));

            _x = _base + RvaTrampolineX;
            byte[] shape = Read(_x - 5, 6);
            if (shape == null || shape[0] != 0xE8 || shape[5] != 0xC3)
                throw new InvalidOperationException(
                    "the trampoline address module+0x471A983 no longer has the `e8 ...... c3` shape the protector wants");
        }

        // ---------------------------------------------------------------- verification
        /// <summary>
        /// Every check <see cref="Start"/> makes, without writing to the client and without
        /// starting a thread: the gadget hunt and the two kernel32 lookups. Attach() has already
        /// verified the Lua canary and the trampoline shape.
        /// </summary>
        public string[] Verify()
        {
            if (_luaState == 0) throw new InvalidOperationException("Attach() first");
            List<string> lines = new List<string>();

            ulong gadget = FindGadget();
            lines.Add(gadget == 0
                ? "gadget      : NONE of the " + GadgetRvas.Length + " candidate `jmp qword ptr [rcx]` sites is warm"
                : "gadget      : 0x" + gadget.ToString("X") + " (module+0x" + (gadget - _base).ToString("X") + "), `ff 21` verified live");

            ulong tick = ResolveKernelFunction("GetTickCount64", true);
            ulong sleep = ResolveKernelFunction("Sleep", true);

            // Identify the client's Lua thread *before* our payload thread exists, so the
            // instruction-pointer sampling cannot pick our own thread by mistake. ONE search
            // only: this used to run twice, and the second run could come back empty after the
            // first had succeeded, leaving the guard with nothing to freeze while the log said
            // it had found the thread.
            uint tid = 0;
            if (GuardEnabled)
            {
                tid = FindLuaThread();
                if (tid == 0)
                    throw new InvalidOperationException(
                        "could not identify the client's Lua thread - nothing was written" + Environment.NewLine + ThreadReport);
                int waited = 0;
                while (!IsLuaQuiet() && waited < 3000)
                {
                    Thread.Sleep(25);
                    waited += 25;
                }
                if (!IsLuaQuiet())
                    throw new InvalidOperationException(
                        "the client's Lua state was busy for 3 s (L->top != L->base) - start the bridge from a quiet moment, e.g. standing still in the world");
            }
            lines.Add(tick == 0
                ? "tick        : kernel32!GetTickCount64 could not be verified - the payload would spin on a budget instead"
                : "tick        : 0x" + tick.ToString("X") + " (kernel32!GetTickCount64, bytes match ours)");
            lines.Add(sleep == 0
                ? "sleep       : kernel32!Sleep could not be verified - the payload would pause-spin while idle"
                : "sleep       : 0x" + sleep.ToString("X") + " (kernel32!Sleep, bytes match ours)");
            lines.Add("trampoline  : mode 1, X = 0x" + _x.ToString("X") + " (module+0x471A983, `e8 ... c3`)");

            if (GuardEnabled)
            {
                lines.Add(tid == 0
                    ? "lua thread  : NOT FOUND - no thread was ever seen inside the Lua cluster (0x4700000..0x4740000)"
                    : "lua thread  : tid " + GuardThreadIds + (GuardThreadSeenInLua
                        ? " (seen executing Lua during the sample - the guard will freeze " + (GuardThreadSeenInLua && _guardThreadIds.Count > 1 ? "these threads" : "this thread") + " for every call)"
                        : " (best guess: none showed a Lua-cluster RIP during the sample, so this is simply the busiest thread in module code)") +
                      "   [--lua-thread " + tid + " forces this pick]");
                lines.Add("lua quiet   : " + (IsLuaQuiet()
                    ? "yes (L->top == L->base, L->ci = 0x" + _restCi.ToString("X") + ") - a call would be safe right now"
                    : "NO (the client is mid-Lua) - the guard would wait for it"));
            }
            else
            {
                lines.Add("lua thread  : guard disabled (--no-guard) - calls will race the client's own Lua execution");
            }

            if (gadget == 0) lines.Add("verdict     : would NOT start (no gadget)");
            else if (tick == 0 || sleep == 0) lines.Add("verdict     : would start, with a spin fallback instead of Sleep/GetTickCount64");
            else lines.Add("verdict     : everything resolves - Start() should get a resident thread");
            return lines.ToArray();
        }

        // ---------------------------------------------------------------- start / stop
        /// <summary>
        /// Allocates the bridge page, writes the payload and starts the one resident thread.
        /// </summary>
        /// <param name="serveSeconds">The payload's own deadline; it exits by itself after this.</param>
        /// <param name="idleSleepMs">How long it sleeps between mailbox polls while idle.</param>
        public void Start(int serveSeconds = 120, int idleSleepMs = 1)
        {
            if (_luaState == 0) throw new InvalidOperationException("Attach() first");
            if (_started) return;

            // Identify the client's Lua thread BEFORE our payload thread exists, so the
            // instruction-pointer sampling cannot pick our own thread by mistake - and so that
            // failing to identify it writes nothing at all into the client. 0.3.0 and 0.4.0's
            // Start() never ran this search (only Verify()/--check did), so every normal run
            // reached its first call with an empty pick, printed a blank thread id and refused.
            if ((GuardEnabled || ExecutionMode == LuaExecutionMode.LuaThreadHijack) && !EnsureGuardThread())
                throw new InvalidOperationException(GuardFailureHelp());

            _page = (ulong)VirtualAllocEx(_process, IntPtr.Zero, new IntPtr(PageSize), 0x1000 | 0x2000, 0x40).ToInt64();
            if (_page == 0) throw new InvalidOperationException("VirtualAllocEx failed: " + Marshal.GetLastWin32Error());

            byte[] payload = FromHex(PayloadHex);
            if (payload.Length != 641)
                throw new InvalidOperationException("the embedded payload is " + payload.Length + " bytes, expected 641");
            Write(_page + OffPayload, payload);

            // FindGadget() returns an ABSOLUTE address (image base already added) - it is the
            // thread's start address as-is. Adding the base again produces an address outside
            // the module (and outside the canonical range), which is what v0.1.0 did: the
            // thread started, faulted on its first instruction and the payload never ran.
            _gadget = FindGadget();
            if (_gadget < _base || _gadget >= _base + _moduleSize)
                throw new InvalidOperationException(
                    "no warm `ff 21` gadget found in module code (looked at " + GadgetRvas.Length + " candidates)");
            if ((ReadU32(_gadget) & 0xFFFFu) != 0x21FFu)
                throw new InvalidOperationException("the gadget at 0x" + _gadget.ToString("X") + " is no longer `ff 21`");

            ulong tick = ResolveKernelFunction("GetTickCount64", true);
            ulong sleep = ResolveKernelFunction("Sleep", true);

            // control block: the payload's own layout, exactly as the hunt46 driver filled it
            Array.Clear(_block, 0, _block.Length);
            PutU64(_block, 0x00, _page + OffPayload);      // [ctrl] is the gadget's jump target
            PutU64(_block, B_L, _luaState);
            PutU64(_block, B_NamePtr, _page + OffName);
            PutU64(_block, B_ArgPtr, _page + OffArg);
            PutU64(_block, B_TrampX, _x);
            PutU64(_block, B_TrampMode, 1);                // validator live: the two-hop return
            PutU64(_block, B_Func, _base + RvaGetField);
            PutU64(_block, B_PushLString, _base + RvaPushLString);
            PutU64(_block, B_PCall, _base + RvaPCall);
            PutU64(_block, B_TickFn, tick);
            PutU64(_block, B_SleepFn, sleep);
            PutU64(_block, B_Deadline, GetTickCount64() + (ulong)(serveSeconds * 1000));
            PutU64(_block, B_SpinBudget, (ulong)Math.Max(serveSeconds, 10) * 20000000UL);   // ~a spin loop's rate; the deadline governs when a clock resolved
            PutU32(_block, B_SleepMs, (uint)idleSleepMs);
            Write(_page + OffCtrl, _block);

            // read-back verification, the same rule the hunt scripts have used since hunt33:
            // nothing is allowed to point a thread at bytes we have not confirmed arrived
            byte[] payloadBack = Read(_page + OffPayload, 32);
            if (payloadBack == null || !Equal(payloadBack, payload, 32))
                throw new InvalidOperationException("the payload did not read back intact at 0x" +
                    (_page + OffPayload).ToString("X"));
            byte[] blockBack = Read(_page + OffCtrl, 0x30);
            byte[] blockHead = new byte[0x30];
            Buffer.BlockCopy(_block, 0, blockHead, 0, 0x30);
            if (blockBack == null || !Equal(blockBack, blockHead, 0x30))
                throw new InvalidOperationException("the control block did not read back intact at 0x" +
                    (_page + OffCtrl).ToString("X"));

            if (ExecutionMode == LuaExecutionMode.LuaThreadHijack)
            {
                StartHijackMode(serveSeconds);                 // no resident thread: the Lua thread runs the payload per command
                return;
            }

            uint tid;
            _thread = CreateRemoteThread(_process, IntPtr.Zero, IntPtr.Zero,
                new IntPtr((long)_gadget),                     // absolute, already includes the image base
                new IntPtr((long)(_page + OffCtrl)), 0, out tid);
            if (_thread == IntPtr.Zero)
                throw new InvalidOperationException("CreateRemoteThread(start 0x" + _gadget.ToString("X") +
                    ") failed: " + Marshal.GetLastWin32Error());
            _threadId = tid;
            _started = true;

            // the payload announces itself with stage 0x4100 once it is polling
            ulong stage = 0;
            int waited = 0;
            while (waited < 5000)
            {
                stage = ReadU64(_page + OffCtrl + (ulong)B_Stage);
                if (stage == StageResident) break;
                Thread.Sleep(10);
                waited += 10;
            }
            if (stage != StageResident)
            {
                uint code;
                GetExitCodeThread(_thread, out code);
                throw new InvalidOperationException(string.Format(
                    "the payload did not reach its idle loop (stage 0x{0:X}, CMD {1}, thread {2} exit code {3}{4})",
                    stage, ReadU64(_page + OffCtrl + (ulong)B_Cmd), _threadId, code,
                    code == 259 ? ", 259 = the payload is still running (a command may simply be slow)"
                                : ", the payload thread is dead - the client has probably died with it"));
            }
        }

        /// <summary>
        /// Asks the payload to exit and frees the page. Safe: the thread has left before the
        /// page is released (freeing a page under a live payload is what killed the client in hunt43).
        /// </summary>
        public void Stop()
        {
            if (!_started) return;
            lock (_gate)
            {
                if (_hijackActive)
                {
                    // the client's Lua thread is inside our payload: touching the page (or its
                    // context) now is what killed the client in hunt43. Report and stop.
                    _stuck = true;
                    _stoppedFrozen = true;
                    return;
                }
                if (_guardHeld) GuardExit();          // before anything else: never leave their thread frozen
                if (!_stuck && ExecutionMode == LuaExecutionMode.ServerThread)
                {
                    WriteU64(_page + OffCtrl + (ulong)B_Cmd, CmdExit);
                    if (WaitForSingleObject(_thread, 5000) == 0)
                    {
                        uint code;
                        GetExitCodeThread(_thread, out code);
                        if (code != MagicExitCode)
                            throw new InvalidOperationException("the payload exited with 0x" + code.ToString("X"));
                        _started = false;
                    }
                    else
                    {
                        _stuck = true;   // never TerminateThread: that is what killed the client in hunts 40/41
                    }
                }
            }
            if (!_stuck && _thread != IntPtr.Zero)
            {
                CloseHandle(_thread);
                _thread = IntPtr.Zero;
            }
            if (!_stuck && _stackPage != 0)
            {
                VirtualFreeEx(_process, new IntPtr((long)_stackPage), IntPtr.Zero, 0x8000);
                _stackPage = 0;
            }
            if (!_stuck && _page != 0)
            {
                VirtualFreeEx(_process, new IntPtr((long)_page), IntPtr.Zero, 0x8000);
                _page = 0;
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { ReleaseThreadHandle(); } catch { }
            if (_ctxBuf != IntPtr.Zero)
            {
                // free what AllocHGlobal returned: the value GetThreadContext uses is padded up
                // to a 16-byte boundary and freeing that interior pointer would not release it
                Marshal.FreeHGlobal(_ctxBufRaw);
                _ctxBuf = IntPtr.Zero;
                _ctxBufRaw = IntPtr.Zero;
            }
            if (_savedCtxBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_savedCtxRaw);
                _savedCtxBuf = IntPtr.Zero;
                _savedCtxRaw = IntPtr.Zero;
            }
            if (_verifyBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_verifyBuf);
                _verifyBuf = IntPtr.Zero;
            }
            if (_process != IntPtr.Zero)
            {
                CloseHandle(_process);
                _process = IntPtr.Zero;
            }
        }

        // ---------------------------------------------------------------- the call
        /// <summary>
        /// Calls a global Lua function by name, e.g. <c>Call("UnitHealth", "player")</c>.
        /// Returns the results (1 or 2 of them). Throws if the client's VM reports an error.
        /// </summary>
        public LuaValue[] Call(string function, string arg = null, int nresults = 1, int timeoutMs = 8000)
        {
            if (!_started) throw new InvalidOperationException("Start() first");
            if (function == null) throw new ArgumentNullException("function");
            if (nresults < 1 || nresults > 2) throw new ArgumentOutOfRangeException("nresults", "the payload captures 1 or 2 results");

            lock (_gate)
            {
                if (_stuck)
                    throw new InvalidOperationException("a previous command never completed - the client must be restarted");
                if (_page == 0)
                    throw new InvalidOperationException("the bridge page is gone");

                if (ExecutionMode == LuaExecutionMode.LuaThreadHijack)
                {
                    int cap = HijackTimeoutMs > 0 ? HijackTimeoutMs : 1000;
                    return CallHijacked(function, arg, nresults, Math.Min(timeoutMs, cap));
                }

                GuardEnter();
                try
                {
                    // with the client's Lua thread frozen, the command must not dawdle
                    return CallCore(function, arg, nresults, GuardEnabled ? Math.Min(timeoutMs, 1000) : timeoutMs);
                }
                finally
                {
                    GuardExit();
                }
            }
        }

        /// <summary>
        /// Everything a command puts in the control block before it is issued - shared by the
        /// resident-thread path and the hijack path so the two cannot drift apart.
        /// </summary>
        private void WriteCommandBlock(string function, string arg, int nresults)
        {
            if (function.Length > 0x7F)
                throw new ArgumentOutOfRangeException("function", "the name slot holds 127 bytes (page+0x1200..page+0x1280)");
            if (!string.IsNullOrEmpty(arg) && arg.Length > 0xC00)
                throw new ArgumentOutOfRangeException("arg", "the argument slot holds 3072 bytes (page+0x1280..page+0x1F00, where the hijack stub lives)");
            bool hasArg = !string.IsNullOrEmpty(arg);
            byte[] name = Encoding.ASCII.GetBytes(function);
            Write(_page + OffName, Terminated(name));
            if (hasArg)
            {
                byte[] argBytes = Encoding.ASCII.GetBytes(arg);
                Write(_page + OffArg, Terminated(argBytes));
            }

            // patch the current block: the payload owns the stage/counters, so read it first
            byte[] live = Read(_page + OffCtrl, 0x200) ?? _block;
            Array.Copy(live, _block, 0x200);
            PutU64(_block, B_Nargs, hasArg ? 1UL : 0UL);
            PutU64(_block, B_Nresults, (ulong)nresults);
            PutU64(_block, B_SkipPcall, 0);
            PutU64(_block, B_ArgLen, hasArg ? (ulong)arg.Length : 0UL);
            PutU64(_block, B_Variant, 0);              // 0 = getfield(globals, name) + pcall
            PutU64(_block, B_NameLen, (ulong)function.Length);
            PutU64(_block, B_RawR8, 0);
            PutU64(_block, B_PcallStatus, StageRaw);
            PutU64(_block, B_Res1Type, StageRaw);
            PutU64(_block, B_Res2Type, StageRaw);
            PutU64(_block, B_Stage, 0);
            PutU64(_block, B_Func, _base + RvaGetField);
            PutU64(_block, B_NamePtr, _page + OffName);
            PutU64(_block, B_ArgPtr, _page + OffArg);
            Write(_page + OffCtrl, _block);
        }

        /// <summary>The results out of a finished command block (both paths use the same layout).</summary>
        private LuaValue[] ReadResults(int nresults, int status)
        {
            byte[] block = Read(_page + OffCtrl, 0x200);
            if (block == null) throw new InvalidOperationException("the control block became unreadable");
            LuaValue[] results = new LuaValue[nresults];
            results[0] = Value(block, B_Res1, B_Res1Type);
            if (nresults == 2) results[1] = Value(block, B_Res2, B_Res2Type);
            return results;
        }

        private LuaValue[] CallCore(string function, string arg, int nresults, int timeoutMs)
        {
            {
                WriteCommandBlock(function, arg, nresults);

                _seq++;
                WriteU64(_page + OffCtrl + (ulong)B_Seq, _seq);
                long t0 = _clock.ElapsedTicks;
                long ms0 = _clock.ElapsedMilliseconds;
                WriteU64(_page + OffCtrl + (ulong)B_Cmd, CmdRun);

                bool done = false;
                while (true)
                {
                    if (ReadU64(_page + OffCtrl + (ulong)B_Ack) == _seq) { done = true; break; }
                    if (_clock.ElapsedMilliseconds - ms0 > timeoutMs) break;
                    Thread.SpinWait(20);
                }
                _lastCallMs = (double)(_clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
                if (!done)
                {
                    _stuck = true;
                    ulong stage = ReadU64(_page + OffCtrl + (ulong)B_Stage);
                    throw new TimeoutException(
                        "the Lua call did not complete within " + timeoutMs + " ms (payload stage 0x" + stage.ToString("X") + ")");
                }

                byte[] block = Read(_page + OffCtrl, 0x200);
                if (block == null) throw new InvalidOperationException("the control block became unreadable");
                ulong stageDone = GetU64(block, B_Stage);
                ulong pc = GetU64(block, B_PcallStatus);
                ulong topBefore = GetU64(block, B_TopBefore);
                ulong topAfter = GetU64(block, B_TopAfter);

                if (stageDone == StageComplete && pc != StageRaw)
                {
                    // stack discipline: lua_pcall consumed function + args and pushed the results
                    ulong expected = topBefore + (ulong)(0x18 * nresults);
                    if (topAfter == expected)
                        WriteU64(_luaState + 0x28, topBefore);
                }

                if (stageDone != StageComplete)
                    throw new InvalidOperationException("the payload stopped at stage 0x" + stageDone.ToString("X"));

                int status = unchecked((int)(uint)pc);
                if (status != 0)
                {
                    LuaValue err = Value(block, B_Res1, B_Res1Type);
                    throw new InvalidOperationException(function + "(" + (arg ?? "") + ") failed: status " + status +
                        (err.Type == LuaType.String ? " error=\"" + err.Text + "\"" : string.Empty));
                }

                return ReadResults(nresults, status);
            }
        }

        /// <summary>Convenience wrapper for the common one-number case.</summary>
        public double CallNumber(string function, string arg = null)
        {
            LuaValue[] v = Call(function, arg, 1);
            if (v[0].Type != LuaType.Number)
                throw new InvalidOperationException(function + " returned " + v[0].Type + ", not a number");
            return v[0].Number;
        }

        /// <summary>Convenience wrapper for the common one-string case.</summary>
        public string? CallString(string function, string arg = null)
        {
            LuaValue[] v = Call(function, arg, 1);
            if (v[0].Type == LuaType.Nil) return null;
            if (v[0].Type != LuaType.String)
                throw new InvalidOperationException(function + " returned " + v[0].Type + ", not a string");
            return v[0].Text;
        }

        /// <summary>Convenience wrapper for the common one-boolean case.</summary>
        public bool CallBoolean(string function, string arg = null)
        {
            LuaValue[] v = Call(function, arg, 1);
            if (v[0].Type == LuaType.Nil) return false;
            if (v[0].Type != LuaType.Boolean)
                throw new InvalidOperationException(function + " returned " + v[0].Type + ", not a boolean");
            return v[0].Boolean;
        }

        private LuaValue Value(byte[] block, int valueOff, int typeOff)
        {
            LuaValue v = new LuaValue();
            v.RawType = (int)GetU16(block, typeOff);
            v.Type = (LuaType)(v.RawType & 0xFF);
            ulong raw = GetU64(block, valueOff);
            v.Pointer = raw;
            switch (v.Type)
            {
                case LuaType.Number:
                    v.Number = BitConverter.ToDouble(block, valueOff);
                    break;
                case LuaType.Boolean:
                    v.Boolean = raw != 0;
                    break;
                case LuaType.String:
                    v.Text = ReadLuaString(raw);
                    break;
            }
            return v;
        }

        /// <summary>
        /// Reads a TString: length at +0x18, characters at +0x20 (verified against the live
        /// dumps: "Tena" -> 4, "HUNTER" -> 6, "Player-70-00000005" -> 18).
        /// </summary>
        private string? ReadLuaString(ulong tstring)
        {
            if (tstring < 0x10000 || tstring > 0x00007FFFFFFF0000) return null;
            uint length = ReadU32(tstring + 0x18);
            if (length == 0 || length > 4096) return null;
            byte[] data = Read(tstring + 0x20, (int)length);
            if (data == null) return null;
            return Encoding.ASCII.GetString(data);
        }

        // ---------------------------------------------------------------- the Lua-thread guard
        /// <summary>One thread of the client, as seen while looking for the one that runs Lua.</summary>
        private sealed class ThreadSample
        {
            public uint Id;
            public IntPtr Handle = IntPtr.Zero;
            public ProcessThread? Proc;
            public double CpuMs = -1;        // TotalProcessorTime at the start of the window
            public double CpuDeltaMs = -1;   // how much CPU it burned during the window
            public int Reads;                // GetThreadContext calls that returned a context
            public int ReadFailed;
            public int SuspendFailed;
            public int LuaHits;
            public int ModuleHits;
            public bool Kept;                // the guard keeps this handle open
            public string? Error;
            public List<ulong> First = new List<ulong>();
        }

        /// <summary>
        /// Finds the thread(s) that run the client's Lua VM.
        ///
        /// Two independent signals, because either one alone can lie:
        ///   1. CPU time - the client's frame thread runs the game loop, and the game loop is
        ///      what executes every Lua script, so it is the busiest thread in the process.
        ///      This needs nothing but ProcessThread and cannot fail.
        ///   2. instruction pointers - suspend each thread for a few microseconds, read its
        ///      CONTEXT_CONTROL and count how often RIP lands in the Lua cluster
        ///      (module+0x4700000..0x4740000). This is what turns the guess into a fact.
        ///
        /// Threads seen inside the Lua cluster are frozen around every command; when nobody
        /// showed up there, the busiest ones are frozen instead and <see cref="GuardThreadSeenInLua"/>
        /// stays false, so <see cref="ThreadReport"/> can say that the pick was a guess.
        /// </summary>
        public uint FindLuaThread(int milliseconds = 2000)
        {
            if (_luaState == 0) throw new InvalidOperationException("Attach() first");
            if (_ctxBuf == IntPtr.Zero)
            {
                _ctxBufRaw = Marshal.AllocHGlobal(ContextSize + 16);
                _ctxBuf = Align16(_ctxBufRaw);
            }

            ReleaseThreadHandle();
            _guardThreads.Clear();
            _guardThreadIds.Clear();
            _luaThreadSeenLua = false;
            _threadReport = "";

            ulong luaLow = _base + 0x4700000UL;
            ulong luaHigh = _base + 0x4740000UL;
            ulong moduleHigh = _base + _moduleSize;

            List<ThreadSample> all = new List<ThreadSample>();
            Process target = Process.GetProcessById(_pid);
            foreach (ProcessThread t in target.Threads)
            {
                ThreadSample sample = new ThreadSample();
                try { sample.Id = (uint)t.Id; } catch { continue; }
                sample.Proc = t;
                try { sample.CpuMs = t.TotalProcessorTime.TotalMilliseconds; } catch { sample.CpuMs = -1; }
                all.Add(sample);
            }

            int openFail = 0;
            int lastErr = 0;
            foreach (ThreadSample sample in all)
            {
                IntPtr h = OpenThread(ThreadSuspendResume | ThreadGetContext, false, sample.Id);
                if (h == IntPtr.Zero)
                {
                    lastErr = Marshal.GetLastWin32Error();
                    h = OpenThread(0x1FFFFFu, false, sample.Id);            // THREAD_ALL_ACCESS
                }
                if (h == IntPtr.Zero)
                {
                    lastErr = Marshal.GetLastWin32Error();
                    openFail++;
                    sample.Error = "OpenThread err " + lastErr;
                    continue;
                }
                sample.Handle = h;
            }

            Stopwatch clock = Stopwatch.StartNew();
            int rounds = 0;
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                rounds++;
                for (int i = 0; i < all.Count; i++)
                {
                    ThreadSample sample = all[i];
                    if (sample.Handle == IntPtr.Zero) continue;
                    if (SuspendThread(sample.Handle) == 0xFFFFFFFFu) { sample.SuspendFailed++; continue; }
                    Marshal.WriteInt32(_ctxBuf, 0x30, unchecked((int)ContextAmd64Control));
                    bool ok = GetThreadContextRaw(sample.Handle, _ctxBuf);
                    ResumeThread(sample.Handle);
                    if (!ok) { sample.ReadFailed++; continue; }
                    sample.Reads++;
                    ulong rip = (ulong)Marshal.ReadInt64(_ctxBuf, ContextOffsetRip);
                    if (rip >= luaLow && rip < luaHigh) sample.LuaHits++;
                    if (rip >= _base && rip < moduleHigh) sample.ModuleHits++;
                    if (sample.First.Count < 4) sample.First.Add(rip);
                }
                Thread.Sleep(1);
            }

            foreach (ThreadSample sample in all)
            {
                if (sample.Proc == null || sample.CpuMs < 0) continue;
                try { sample.CpuDeltaMs = sample.Proc.TotalProcessorTime.TotalMilliseconds - sample.CpuMs; }
                catch { sample.CpuDeltaMs = -1; }
            }

            all.Sort(delegate(ThreadSample a, ThreadSample b)
            {
                if (a.LuaHits != b.LuaHits) return b.LuaHits.CompareTo(a.LuaHits);
                return b.CpuDeltaMs.CompareTo(a.CpuDeltaMs);
            });

            List<ThreadSample> chosen = new List<ThreadSample>();
            foreach (ThreadSample sample in all)
                if (sample.LuaHits > 0 && chosen.Count < 4) chosen.Add(sample);
            bool confirmed = chosen.Count > 0;

            if (!confirmed)
            {
                // nobody was caught inside the Lua cluster during the window: freeze the busiest
                // threads instead, which is the frame thread in practice
                foreach (ThreadSample sample in all)
                    if (chosen.Count < 3 && sample.CpuDeltaMs > 1.0) chosen.Add(sample);
                if (chosen.Count == 0)
                    foreach (ThreadSample sample in all)
                        if (chosen.Count < 1 && sample.Handle != IntPtr.Zero) chosen.Add(sample);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("sampled ").Append(all.Count).Append(" threads for ").Append(milliseconds)
              .Append(" ms in ").Append(rounds).Append(" rounds; ");
            if (openFail == 0) sb.Append("all thread handles opened");
            else sb.Append(openFail).Append(" of ").Append(all.Count).Append(" threads could NOT be opened (last Win32 error ").Append(lastErr).Append(")");
            sb.Append("\n            tid       cpu+ms   reads  module    lua   first RIPs");
            int shown = 0;
            foreach (ThreadSample sample in all)
            {
                if (shown >= 12 && sample.LuaHits == 0) break;
                shown++;
                sb.Append("\n            ").Append(sample.Id.ToString().PadRight(10));
                sb.Append((sample.CpuDeltaMs < 0 ? "?" : sample.CpuDeltaMs.ToString("F0")).PadLeft(7));
                sb.Append(sample.Reads.ToString().PadLeft(8));
                sb.Append(sample.ModuleHits.ToString().PadLeft(8));
                sb.Append(sample.LuaHits.ToString().PadLeft(7));
                sb.Append("   ");
                for (int k = 0; k < sample.First.Count && k < 3; k++)
                {
                    ulong rip = sample.First[k];
                    if (rip >= _base && rip < moduleHigh) sb.Append("module+0x").Append((rip - _base).ToString("X"));
                    else sb.Append("0x").Append(rip.ToString("X"));
                    sb.Append(' ');
                }
                if (sample.Error != null) sb.Append('(').Append(sample.Error).Append(')');
                if (sample.ReadFailed > 0) sb.Append('[').Append(sample.ReadFailed).Append(" context reads failed]");
                if (sample.SuspendFailed > 0) sb.Append('[').Append(sample.SuspendFailed).Append(" suspends failed]");
            }
            sb.Append("\n            -> ");
            if (confirmed) sb.Append("the Lua thread was SEEN (RIP inside module+0x4700000..0x4740000)");
            else sb.Append("no thread was caught inside the Lua cluster - the pick is a CPU-time guess");
            sb.Append("; freezing: ");
            for (int k = 0; k < chosen.Count; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append(chosen[k].Id);
            }
            if (chosen.Count == 0) sb.Append("(nothing - the guard cannot protect a call)");

            _luaThreadSeenLua = confirmed;
            _luaThreadId = chosen.Count > 0 ? chosen[0].Id : 0;
            for (int k = 0; k < chosen.Count; k++)
            {
                chosen[k].Kept = true;
                _guardThreads.Add(chosen[k].Handle);
                _guardThreadIds.Add(chosen[k].Id);
            }
            _luaThread = _guardThreads.Count > 0 ? _guardThreads[0] : IntPtr.Zero;

            foreach (ThreadSample sample in all)
                if (sample.Handle != IntPtr.Zero && !sample.Kept) CloseHandle(sample.Handle);

            _threadReport = sb.ToString();

            // hygiene: the Process/ProcessThread objects each hold a handle; the guard keeps its
            // own OpenThread handles (_guardThreads) and those stay open on purpose
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Proc == null) continue;
                try { all[i].Proc.Dispose(); } catch { }
            }
            try { target.Dispose(); } catch { }

            return _luaThreadId;
        }

        /// <summary>Freezes an explicitly given thread instead of the automatic pick (--lua-thread N).</summary>
        public bool UseLuaThread(uint tid)
        {
            IntPtr h = OpenThread(ThreadSuspendResume | ThreadGetContext, false, tid);
            if (h == IntPtr.Zero) h = OpenThread(0x1FFFFFu, false, tid);
            if (h == IntPtr.Zero) return false;
            ReleaseThreadHandle();
            _guardThreads.Add(h);
            _guardThreadIds.Add(tid);
            _luaThread = h;
            _luaThreadId = tid;
            _luaThreadSeenLua = false;
            _guardSearchFailed = false;
            _threadReport = "guard thread forced by --lua-thread: tid " + tid +
                " (its handle was opened and the guard will freeze it for every call)";
            return true;
        }

        /// <summary>True when the thread chosen by <see cref="FindLuaThread"/> was actually seen inside the Lua cluster.</summary>
        public bool GuardThreadSeenInLua { get { return _luaThreadSeenLua; } }

        /// <summary>What the last FindLuaThread() saw, one line per thread (for --check and the log).</summary>
        public string ThreadReport { get { return _threadReport; } }

        /// <summary>True when the guard has a thread to freeze (or the guard is switched off).</summary>
        public bool GuardReady { get { return !GuardEnabled || _guardThreads.Count > 0; } }

        /// <summary>
        /// Runs the thread search unless one already succeeded or already failed. Start() calls
        /// this before it writes anything, and the guard calls it as a backstop for callers that
        /// build the bridge themselves. Never searched twice: the window costs milliseconds of
        /// every thread in the client, so a failure is remembered.
        /// </summary>
        public bool EnsureGuardThread()
        {
            if (ExecutionMode != LuaExecutionMode.LuaThreadHijack && !GuardEnabled) return true;
            if (_guardThreads.Count > 0) return true;
            if (_guardSearchFailed) return false;
            FindLuaThread();
            if (_guardThreads.Count > 0) return true;
            _guardSearchFailed = true;
            return false;
        }

        /// <summary>The refusal text: why the guard will not call, what the sampling saw, and the fix.</summary>
        public string GuardFailureHelp()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("the client's Lua thread could not be identified, so a call would race it.");
            sb.Append(Environment.NewLine).Append("What the sampling window saw (suspend -> CONTEXT_CONTROL -> resume):");
            sb.Append(Environment.NewLine);
            sb.Append(_threadReport.Length > 0 ? _threadReport : "(the search produced no table at all)");
            sb.Append(Environment.NewLine);
            sb.Append("Nothing was called and nothing was written. Fixes, best first:");
            sb.Append(Environment.NewLine).Append("  1. LuaBridgeProbe --check   prints the full table; the tid with lua hits is the one to freeze");
            sb.Append(Environment.NewLine).Append("  2. run with --lua-thread <tid>   (UseLuaThread(tid) freezes exactly that thread)");
            sb.Append(Environment.NewLine).Append("  3. GuardEnabled = false takes the race deliberately - only to reproduce a crash");
            return sb.ToString();
        }

        private bool _luaThreadSeenLua;
        private bool _guardSearchFailed;
        private string _threadReport = "";

        /// <summary>L->top == L->base and L->ci at its resting value: no Lua call is in flight.</summary>
        private bool IsLuaQuiet()
        {
            ulong top = ReadU64(_luaState + 0x28);
            if (top == 0) return false;
            if (top != ReadU64(_luaState + 0x30)) return false;
            ulong ci = ReadU64(_luaState + 0x40);
            if (_restCi == 0) { _restCi = ci; return ci != 0; }
            return ci == _restCi;
        }

        private bool IsRipInLuaCluster(IntPtr thread)
        {
            if (_ctxBuf == IntPtr.Zero) return false;
            Marshal.WriteInt32(_ctxBuf, 0x30, unchecked((int)ContextAmd64Control));
            if (!GetThreadContextRaw(thread, _ctxBuf)) return false;
            ulong rip = (ulong)Marshal.ReadInt64(_ctxBuf, ContextOffsetRip);
            return rip >= _base + 0x4700000UL && rip < _base + 0x4740000UL;
        }

        private bool AnyGuardThreadInLua()
        {
            for (int i = 0; i < _guardThreads.Count; i++)
                if (IsRipInLuaCluster(_guardThreads[i])) return true;
            return false;
        }

        private bool SuspendGuardThreads()
        {
            for (int i = 0; i < _guardThreads.Count; i++)
            {
                if (SuspendThread(_guardThreads[i]) == 0xFFFFFFFFu)
                {
                    for (int j = 0; j < i; j++) ResumeThread(_guardThreads[j]);
                    return false;
                }
            }
            return true;
        }

        private void ResumeGuardThreads()
        {
            for (int i = 0; i < _guardThreads.Count; i++) ResumeThread(_guardThreads[i]);
        }

        private void GuardEnter()
        {
            if (!GuardEnabled) return;
            if (_guardThreads.Count == 0)
            {
                // a caller that constructed the bridge itself (the bot) may reach a call without
                // Start() having run the search; try once here rather than refuse blind
                if (!EnsureGuardThread())
                    throw new InvalidOperationException(GuardFailureHelp());
            }

            long start = _clock.ElapsedMilliseconds;
            while (true)
            {
                if (IsLuaQuiet())
                {
                    Thread.Sleep(2);                                  // it must stay quiet across a sleep quantum
                    if (IsLuaQuiet() && SuspendGuardThreads())
                    {
                        if (IsLuaQuiet() && !AnyGuardThreadInLua())
                        {
                            _guardHeld = true;
                            _guardSuspends++;
                            _suspendStartTicks = _clock.ElapsedTicks;
                            return;                                   // the VM is ours until GuardExit
                        }
                        ResumeGuardThreads();                         // it moved while we were suspending it
                        _guardRetries++;
                    }
                    else
                    {
                        _guardRetries++;
                    }
                }

                if (_clock.ElapsedMilliseconds - start > 2000)
                    throw new InvalidOperationException(
                        "the client's Lua state never went quiet (L->top != L->base for 2 s) - a loading screen or an addon spin looks like this; nothing was written");
                _guardWaits++;
                Thread.Sleep(1);
            }
        }

        private void GuardExit()
        {
            if (!_guardHeld) return;
            _guardHeld = false;
            if (_stuck)
            {
                // A command timed out while the VM was frozen. Our payload may still be inside the
                // call, so resuming the client's Lua thread would let it run the VM concurrently
                // with an in-flight call - that is the corruption the guard exists to prevent
                // (2026-10-13). Leave it frozen; the client must be restarted.
                _guardFrozenStuck = true;
                _stoppedFrozen = true;
                return;
            }
            ResumeGuardThreads();
            _guardSuspendMs += (double)(_clock.ElapsedTicks - _suspendStartTicks) * 1000.0 / Stopwatch.Frequency;
        }

        private void ReleaseThreadHandle()
        {
            if (_guardHeld) { ResumeGuardThreads(); _guardHeld = false; }
            if (_hijackThread != IntPtr.Zero) { CloseHandle(_hijackThread); _hijackThread = IntPtr.Zero; }
            for (int i = 0; i < _guardThreads.Count; i++) CloseHandle(_guardThreads[i]);
            _guardThreads.Clear();
            _guardThreadIds.Clear();
            _luaThread = IntPtr.Zero;
            _luaThreadId = 0;
        }

        // ======================================================================= hijack mode
        // --------------------------------------------------------------------------------------
        // Why this exists: the 2026-10-13 crash report is
        //
        //     Error: ASSERT(FrameScript_IsLuaThread())
        //     File:  Engine/Source/FrameScript/FrameScriptEvents.cpp, Line 208
        //     ThreadID: 13572   (our payload thread)
        //     stack:  ... lua_pcall -> MoveForwardStart (module+0x23C8C0C) ->
        //             module+0x23C550D -> module+0x23DD429 -> module+0x23DBE47 ->
        //             module+0x1DA4596 (the event dispatcher) -> assert
        //
        // The client requires FrameScript events to be raised from its OWN Lua thread, and it
        // terminates itself when they are raised from anywhere else. Nothing about our payload
        // can fix that; the command has to RUN on that thread. So:
        //
        //   suspend the client's Lua thread at a quiet moment
        //   save its full CONTEXT (we must give it back byte for byte)
        //   set RIP = our payload entry, RCX = the control block,
        //       RSP = a private stack whose top slot holds the stub below
        //   resume it: the payload now runs ON the Lua thread, exactly like a C handler would
        //   drive it through the mailbox as usual, then send CMD = 0xFF so it exits
        //   its final `ret` lands on the stub (pause loop) - never in the game's code
        //   suspend, restore the saved CONTEXT, resume: the game continues where it was
        //
        // Costs the same ~2-5 ms of frozen game time per command as the guard did, but the work
        // happens on the VM's own thread: no race, no assert, and the frame thread's locks are
        // released during the same window they would be during an ordinary C handler call.
        // --------------------------------------------------------------------------------------

        /// <summary>One-time setup for <see cref="LuaExecutionMode.LuaThreadHijack"/> (called by Start).</summary>
        private void StartHijackMode(int serveSeconds)
        {
            _stackPage = (ulong)VirtualAllocEx(_process, IntPtr.Zero, new IntPtr(HijackStackSize), 0x1000 | 0x2000, 0x04).ToInt64();
            if (_stackPage == 0)
                throw new InvalidOperationException("VirtualAllocEx(stack) failed: " + Marshal.GetLastWin32Error());
            _stubVa = _page + HijackStubOffset;
            Write(_stubVa, HijackStub);
            WriteU64(_stackPage + HijackStackSize - 8, _stubVa);   // the payload's final `ret` pops this

            ulong tick = ResolveKernelFunction("GetTickCount64", true);
            ulong sleep = ResolveKernelFunction("Sleep", true);
            PutU64(_block, B_TickFn, tick);
            PutU64(_block, B_SleepFn, sleep);
            PutU64(_block, B_Deadline, GetTickCount64() + 3600000UL);   // an hour: a wedge is detected by the ack, not by this
            PutU64(_block, B_SpinBudget, 0x7FFFFFFFUL);
            PutU32(_block, B_SleepMs, 1);
            Write(_page + OffCtrl, _block);
            byte[] back = Read(_page + OffCtrl, 0x30);
            if (back == null)
                throw new InvalidOperationException("the control block became unreadable after the hijack setup");

            EnsureContextBuffers();

            // Pick the thread that can actually be taken over BEFORE a command depends on it.
            // 0.7.0 opened one thread and found out inside the first command - after it had
            // already written a command block and was ready to point a context at the payload.
            if (_hijackThread == IntPtr.Zero)
                ChooseHijackThread();
            _started = true;
        }

        /// <summary>The aligned CONTEXT buffers a takeover needs (also used by --check).</summary>
        private void EnsureContextBuffers()
        {
            if (_ctxBuf == IntPtr.Zero)
            {
                _ctxBufRaw = Marshal.AllocHGlobal(ContextSize + 16);
                _ctxBuf = Align16(_ctxBufRaw);
            }
            if (_savedCtxBuf == IntPtr.Zero)
            {
                _savedCtxRaw = Marshal.AllocHGlobal(ContextSize + 16);
                _savedCtxBuf = Align16(_savedCtxRaw);
            }
            if (_verifyBuf == IntPtr.Zero)
                _verifyBuf = Align16(Marshal.AllocHGlobal(ContextSize + 16));
        }

        /// <summary>
        /// Runs the takeover's setup checks without starting anything: which candidate threads the
        /// search found, which rights a handle to each one grants, and whether a real CONTEXT
        /// round-trip (suspend -> read -> write the same context back -> read -> resume) succeeds.
        /// --check calls this, so a rights or alignment problem is visible BEFORE a command
        /// depends on it - 0.7.0 discovered its own access-mask bug inside the first command.
        /// Nothing is written into the client and no command is sent.
        /// </summary>
        public string CheckHijackReadiness()
        {
            EnsureContextBuffers();
            if (_hijackThread == IntPtr.Zero)
                ChooseHijackThread();                  // throws with the report when nothing works
            return _hijackReport;
        }

        /// <summary>
        /// Opens each Lua-thread candidate with the rights a takeover needs and keeps the first one
        /// that survives a real CONTEXT round-trip. The checks are ordered so that the log always
        /// names the culprit: open rights, then suspend, then read, then write, then read-back.
        /// </summary>
        private void ChooseHijackThread()
        {
            List<uint> candidates = new List<uint>();
            if (_guardThreadIds.Count > 0) candidates.AddRange(_guardThreadIds);
            else if (_luaThreadId != 0) candidates.Add(_luaThreadId);
            if (candidates.Count == 0)
                throw new LuaTakeoverException(
                    "hijack mode needs the client's Lua thread - FindLuaThread() found none (the sampling table says why)");

            StringBuilder report = new StringBuilder();
            for (int i = 0; i < candidates.Count; i++)
            {
                uint tid = candidates[i];
                IntPtr h = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadSetContext, false, tid);
                string opened;
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    h = OpenThread(ThreadAllAccess, false, tid);
                    opened = "OpenThread(0x1A = SUSPEND|GET_CONTEXT|SET_CONTEXT) err " + err;
                    if (h == IntPtr.Zero) opened += "; THREAD_ALL_ACCESS err " + Marshal.GetLastWin32Error();
                    else opened += "; THREAD_ALL_ACCESS ok";
                }
                else
                {
                    opened = "OpenThread(0x1A = SUSPEND|GET_CONTEXT|SET_CONTEXT) ok";
                }
                if (h == IntPtr.Zero)
                {
                    report.Append("tid ").Append(tid).Append(": ").Append(opened).Append(Environment.NewLine);
                    continue;
                }

                string detail;
                bool ok = SelfTestContext(h, out detail);
                report.Append("tid ").Append(tid).Append(": ").Append(opened).Append("; ").Append(detail).Append(Environment.NewLine);
                if (ok && _hijackThread == IntPtr.Zero)
                {
                    _hijackThread = h;
                    _luaThreadId = tid;
                }
                else
                {
                    CloseHandle(h);
                }
            }

            _hijackReport = report.ToString().TrimEnd();
            if (_hijackThread == IntPtr.Zero)
                throw new LuaTakeoverException(
                    "no Lua-thread candidate accepted a CONTEXT write, so no command can run on the client's Lua thread." +
                    Environment.NewLine + _hijackReport + Environment.NewLine +
                    "Nothing was written into the client and nothing was called. A takeover needs THREAD_SET_CONTEXT on " +
                    "the thread (0x10 - 0.7.0 asked for 0x40, THREAD_QUERY_INFORMATION) and a 16-byte aligned CONTEXT " +
                    "buffer; both are now requested and allocated, so the line above is what the kernel actually said.");
        }

        /// <summary>
        /// Suspend, read the context, write the SAME context back, read it again, resume. Nothing
        /// about the client changes if this works, and if it does not, the takeover would have
        /// failed inside a command instead - which is where 0.7.0 found out.
        /// </summary>
        private bool SelfTestContext(IntPtr h, out string detail)
        {
            StringBuilder sb = new StringBuilder();
            uint prev = SuspendThread(h);
            int err = Marshal.GetLastWin32Error();
            if (prev == 0xFFFFFFFFu)
            {
                detail = "SuspendThread err " + err;
                return false;
            }
            sb.Append("suspend count was ").Append(prev);
            bool ok = false;
            try
            {
                Marshal.WriteInt32(_savedCtxBuf, 0x30, unchecked((int)ContextAmd64Full));
                if (!GetThreadContextRaw(h, _savedCtxBuf))
                {
                    detail = sb.Append("; GetThreadContext(FULL) err ").Append(Marshal.GetLastWin32Error()).ToString();
                    return false;
                }
                ulong rip = (ulong)Marshal.ReadInt64(_savedCtxBuf, ContextOffsetRip);
                ulong rsp = (ulong)Marshal.ReadInt64(_savedCtxBuf, ContextOffsetRsp);
                sb.Append("; RIP 0x").Append(rip.ToString("X")).Append(" RSP 0x").Append(rsp.ToString("X"));

                string how;
                ok = TryWriteContext(h, _savedCtxBuf, rip, out how);        // FULL
                sb.Append("; FULL: ").Append(how);
                if (!ok)
                {
                    // second chance with the smallest request a takeover needs (RIP/RSP/segments
                    // plus the integer set that carries the control-block pointer)
                    Marshal.WriteInt32(_savedCtxBuf, 0x30, unchecked((int)ContextAmd64ControlInteger));
                    ok = TryWriteContext(h, _savedCtxBuf, rip, out how);
                    sb.Append("; CONTROL|INTEGER: ").Append(how);
                }
            }
            finally
            {
                ResumeThread(h);                                            // one resume per suspend
            }
            sb.Append(ok ? " -> round-trip OK" : " -> round-trip FAILED");
            detail = sb.ToString();
            return ok;
        }

        /// <summary>
        /// One verified CONTEXT write: Win32 first, then the raw NT call so the real NTSTATUS ends
        /// up on the record, then a read-back that proves the thread will actually run the RIP we
        /// asked for. The 2026-10-13 run only had "SetThreadContext failed: 0" to go on, because it
        /// read the last error after ResumeThread had already overwritten it.
        /// </summary>
        private bool TryWriteContext(IntPtr h, IntPtr ctx, ulong expectRip, out string detail)
        {
            uint flags = unchecked((uint)Marshal.ReadInt32(ctx, 0x30));
            bool ok = SetThreadContextRaw(h, ctx);
            int win32 = Marshal.GetLastWin32Error();           // capture BEFORE any other call runs
            StringBuilder sb = new StringBuilder();
            sb.Append("SetThreadContext(flags 0x").Append(flags.ToString("X")).Append(")=")
              .Append(ok ? "ok" : "FALSE err " + win32);
            if (!ok)
            {
                int nt = NtSetContextThreadRaw(h, ctx, flags);
                sb.Append("; NtSetContextThread=0x").Append(nt.ToString("X8"));
                if (nt == 0)
                {
                    ok = true;
                    sb.Append(" (the raw call took it)");
                }
            }
            if (ok)
            {
                // prove the three registers the payload cannot live without: the entry it runs at,
                // the stack it runs on, and the control block it reads (Rcx is what the payload
                // puts in Rbx and dereferences first - the 2026-10-13 crash was exactly a stale Rcx)
                Marshal.WriteInt32(_verifyBuf, 0x30, unchecked((int)ContextAmd64ControlInteger));
                if (!GetThreadContextRaw(h, _verifyBuf))
                {
                    ok = false;
                    sb.Append("; read-back FAILED err ").Append(Marshal.GetLastWin32Error());
                }
                else
                {
                    ulong gotRip = (ulong)Marshal.ReadInt64(_verifyBuf, ContextOffsetRip);
                    ulong gotRsp = (ulong)Marshal.ReadInt64(_verifyBuf, ContextOffsetRsp);
                    ulong gotRcx = (ulong)Marshal.ReadInt64(_verifyBuf, ContextOffsetRcx);
                    ulong wantRsp = (ulong)Marshal.ReadInt64(ctx, ContextOffsetRsp);
                    ulong wantRcx = (ulong)Marshal.ReadInt64(ctx, ContextOffsetRcx);
                    sb.Append("; read-back RIP 0x").Append(gotRip.ToString("X"))
                      .Append(" RSP 0x").Append(gotRsp.ToString("X"))
                      .Append(" RCX 0x").Append(gotRcx.ToString("X"));
                    if (gotRip != expectRip || gotRsp != wantRsp || gotRcx != wantRcx)
                    {
                        ok = false;
                        sb.Append(" -> the write did not take");
                    }
                }
            }
            detail = sb.ToString();
            return ok;
        }

        /// <summary>Take the client's Lua thread over: it will start running our payload.</summary>
        private void HijackBegin()
        {
            if (_hijackThread == IntPtr.Zero)
                throw new LuaTakeoverException("no hijackable Lua thread (the setup said: " + _hijackReport + ")");
            long start = _clock.ElapsedMilliseconds;
            int window = HijackWindowMs > 0 ? HijackWindowMs : 5000;
            int attempts = 0;
            bool acceptedBusy = false;
            string last = "no quiet moment seen yet";
            while (true)
            {
                attempts++;
                if (IsLuaQuiet())
                {
                    Thread.Sleep(2);                       // it must stay quiet across a sleep quantum
                    if (IsLuaQuiet())
                    {
                        uint prev = SuspendThread(_hijackThread);
                        int suspendErr = Marshal.GetLastWin32Error();      // before anything else
                        if (prev == 0xFFFFFFFFu)
                        {
                            last = "SuspendThread err " + suspendErr;
                        }
                        else if (prev != 0)
                        {
                            // someone else has this thread suspended; taking it over would fight them
                            // and resuming it once would leave it as they had it - but the VM is
                            // not ours to redirect while a third party holds it
                            last = "the thread already carried " + prev + " suspension(s) - not ours to redirect";
                            ResumeThread(_hijackThread);
                        }
                        else
                        {
                            bool took = false;
                            try
                            {
                                ulong clientRip = 0;
                                if (!IsLuaQuiet())
                                {
                                    last = "the VM moved while we were suspending it";
                                    _guardRetries++;
                                }
                                else
                                {
                                    Marshal.WriteInt32(_savedCtxBuf, 0x30, unchecked((int)ContextAmd64Full));
                                    if (!GetThreadContextRaw(_hijackThread, _savedCtxBuf))
                                    {
                                        last = "GetThreadContext err " + Marshal.GetLastWin32Error();
                                    }
                                    else
                                    {
                                        clientRip = (ulong)Marshal.ReadInt64(_savedCtxBuf, ContextOffsetRip);
                                        bool inLua = clientRip >= _base + 0x4700000UL && clientRip < _base + 0x4740000UL;
                                        bool inModule = clientRip >= _base && clientRip < _base + _moduleSize;
                                        if (!inModule)
                                        {
                                            // THE 2026-10-13 CRASH. A thread parked in a kernel wait (ntdll,
                                            // a driver, anywhere outside the game module) does NOT get the
                                            // whole context applied by the kernel: CONTROL took (Rip/Rsp were
                                            // ours - the fault log proves it) but the INTEGER file did not,
                                            // so the payload started with the client's stale RCX and its first
                                            // instruction - mov qword ptr [rbx+0xE0], 0, the call counter -
                                            // wrote into the client's code page and killed the process.
                                            // Nothing but user code inside the game module may be hijacked.
                                            _hijackSkippedWaitMoments++;
                                            last = "RIP 0x" + clientRip.ToString("X") + " is outside the game module" +
                                                   (_base != 0 && clientRip > _base + _moduleSize ? " (a parked thread)" : "") +
                                                   " - a context written there is applied only in part (that is the crash); " +
                                                   "waiting for a moment with RIP inside the module";
                                        }
                                        else if (inLua && _clock.ElapsedMilliseconds - start < 700)
                                        {
                                            // a moment spent inside the VM is the risky kind: it may be
                                            // between two mutations of state we are about to run code
                                            // against. Wait for a cheaper one while there is time.
                                            _hijackSkippedBusyMoments++;
                                            last = "RIP 0x" + clientRip.ToString("X") +
                                                   " is inside the Lua cluster - waiting for a moment outside it";
                                        }
                                        else
                                        {
                                            if (inLua) acceptedBusy = true;
                                            byte[] copy = new byte[ContextSize];
                                            Marshal.Copy(_savedCtxBuf, copy, 0, ContextSize);
                                            Marshal.Copy(copy, 0, _ctxBuf, ContextSize);
                                            WriteTakeoverRegisters();
                                            string how;
                                            bool ok = TryWriteContext(_hijackThread, _ctxBuf, _page + OffPayload, out how);
                                            if (!ok)
                                            {
                                                // second chance with the smallest request the payload
                                                // can start under: RIP/RSP/segments + the integer file
                                                Marshal.WriteInt32(_ctxBuf, 0x30, unchecked((int)ContextAmd64ControlInteger));
                                                WriteTakeoverRegisters();
                                                string how2;
                                                bool second = TryWriteContext(_hijackThread, _ctxBuf, _page + OffPayload, out how2);
                                                how = how + "; CONTROL|INTEGER retry: " + how2;
                                                ok = second;
                                            }
                                            last = how;
                                            if (ok)
                                            {
                                                _hijackHow = how;
                                                _hijackClientRip = clientRip;
                                                _hijackActive = true;
                                                took = true;
                                            }
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                if (!_hijackActive) ResumeThread(_hijackThread);   // one resume per suspend
                            }
                            if (took)
                            {
                                _hijackAttempts = attempts;
                                ResumeThread(_hijackThread);      // the payload now runs on the Lua thread
                                return;
                            }
                        }
                    }
                }
                if (_clock.ElapsedMilliseconds - start > window)
                {
                    throw new LuaTakeoverException(
                        "could not take the client's Lua thread over in " + attempts + " quiet window(s) within " + window +
                        " ms: " + last +
                        Environment.NewLine + "  hijack setup: " + _hijackReport +
                        (acceptedBusy ? Environment.NewLine + "  a moment with RIP inside the Lua VM had to be accepted" : string.Empty) +
                        (_hijackSkippedBusyMoments > 0
                            ? Environment.NewLine + "  moments skipped for being inside the Lua VM: " + _hijackSkippedBusyMoments
                            : string.Empty) +
                        (_hijackSkippedWaitMoments > 0
                            ? Environment.NewLine + "  moments skipped for being OUTSIDE the game module (parked threads - " +
                              "a context does not apply there, this is the 2026-10-13 crash): " + _hijackSkippedWaitMoments
                            : string.Empty) +
                        Environment.NewLine + "  nothing was called and the client's thread was resumed where it was");
                }
                _guardWaits++;
                Thread.Sleep(1);
            }
        }

        /// <summary>The three registers a takeover changes: which block, which stack, which code.</summary>
        private void WriteTakeoverRegisters()
        {
            Marshal.WriteInt64(_ctxBuf, ContextOffsetRcx, (long)(_page + OffCtrl));
            Marshal.WriteInt64(_ctxBuf, ContextOffsetRsp, (long)(_stackPage + HijackStackSize - 8));
            Marshal.WriteInt64(_ctxBuf, ContextOffsetRip, (long)(_page + OffPayload));
        }

        /// <summary>
        /// Give the client's Lua thread back exactly where we took it (payload parked in the stub).
        /// If the context cannot be put back the thread is left suspended in our stub - never
        /// resumed, because resuming it would run the client's thread on our registers.
        /// </summary>
        private void HijackEnd()
        {
            if (!_hijackActive) return;
            uint prev = SuspendThread(_hijackThread);
            int suspendErr = Marshal.GetLastWin32Error();
            if (prev == 0xFFFFFFFFu)
                throw new LuaTakeoverException("SuspendThread failed while handing the Lua thread back (err " + suspendErr +
                    "): the client's thread is parked in our stub and its context was NOT restored - end wowb.exe from Task Manager");
            string how;
            Marshal.WriteInt32(_savedCtxBuf, 0x30, unchecked((int)ContextAmd64Full));
            bool ok = TryWriteContext(_hijackThread, _savedCtxBuf, _hijackClientRip, out how);
            if (!ok)
            {
                Marshal.WriteInt32(_savedCtxBuf, 0x30, unchecked((int)ContextAmd64ControlInteger));
                string how2;
                bool second = TryWriteContext(_hijackThread, _savedCtxBuf, _hijackClientRip, out how2);
                how = how + "; CONTROL|INTEGER retry: " + how2;
                ok = second;
            }
            _hijackHow = how;
            if (!ok)
            {
                _hijackActive = false;
                _stuck = true;
                _stoppedFrozen = true;
                throw new LuaTakeoverException("the client's Lua thread could not be given its context back (" + how +
                    "). It is parked in our stub and has been left suspended on purpose: ending wowb.exe from Task Manager " +
                    "is the only safe step left." + Environment.NewLine + "  hijack setup: " + _hijackReport);
            }
            ResumeThread(_hijackThread);
            _hijackActive = false;
        }

        private bool WaitForStage(ulong stage, int timeoutMs)
        {
            long start = _clock.ElapsedMilliseconds;
            while (true)
            {
                if (ReadU64(_page + OffCtrl + (ulong)B_Stage) == stage) return true;
                if (_clock.ElapsedMilliseconds - start > timeoutMs) return false;
                Thread.SpinWait(50);
            }
        }

        private bool WaitForAck(ulong seq, int timeoutMs)
        {
            long start = _clock.ElapsedMilliseconds;
            while (true)
            {
                if (ReadU64(_page + OffCtrl + (ulong)B_Ack) == seq) return true;
                if (_clock.ElapsedMilliseconds - start > timeoutMs) return false;
                Thread.SpinWait(50);
            }
        }

        /// <summary>
        /// A command on the client's Lua thread. The payload zeroes CMD as its first act, so the
        /// command is written only after the stage says the payload is polling; the exit command
        /// only after the ack. If any of that does not happen, the thread is left where it is -
        /// restoring its context would resume a half-finished VM mutation, which is exactly the
        /// corruption this mode exists to avoid.
        /// </summary>
        private LuaValue[] CallHijacked(string function, string arg, int nresults, int timeoutMs)
        {
            WriteCommandBlock(function, arg, nresults);
            HijackBegin();

            bool clean = false;
            try
            {
                if (!WaitForStage(StageResident, 2000))
                    throw new TimeoutException("the payload did not reach its poll loop on the Lua thread (stage 0x" +
                        ReadU64(_page + OffCtrl + (ulong)B_Stage).ToString("X") + ")");

                _seq++;
                WriteU64(_page + OffCtrl + (ulong)B_Seq, _seq);
                WriteU64(_page + OffCtrl + (ulong)B_Cmd, CmdRun);
                long t0 = _clock.ElapsedTicks;
                if (!WaitForAck(_seq, timeoutMs))
                    throw new TimeoutException("the command did not complete within " + timeoutMs +
                        " ms ON the Lua thread (payload stage 0x" + ReadU64(_page + OffCtrl + (ulong)B_Stage).ToString("X") + ")");
                _lastCallMs = (double)(_clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;

                WriteU64(_page + OffCtrl + (ulong)B_Cmd, CmdExit);
                if (!WaitForStage(StageExit, 2000))
                    throw new TimeoutException("the payload did not exit to the stub (stage 0x" +
                        ReadU64(_page + OffCtrl + (ulong)B_Stage).ToString("X") + ")");
                clean = true;
            }
            finally
            {
                if (clean)
                {
                    try
                    {
                        HijackEnd();                   // the thread is parked in our stub: safe to give back
                    }
                    catch
                    {
                        _stuck = true;                 // could not put its context back - do not pretend it is ours
                        _stoppedFrozen = true;
                        throw;
                    }
                }
                else
                {
                    // Leave everything as it is. The Lua thread is inside our payload (probably
                    // inside the client's own function); restoring the context now would resume
                    // the VM with a call still on its stack. The client must be restarted.
                    _stuck = true;
                    _stoppedFrozen = true;
                }
            }

            byte[] block = Read(_page + OffCtrl, 0x200);
            if (block == null) throw new InvalidOperationException("the control block became unreadable");
            ulong pc = GetU64(block, B_PcallStatus);
            int status = unchecked((int)(uint)pc);
            if (status != 0)
            {
                LuaValue err = Value(block, B_Res1, B_Res1Type);
                throw new InvalidOperationException(function + "(" + (arg ?? "") + ") failed: status " + status +
                    (err.Type == LuaType.String ? " error=\"" + err.Text + "\"" : string.Empty));
            }
            return ReadResults(nresults, status);
        }

        private IntPtr OpenThread2(uint tid)
        {
            return OpenThread(ThreadSuspendResume | ThreadGetContext, false, tid);
        }

        private static IntPtr Align16(IntPtr p)
        {
            return new IntPtr((p.ToInt64() + 15L) & ~15L);
        }

        // ---------------------------------------------------------------- helpers
        private ulong FindGadget()
        {
            for (int i = 0; i < GadgetRvas.Length; i++)
            {
                ulong va = _base + GadgetRvas[i];
                byte[] b = Read(va, 2);
                if (b == null || b[0] != 0xFF || b[1] != 0x21) continue;
                byte[] mbi = new byte[0x30];
                if (VirtualQueryEx(_process, new IntPtr((long)va), mbi, 0x30) < 0x30) continue;
                uint protect = BitConverter.ToUInt32(mbi, 0x24);
                uint kind = protect & 0xF0;
                if (kind != 0x10 && kind != 0x20 && kind != 0x40 && kind != 0x80) continue;
                return va;
            }
            return 0;
        }

        /// <summary>
        /// kernel32 is a KnownDLL: every process on this boot maps it at the same address, the
        /// same assumption <see cref="Executor64"/> makes. Verified anyway - if the target's bytes
        /// at that address do not match ours, the function is not used (the payload then falls
        /// back to its spin budget, which still expires).
        /// </summary>
        private ulong ResolveKernelFunction(string name, bool verify)
        {
            IntPtr module = GetModuleHandleA("kernel32.dll");
            if (module == IntPtr.Zero) return 0;
            IntPtr fn = GetProcAddress(module, name);
            if (fn == IntPtr.Zero) return 0;
            ulong va = (ulong)fn.ToInt64();
            if (!verify) return va;
            byte[] local = new byte[16];
            Marshal.Copy(fn, local, 0, local.Length);
            byte[] remote = Read(va, 16);
            if (remote == null) return 0;
            for (int i = 0; i < 16; i++)
                if (local[i] != remote[i]) return 0;
            byte[] mbi = new byte[0x30];
            if (VirtualQueryEx(_process, new IntPtr((long)va), mbi, 0x30) < 0x30) return 0;
            if (BitConverter.ToUInt32(mbi, 0x20) != 0x1000) return 0;   // MEM_COMMIT
            if ((BitConverter.ToUInt32(mbi, 0x24) & 0xF0) == 0) return 0;
            return va;
        }

        private byte[]? Read(ulong address, int count)
        {
            if (_process == IntPtr.Zero || count <= 0) return null;
            byte[] buf = new byte[count];
            IntPtr got;
            if (!ReadProcessMemory(_process, new IntPtr((long)address), buf, new IntPtr(count), out got)) return null;
            if (got.ToInt64() != count) return null;
            return buf;
        }

        private void Write(ulong address, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            IntPtr written;
            if (!WriteProcessMemory(_process, new IntPtr((long)address), data, new IntPtr(data.Length), out written) ||
                written.ToInt64() != data.Length)
                throw new InvalidOperationException("WriteProcessMemory(0x" + address.ToString("X") + ") failed: " +
                    Marshal.GetLastWin32Error());
        }

        private ulong ReadU64(ulong address)
        {
            byte[] b = Read(address, 8);
            return b == null ? 0 : BitConverter.ToUInt64(b, 0);
        }

        private uint ReadU32(ulong address)
        {
            byte[] b = Read(address, 4);
            return b == null ? 0 : BitConverter.ToUInt32(b, 0);
        }

        private void WriteU64(ulong address, ulong value)
        {
            Write(address, BitConverter.GetBytes(value));
        }

        private static ulong GetU64(byte[] b, int off)
        {
            return BitConverter.ToUInt64(b, off);
        }

        private static ushort GetU16(byte[] b, int off)
        {
            return BitConverter.ToUInt16(b, off);
        }

        private static void PutU64(byte[] b, int off, ulong v)
        {
            byte[] t = BitConverter.GetBytes(v);
            Buffer.BlockCopy(t, 0, b, off, 8);
        }

        private static void PutU32(byte[] b, int off, uint v)
        {
            byte[] t = BitConverter.GetBytes(v);
            Buffer.BlockCopy(t, 0, b, off, 4);
        }

        private static bool Equal(byte[] a, byte[] b, int count)
        {
            for (int i = 0; i < count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] Terminated(byte[] ascii)
        {
            byte[] b = new byte[ascii.Length + 1];
            Buffer.BlockCopy(ascii, 0, b, 0, ascii.Length);
            return b;
        }

        private static string ToHex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        private static byte[] FromHex(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
