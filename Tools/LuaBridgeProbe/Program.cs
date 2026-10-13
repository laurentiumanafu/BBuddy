using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GreenMagic;

namespace LuaBridgeProbe
{
    /// <summary>
    /// Drives the resident x64 Lua bridge (GreenMagic/LuaBridge64.cs) against a live
    /// native 64-bit WoW client, so the managed side of the port can be exercised
    /// without the bot UI.
    ///
    ///   dotnet run --project Tools/LuaBridgeProbe -c Release
    ///   dotnet run --project Tools/LuaBridgeProbe -c Release -- --seconds 60 --interval 250
    ///   dotnet run --project Tools/LuaBridgeProbe -c Release -- --call UnitHealth --arg player
    ///   dotnet run --project Tools/LuaBridgeProbe -c Release -- --call RunScript --arg "print(1)"
    ///   dotnet run --project Tools/LuaBridgeProbe -c Release -- --selftest
    /// </summary>
    internal static class Program
    {
        private static volatile bool _stop;

        private static int Main(string[] args)
        {
            if (Has(args, "--help") || Has(args, "-h"))
            {
                Usage();
                return 0;
            }
            if (Has(args, "--selftest"))
                return SelfTest();

            string name = Arg(args, "--name") ?? "wowb";
            int seconds = ToInt(Arg(args, "--seconds"), 30);
            int interval = ToInt(Arg(args, "--interval"), 500);
            bool verbose = Has(args, "--verbose");
            string extra = Arg(args, "--each");                 // was --call: one extra call per round
            string oneCall = Arg(args, "--call");               // one call, then stop
            string seqScript = Arg(args, "--seq");              // several calls in one session
            string oneArg = Arg(args, "--arg");
            int oneResults = ToInt(Arg(args, "--nresults"), 1);
            bool force = Has(args, "--force");
            ulong rvaHint = ToU64(Arg(args, "--rva"));
            bool onLuaThread = Has(args, "--on-lua-thread");
            string dumpSpec = Arg(args, "--dump");
            string dumpOut = Arg(args, "--out");
            bool dumpText = Has(args, "--dump-text");
            int timeoutMsOpt = ToInt(Arg(args, "--timeout-ms"), 1000);
            uint luaThread = 0;
            string luaThreadText = Arg(args, "--lua-thread");
            if (luaThreadText != null)
                uint.TryParse(luaThreadText, NumberStyles.Integer, CultureInfo.InvariantCulture, out luaThread);

            int pid = 0;
            string pidText = Arg(args, "--pid");
            if (pidText != null)
            {
                if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                {
                    Console.WriteLine("--pid needs a number");
                    return 2;
                }
            }
            else
            {
                pid = LuaBridge64.FindProcessId(name);
            }
            if (pid <= 0)
            {
                Console.WriteLine("no process named '" + name + "' found - start the x64 client, log a character in, then run this again");
                return 3;
            }

            LuaBridge64 bridge = null;
            try
            {
                bridge = new LuaBridge64();
                bridge.Attach(pid);
                Console.WriteLine("client      : pid " + pid + "   image base 0x" + bridge.ImageBase.ToString("X"));
                Console.WriteLine("lua_State   : 0x" + bridge.LuaState.ToString("X"));
                Console.WriteLine("trampoline  : 0x" + bridge.TrampolineAddress.ToString("X") +
                                  "   (module+0x471A983, `e8 ... c3` verified live)");

                if (Has(args, "--no-guard")) bridge.GuardEnabled = false;
                if (timeoutMsOpt > 0) bridge.HijackTimeoutMs = timeoutMsOpt;
                if (onLuaThread)
                {
                    bridge.ExecutionMode = LuaExecutionMode.LuaThreadHijack;
                    Console.WriteLine("mode        : ON the client's Lua thread (hijack) - each command runs on the VM's own thread");
                }
                else
                {
                    Console.WriteLine("mode        : on our injected resident thread (the client's Lua thread is frozen around each call)");
                }
                if (luaThread != 0)
                {
                    bool forced = bridge.UseLuaThread(luaThread);
                    Console.WriteLine("lua thread  : forced to tid " + luaThread +
                                      (forced ? " (handle opened, guard will freeze it)"
                                              : " - OpenThread FAILED for that tid (is it still running?)"));
                    if (!forced) return 4;
                }

            if (Has(args, "--check"))
                {
                    string[] report = bridge.Verify();
                    for (int i = 0; i < report.Length; i++) Console.WriteLine("            " + report[i]);
                    if (onLuaThread)
                    {
                        Console.WriteLine("            hijack      : readiness - a real CONTEXT round-trip on each candidate thread");
                        Console.WriteLine("                          (suspend to read the SAME context back to resume; no command is sent):");
                        Console.WriteLine("            " + bridge.CheckHijackReadiness().Replace(Environment.NewLine,
                            Environment.NewLine + "            "));
                    }
                    if (bridge.GuardEnabled && bridge.ThreadReport.Length > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("[check]     how the guard picked its thread (suspend -> CONTEXT_CONTROL -> resume):");
                        Console.WriteLine(bridge.ThreadReport);
                    }
                    Console.WriteLine();
                    Console.WriteLine("[check]     nothing was written into the client and no thread was started");
                    return 0;
                }

                if (dumpSpec != null)
                    return DumpMemory(bridge, dumpSpec, dumpOut ?? "bbuddy-dump.bin", dumpText);

                bridge.Start(Math.Max(seconds + 30, 60), 1);
                Console.WriteLine("bridge page : 0x" + bridge.Page.ToString("X") + "   gadget 0x" + bridge.Gadget.ToString("X"));
                Console.WriteLine("payload     : " + LuaBridge64.PayloadSize + " bytes, resident, waiting for commands");
                if (bridge.ExecutionMode == LuaExecutionMode.LuaThreadHijack)
                {
                    Console.WriteLine("guard       : not used in this mode - taking the Lua thread over IS the synchronisation" +
                                      (bridge.GuardThreadIds.Length > 0 ? " (tid " + bridge.GuardThreadIds + ")" : ""));
                    if (bridge.HijackReport.Length > 0)
                        Console.WriteLine("takeover    : " + bridge.HijackReport.Replace(Environment.NewLine,
                                          Environment.NewLine + "              "));
                }
                else if (bridge.GuardEnabled && bridge.GuardThreadIds.Length == 0)
                {
                    Console.WriteLine("guard       : NO THREAD IDENTIFIED - a call would race the client's Lua");
                    Console.WriteLine(bridge.ThreadReport.Length > 0 ? bridge.ThreadReport : "(no sampling table)");
                    Console.WriteLine("              fix: --check prints the table, then --lua-thread <tid> freezes a thread by hand");
                }
                else if (bridge.GuardEnabled)
                    Console.WriteLine("guard       : freezes tid " + bridge.GuardThreadIds +
                                      (bridge.GuardThreadSeenInLua ? " (seen executing Lua when sampled)" : " (best guess: busiest module thread)") +
                                      " for the duration of every call");
                else
                    Console.WriteLine("guard       : DISABLED (--no-guard) - calls will race the client's own Lua execution");
                if (bridge.GuardEnabled && bridge.ThreadReport.Length > 0)
                    Console.WriteLine(bridge.ThreadReport);
                Console.WriteLine();

                if (seqScript != null)
                    return Sequence(bridge, pid, seqScript, force);
                if (oneCall != null)
                    return OneShot(bridge, pid, oneCall, oneArg, oneResults, force, rvaHint);

                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    _stop = true;
                };

                List<double> latency = new List<double>();
                Stopwatch clock = Stopwatch.StartNew();
                int rounds = 0;
                int calls = 0;

                while (!_stop && clock.Elapsed.TotalSeconds < seconds)
                {
                    rounds++;
                    long tick = clock.ElapsedTicks;

                    string time = bridge.Call("GetTime", null, 1)[0].ToString();
                    string who = bridge.CallString("UnitName", "player") ?? "(none)";
                    double level = bridge.CallNumber("UnitLevel", "player");
                    double hp = bridge.CallNumber("UnitHealth", "player");
                    bool hasTarget = bridge.CallBoolean("UnitExists", "target");
                    string guid = bridge.CallString("UnitGUID", "player");
                    LuaValue[] klass = bridge.Call("UnitClass", "player", 2);
                    calls += 7;

                    latency.Add((double)(clock.ElapsedTicks - tick) * 1000.0 / Stopwatch.Frequency);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "[{0,4}] GetTime={1,-13} {2,-10} level {3,-3} hp {4,-5} target={5,-5} {6,-20} class {7}/{8}",
                        rounds, time, who, level, hp, hasTarget, ShortGuid(guid), klass[0], klass[1]));

                    if (verbose)
                        Console.WriteLine("        last call " + bridge.LastCallMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
                                          " ms, payload has served " + bridge.ServedCalls + " commands" +
                                          (bridge.GuardEnabled
                                              ? ", guard: " + bridge.GuardSuspends + " freezes, " + bridge.GuardWaits + " waits, " + bridge.GuardRetries + " retries"
                                              : string.Empty));

                    if (extra != null)
                    {
                        int eq = extra.IndexOf('=');
                        string fn = eq < 0 ? extra : extra.Substring(0, eq);
                        string arg = eq < 0 ? null : extra.Substring(eq + 1);
                        calls++;
                        Console.WriteLine("        " + fn + "(" + (arg ?? "") + ") = " + Join(bridge.Call(fn, arg, 1)));
                    }

                    Thread.Sleep(Math.Max(0, interval));
                }

                Console.WriteLine();
                Stats(latency, calls, clock.Elapsed.TotalSeconds);
                Console.WriteLine("payload     : served " + bridge.ServedCalls + " commands in total");
                if (bridge.GuardEnabled && bridge.GuardSuspends > 0)
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "guard       : {0} freezes, {1} waits for the VM to go quiet, {2} retries, {3:F3} ms frozen per call",
                        bridge.GuardSuspends, bridge.GuardWaits, bridge.GuardRetries,
                        bridge.GuardSuspendMilliseconds / bridge.GuardSuspends));

                bridge.Stop();
                Console.WriteLine("[stop]      the resident thread exited on command - no TerminateThread was used");

                Process alive = Process.GetProcessById(pid);
                Console.WriteLine("[alive]     pid " + alive.Id + " is still running (" + alive.Threads.Count + " threads)");
                return 0;
            }
            catch (LuaTakeoverException ex)
            {
                Console.WriteLine();
                Console.WriteLine("[takeover] the bridge could not run a command on the client's Lua thread:");
                Console.WriteLine("[takeover] " + ex.Message.Replace(Environment.NewLine, Environment.NewLine + "[takeover] "));
                Console.WriteLine("[takeover] this is a setup/rights problem, not a Lua refusal - the client's VM was not");
                Console.WriteLine("[takeover] modified by it, and no command was sent.");
                return 8;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("[error] " + ex.GetType().Name + ": " + ex.Message);
                bool alive = false;
                try { Process p2 = Process.GetProcessById(pid); alive = !p2.HasExited; } catch { alive = false; }
                Console.WriteLine(alive
                    ? "[error] the client is STILL RUNNING - send this log back; restart it only if a command never completed"
                    : "[error] the client has DIED - restart it before the next attempt");
                if (bridge != null && bridge.ThreadReport.Length > 0)
                {
                    Console.WriteLine("[error] the guard thread sampling table:");
                    Console.WriteLine(bridge.ThreadReport);
                }
                return 4;
            }
            finally
            {
                if (bridge != null) bridge.Dispose();
            }
        }

        /// <summary>
        /// One guarded call, then stop - "invoke any Lua global" mode. It refuses to call a
        /// handler whose page the client has not decrypted yet, because that executes ciphertext.
        /// </summary>
        private static int OneShot(LuaBridge64 bridge, int pid, string name, string arg, int nresults, bool force, ulong rvaHint)
        {
            Console.WriteLine();
            string gate;
            if (!GateCheck(bridge, name, force, rvaHint, out gate))
            {
                Console.WriteLine(gate);
                return 5;
            }
            if (gate.Length > 0) Console.WriteLine(gate);

            LuaValue[] res = null;
            string luaError = null;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                res = bridge.Call(name, arg, Math.Max(1, Math.Min(2, nresults)));
            }
            catch (LuaTakeoverException ex)
            {
                Console.WriteLine();
                Console.WriteLine("[call]      BRIDGE could not take the Lua thread over - nothing in the client's VM was touched:");
                Console.WriteLine("[call]      " + ex.Message.Replace(Environment.NewLine, Environment.NewLine + "[call]      "));
                return 8;
            }
            catch (InvalidOperationException ex)
            {
                luaError = ex.Message;                       // the handler ran and the CLIENT refused it
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine();
                Console.WriteLine("[call]      TIMED OUT - " + ex.Message);
                PrintWedgeAdvice(bridge);
                return 6;
            }
            sw.Stop();

            Console.WriteLine();
            if (luaError != null)
            {
                Console.WriteLine("[call]      the client's Lua refused it: " + luaError);
                Console.WriteLine("[call]      that is an answer, not a bridge failure - the command was served and the");
                Console.WriteLine("[call]      payload acknowledged it. If the text says the function is protected or not");
                Console.WriteLine("[call]      usable by insecure code, this action needs the in-process/hardware path");
                Console.WriteLine("[call]      (DX11PresentHook64 + synthetic input) rather than a Lua call.");
            }
            else
            {
                string text = name + "(" + (arg == null ? string.Empty : "\"" + arg + "\"") + ") = ";
                for (int i = 0; i < res.Length; i++)
                {
                    if (i > 0) text += ",   ";
                    text += res[i].ToString();
                }
                Console.WriteLine("[call]      " + text);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[call]      {0:F2} ms wall, {1:F2} ms while frozen, type word 0x{2:X} -> {3}",
                    sw.Elapsed.TotalMilliseconds, bridge.LastCallMilliseconds, res[0].RawType, res[0].Type));
                if (res.Length > 1)
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "[call]      second result: {0}   (type word 0x{1:X} -> {2})",
                        res[1].ToString(), res[1].RawType, res[1].Type));
                Console.WriteLine("[call]      the type word is not the type: the client sets 0x100 on secure values,");
                Console.WriteLine("[call]      so compare (tt & 0xFF) == 3 for a number, as this build does");
            }
            if (bridge.GuardEnabled && bridge.GuardSuspends > 0)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[guard]     {0} freezes, {1} waits for the VM to go quiet, {2} retries, {3:F3} ms frozen per call",
                    bridge.GuardSuspends, bridge.GuardWaits, bridge.GuardRetries,
                    bridge.GuardSuspendMilliseconds / bridge.GuardSuspends));
            Console.WriteLine("payload     : served " + bridge.ServedCalls + " command(s) on one resident thread");

            bridge.Stop();
            Console.WriteLine("[stop]      the resident thread exited on command - no TerminateThread was used");
            Process alive = Process.GetProcessById(pid);
            Console.WriteLine("[alive]     pid " + alive.Id + " is still running (" + alive.Threads.Count + " threads)");
            return luaError == null ? 0 : 7;
        }

        /// <summary>
        /// What a wedged command means now: nothing was resumed and nothing was freed, on purpose.
        /// A half-finished call must not be allowed to run alongside the client's own VM work.
        /// </summary>
        private static void PrintWedgeAdvice(LuaBridge64 bridge)
        {
            Console.WriteLine();
            if (bridge.ExecutionMode == LuaExecutionMode.LuaThreadHijack)
                Console.WriteLine("[wedge]     the client's Lua thread is still inside the command (it is parked in our page)");
            else
                Console.WriteLine("[wedge]     the client's Lua thread is still FROZEN (the guard did not resume it)");
            Console.WriteLine("[wedge]     I deliberately did NOT resume it and did NOT free the page: a call that never");
            Console.WriteLine("[wedge]     returned must not run beside the client's own VM work - that is the 2026-10-13");
            Console.WriteLine("[wedge]     corruption. The client is unusable now: end it from Task Manager.");
            Console.WriteLine("[wedge]     If it terminated itself instead, its own error report says why (send that file).");
        }

        /// <summary>
        /// The cold-page gate: may this handler be called now? Returns true to proceed (the
        /// message, when non-empty, says what was found), false to refuse (the message is the
        /// reason plus the fix). A page the client has not decrypted holds ciphertext - executing
        /// it faults or wedges, and a protection change does not decrypt it.
        /// </summary>
        private static bool GateCheck(LuaBridge64 bridge, string name, bool force, ulong rvaHint, out string message)
        {
            message = string.Empty;
            ulong rva = rvaHint != 0 ? rvaHint : KnownHandlerRva(name);
            bool wasCold = WasColdInSurvey(name);

            if (rva == 0)
            {
                string head = "handler     : '" + name + "' is not in the RVA table - no cold-page pre-check" + Environment.NewLine +
                              "              (pass --rva 0x... to pre-check its handler anyway)";
                if (wasCold && !force)
                {
                    message = head + Environment.NewLine + Environment.NewLine +
                        "refusing to call '" + name + "': the hunt37/hunt38 survey found this handler encrypted," + Environment.NewLine +
                        "and without an RVA there is nothing to verify it against now. If you have used it in-game" + Environment.NewLine +
                        "since (which decrypts it) add --force, or pass its handler RVA as --rva 0x... and let the check decide.";
                    return false;
                }
                message = head;
                return true;
            }

            string hex = bridge.ReadHex(bridge.ImageBase + rva, 16);
            bool cold = hex == null;
            message = "handler     : module+0x" + rva.ToString("X") +
                      (cold ? "   UNREADABLE - still encrypted (cold)"
                            : "   readable, first bytes " + hex + " -> WARM") +
                      (wasCold ? (cold ? "   [cold in the hunt37/38 survey, and still cold]"
                                       : "   [cold in the hunt37/38 survey - the game has since decrypted it]")
                               : string.Empty);
            if (cold && !force)
            {
                string companion = string.Empty;
                if (name.EndsWith("Start", StringComparison.OrdinalIgnoreCase) && name.Length > 5)
                    companion = Environment.NewLine + "      (then   /run " +
                                name.Substring(0, name.Length - 5) + "Stop()   so you do not walk into a wall)";
                message += Environment.NewLine + Environment.NewLine +
                    "refusing to call '" + name + "': the client has not decrypted that handler's page" + Environment.NewLine +
                    "yet, so calling it now would execute ciphertext (fault or wedge). Warm it by making" + Environment.NewLine +
                    "the CLIENT run the function once - in the chat box:" + Environment.NewLine +
                    Environment.NewLine +
                    "      " + WarmHint(name) + companion + Environment.NewLine +
                    Environment.NewLine +
                    "A bound key is not enough for a movement function: the key's code path and the Lua" + Environment.NewLine +
                    "handler sit on different pages (the 2026-10-13 session had movement in it and this" + Environment.NewLine +
                    "page was still cold). The read-only set (Unit*, GetTime, GetMoney, IsLoggedIn," + Environment.NewLine +
                    "GetFramerate, ...) is warm and needs no warming at all." + Environment.NewLine +
                    "--force calls it anyway; only use it when you know the page is decrypted.";
                return false;
            }
            if (cold) message += Environment.NewLine + "              --force given: calling it anyway";
            return true;
        }

        /// <summary>
        /// Read-only memory dump: "va:len" pairs in hex, separated by ',' or ';'. Nothing is
        /// injected and no thread is started - this is the same ReadProcessMemory the cold-page
        /// gate uses, and it exists because the pages that matter (the movement handlers, the
        /// dispatcher chain) are decrypted in the LIVE client the moment the game itself runs
        /// them, while the offline image is encrypted for exactly those pages.
        /// The binary file is the ranges concatenated in order; the .txt beside it is a manifest
        /// (offset, address, length, unreadable 4 KB pages, first bytes).
        /// </summary>
        private static int DumpMemory(LuaBridge64 bridge, string spec, string outPath, bool asText)
        {
            string[] parts = spec.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            List<ulong> vas = new List<ulong>();
            List<int> lens = new List<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                string[] pair = parts[i].Split(':');
                if (pair.Length != 2)
                {
                    Console.WriteLine("--dump wants va:len pairs, e.g. --dump 0x23C5500:0x400,0x23C8BE0:0x400");
                    return 2;
                }
                string spec0 = pair[0].Trim();
                bool forcedRva = spec0.StartsWith("rva:", StringComparison.OrdinalIgnoreCase);
                bool forcedVa = spec0.StartsWith("va:", StringComparison.OrdinalIgnoreCase);
                if (forcedRva || forcedVa) spec0 = spec0.Substring(4);
                ulong raw = ToU64(spec0);
                ulong len = ToU64(pair[1]);
                if (raw == 0 || len == 0 || len > 0x100000)
                {
                    Console.WriteLine("--dump: bad range '" + parts[i] + "' (each len up to 0x100000)");
                    return 2;
                }
                // Hunt addresses are module RVAs (0x23C8BE0) while a dump needs an absolute address.
                // Anything below the image base is read as an RVA unless the range says "va:".
                bool asRva = forcedRva || (!forcedVa && raw < bridge.ImageBase);
                ulong va = asRva ? bridge.ImageBase + raw : raw;
                vas.Add(va);
                lens.Add((int)len);
            }
            if (vas.Count == 0)
            {
                Console.WriteLine("--dump wants at least one va:len pair");
                return 2;
            }

            StringBuilder manifest = new StringBuilder();
            manifest.Append("bbuddy live dump   image base 0x").Append(bridge.ImageBase.ToString("X")).Append(Environment.NewLine);
            List<byte> all = new List<byte>();
            for (int i = 0; i < vas.Count; i++)
            {
                int unreadablePages, unreadableBlocks;
                byte[] data = bridge.ReadDump(vas[i], lens[i], out unreadablePages, out unreadableBlocks);
                int pages = (lens[i] + 0xFFF) / 0x1000;
                string where = vas[i] >= bridge.ImageBase
                    ? "  module+0x" + (vas[i] - bridge.ImageBase).ToString("X")
                    : string.Empty;
                string line = "va 0x" + vas[i].ToString("X") + where +
                              "  len 0x" + lens[i].ToString("X") +
                              "  readable " + (pages - unreadablePages) + "/" + pages + " page(s)" +
                              (unreadablePages > 0 || unreadableBlocks > 0
                                  ? "  [" + unreadablePages + " page(s) refused whole, " +
                                    unreadableBlocks + " 64-byte block(s) unreadable -> zeros]"
                                  : "  [fully readable]");
                manifest.Append("offset 0x").Append(all.Count.ToString("X8")).Append("  ").Append(line)
                        .Append("  first ").Append(Hex(data, 0, Math.Min(16, data.Length))).Append(Environment.NewLine);
                Console.WriteLine("[dump]      " + line);
                if (asText)
                {
                    Console.WriteLine();
                    for (int off = 0; off < data.Length; off += 16)
                        Console.WriteLine(Hex(data, off, Math.Min(16, data.Length - off)));
                    Console.WriteLine();
                }
                all.AddRange(data);
            }

            if (asText)
            {
                Console.WriteLine("[dump]      " + all.Count + " bytes printed; nothing was written into the client");
                return 0;
            }

            File.WriteAllBytes(outPath, all.ToArray());
            File.WriteAllText(outPath + ".txt", manifest.ToString());
            Console.WriteLine("[dump]      " + all.Count + " bytes -> " + outPath + "  (manifest: " + outPath + ".txt)");
            Console.WriteLine("[dump]      md5 " + Md5Hex(all.ToArray()));
            Console.WriteLine("[dump]      read-only: no thread was started and nothing was written into the client");
            return 0;
        }

        /// <summary>
        /// Several guarded calls in ONE session: <c>"MoveForwardStart ; sleep=1200 ; MoveForwardStop"</c>.
        /// The page, the thread pick and the resident payload thread are set up once, so an action
        /// that has to be undone (walk for a moment, turn, cast) does not pay the ~4 s of attach +
        /// sampling that a second process would - and the steps are separated by milliseconds, not
        /// seconds. This is the shape the bot's action queue will have.
        /// </summary>
        private static int Sequence(LuaBridge64 bridge, int pid, string script, bool force)
        {
            string[] raw = script.Split(';');
            Console.WriteLine();
            Console.WriteLine("[seq]       steps: " + raw.Length + " (one session, one thread pick, one resident payload)");
            int step = 0;
            int served = 0;
            int luaErrors = 0;
            try
            {
                for (int i = 0; i < raw.Length; i++)
                {
                    string s = raw[i].Trim();
                    if (s.Length == 0) continue;
                    step++;

                    if (s.StartsWith("sleep=", StringComparison.OrdinalIgnoreCase))
                    {
                        int ms;
                        if (!int.TryParse(s.Substring(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) || ms < 0)
                        {
                            Console.WriteLine("[seq " + step + "]     bad sleep step: '" + s + "' (use sleep=1200)");
                            return 2;
                        }
                        Console.WriteLine("[seq " + step + "]     sleep " + ms + " ms");
                        Thread.Sleep(Math.Min(ms, 30000));
                        continue;
                    }

                    int eq = s.IndexOf('=');
                    string fn = eq < 0 ? s : s.Substring(0, eq).Trim();
                    string arg = eq < 0 ? null : s.Substring(eq + 1);
                    string gate;
                    if (!GateCheck(bridge, fn, force, 0, out gate))
                    {
                        Console.WriteLine(gate);
                        Console.WriteLine("[seq " + step + "]     REFUSED at step " + step + " - nothing after it was sent");
                        return 5;
                    }
                    if (gate.Length > 0) Console.WriteLine(gate);

                    Stopwatch sw = Stopwatch.StartNew();
                    LuaValue[] res = null;
                    try
                    {
                        res = bridge.Call(fn, string.IsNullOrEmpty(arg) ? null : arg, 1, 4000);
                    }
                    catch (LuaTakeoverException ex)
                    {
                        // the BRIDGE could not run this on the client's Lua thread, so nothing in the
                        // VM was touched - and no later step can succeed either
                        sw.Stop();
                        Console.WriteLine("[seq " + step + "]     BRIDGE could not take the Lua thread over at this step - stopping:");
                        Console.WriteLine("            " + ex.Message.Replace(Environment.NewLine, Environment.NewLine + "            "));
                        return 8;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // the handler ran and the CLIENT refused it (a Lua error, e.g. a protected
                        // function): the command was served, so the payload is healthy and the
                        // remaining steps are still meaningful - keep going
                        sw.Stop();
                        luaErrors++;
                        Console.WriteLine("[seq " + step + "]     " + fn + "(" + (arg ?? string.Empty) +
                                          ") -> the client's Lua refused it: " + ex.Message);
                        continue;
                    }
                    sw.Stop();
                    served++;
                    string shown = string.Empty;
                    for (int k = 0; k < res.Length; k++)
                    {
                        if (k > 0) shown += ", ";
                        shown += res[k].ToString();
                    }
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "[seq {0}]     {1}({2}) = {3}   {4:F2} ms wall / {5:F2} ms frozen in the call",
                        step, fn, arg ?? string.Empty, shown, sw.Elapsed.TotalMilliseconds, bridge.LastCallMilliseconds));
                }

                Console.WriteLine("[seq]       " + served + " call(s) returned, " + luaErrors +
                                  " refused by the client's Lua; guard: " + bridge.GuardSuspends +
                                  " freezes, " + bridge.GuardWaits + " waits, " + bridge.GuardRetries + " retries");
                bridge.Stop();
                Console.WriteLine("[stop]      the resident thread exited on command - no TerminateThread was used");
                Process alive = Process.GetProcessById(pid);
                Console.WriteLine("[alive]     pid " + alive.Id + " is still running (" + alive.Threads.Count + " threads)");
                return 0;
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine();
                Console.WriteLine("[seq " + step + "]     TIMED OUT - " + ex.Message);
                PrintWedgeAdvice(bridge);
                return 6;
            }
        }

        /// <summary>
        /// handler RVAs for the build 70291 client, read out of the registration table
        /// (hunt/api_handlers.tsv). Only used to pre-check warmth before a --call.
        /// </summary>
        private static readonly Dictionary<string, ulong> HandlerRvas = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            { "UnitHealth", 0x1D63190 },
            { "UnitName", 0x1D7B840 },
            { "UnitExists", 0x1D596F0 },
            { "UnitGUID", 0x1D5AD60 },
            { "UnitLevel", 0x1D7B330 },
            { "UnitClass", 0x1D4EDE0 },
            { "UnitIsDeadOrGhost", 0x1D6CD20 },
            { "UnitPosition", 0x1D82040 },
            { "GetTime", 0x1B2E050 },
            { "GetMoney", 0x18C8920 },
            { "IsLoggedIn", 0x18E0180 },
            { "GetFramerate", 0x10183D0 },
            { "GetZoneText", 0x1E7C7D0 },
            { "FollowUnit", 0x18BA5D0 },
            { "TargetUnit", 0x1B46C60 },
            { "RunScript", 0x136FFF0 },
            { "CastSpellByName", 0x2C24040 },
            { "RunMacro", 0x2CC2DB0 },
            { "MoveForwardStart", 0x23C8BE0 },
            // ---- the movement/binding set -------------------------------------------------
            // hunt31 walked these registration tables LIVE and the static dump agreed (the
            // movement table is 58 entries at module+0x58CF2B0), so these RVAs are evidence,
            // not guesses. The table is only used for the cold-page pre-check: the call itself
            // always goes by NAME through the client's own globals.
            { "MoveForwardStop", 0x23C9000 },
            { "MoveBackwardStart", 0x23C9410 },
            { "MoveBackwardStop", 0x23C9830 },
            { "TurnLeftStart", 0x23C9C40 },
            { "TurnLeftStop", 0x23CA060 },
            { "TurnRightStart", 0x23CA470 },
            { "TurnRightStop", 0x23CA890 },
            { "StrafeLeftStart", 0x23CACA0 },
            { "StrafeLeftStop", 0x23CB0D0 },
            { "StrafeRightStart", 0x23CB500 },
            { "StrafeRightStop", 0x23CB930 },
            { "PitchUpStart", 0x23CBD60 },
            { "PitchUpStop", 0x23CC180 },
            { "PitchDownStart", 0x23CC590 },
            { "PitchDownStop", 0x23CC9B0 },
            { "JumpOrAscendStart", 0x23C5560 },
            { "AscendStop", 0x23C6840 },
            { "DescendStop", 0x23C7160 },
            { "ToggleRun", 0x23C76C0 },
            { "ToggleAutoRun", 0x23C7F50 },
            { "StartAutoRun", 0x23C83A0 },
            { "StopAutoRun", 0x23C87C0 },
            { "MouselookStart", 0x23D1110 },
            { "MouselookStop", 0x23D1580 },
            { "IsMouselooking", 0x23D1A40 },
            { "SetMoveEnabled", 0x23D9340 },
            { "SetTurnEnabled", 0x23D97E0 },
        };

        /// <summary>
        /// Handlers the hunt37/hunt38 survey found still encrypted - worth saying out loud.
        /// MoveForwardStart is listed after the 2026-10-13 run refused it: hunt37's map had it
        /// warm, the live reading later that day and this run both say cold. Warmth is a sample
        /// of one moment, never a build property - which is why the gate reads the page.
        /// </summary>
        private static readonly string[] ColdInSurvey =
        {
            "TargetUnit", "RunScript", "CastSpellByName", "RunMacro", "GetNumCompanions",
            "FnEntityGetGuid", "MoveForwardStart", "MoveForwardStop"
        };

        /// <summary>
        /// The chat line that makes the CLIENT execute a handler - which is what decrypts its
        /// page. Nothing else warms a page: not pressing keys (the binding's code path and the
        /// Lua handler are different pages - the 2026-10-13 session had movement in it and
        /// MoveForwardStart was still cold), and not a protection change.
        /// </summary>
        private static string WarmHint(string name)
        {
            if (string.Equals(name, "RunScript", StringComparison.OrdinalIgnoreCase)) return "/run print(1)";
            if (string.Equals(name, "TargetUnit", StringComparison.OrdinalIgnoreCase)) return "/run " + name + "('player')";
            if (name.StartsWith("Cast", StringComparison.OrdinalIgnoreCase)) return "/run " + name + "('Fireball')";
            if (string.Equals(name, "RunMacro", StringComparison.OrdinalIgnoreCase)) return "/run " + name + "('1')";
            if (name.EndsWith("Start", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Stop", StringComparison.OrdinalIgnoreCase))
                return "/run " + name + "()";
            return "/run " + name + "(<the arguments the game would pass>)";
        }

        private static ulong KnownHandlerRva(string name)
        {
            ulong rva;
            return HandlerRvas.TryGetValue(name, out rva) ? rva : 0;
        }

        private static bool WasColdInSurvey(string name)
        {
            for (int i = 0; i < ColdInSurvey.Length; i++)
                if (string.Equals(ColdInSurvey[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Usage()
        {
            Console.WriteLine("LuaBridgeProbe - read live state out of a native x64 WoW client through the resident Lua bridge");
            Console.WriteLine();
            Console.WriteLine("  --pid N          target process id (default: the first process whose name starts with --name)");
            Console.WriteLine("  --name wowb      process name to look for (default wowb)");
            Console.WriteLine("  --seconds 30     how long to keep reading");
            Console.WriteLine("  --interval 500   milliseconds between rounds");
            Console.WriteLine("  --call Name      call ONE Lua global now and print the result, then stop (guarded)");
            Console.WriteLine("  --arg TEXT       the single argument for --call (omit for a no-argument call)");
            Console.WriteLine("  --nresults 1     how many results --call captures (1 or 2)");
            Console.WriteLine("  --seq \"A ; sleep=1200 ; B\"   several guarded calls in ONE session, in order");
            Console.WriteLine("                   (steps are Name or Name=arg separated by ';', sleep=MS pauses)");
            Console.WriteLine("  --dump A:L,...   READ-ONLY dump of va:len ranges (hex). A value below the image base is");
            Console.WriteLine("                   read as a MODULE RVA (adds the base); the binary goes to --out with a .txt");
            Console.WriteLine("                   manifest beside it. No thread is started, nothing is written to the client.");
            Console.WriteLine("  --out FILE       where --dump writes (default bbuddy-dump.bin)");
            Console.WriteLine("  --dump-text      print the bytes to the console instead of writing the file");
            Console.WriteLine("  --rva 0x1234     handler RVA for the cold-page pre-check when the name is unknown");
            Console.WriteLine("  --force          call even when the pre-check says the handler is still encrypted");
            Console.WriteLine("  --each Name=arg  also call one extra Lua function per round (the old --call)");
            Console.WriteLine("  --verbose        per-round timing and the payload's own call counter");
            Console.WriteLine("  --check          attach and verify (canary, gadget, kernel32, trampoline, Lua thread) but write nothing and start no thread");
            Console.WriteLine("  --on-lua-thread  run each command ON the client's own Lua thread (required for functions that");
            Console.WriteLine("                   raise FrameScript events - movement, target, cast; the injected thread cannot do those)");
            Console.WriteLine("  --timeout-ms N   how long a command may take in that mode before the bridge gives up (default 1000)");
            Console.WriteLine("  --no-guard       do not freeze the client's Lua thread around each call (races it - useful only to reproduce a crash)");
            Console.WriteLine("  --lua-thread N   freeze this thread id instead of the automatically found one (see the sampling table)");
            Console.WriteLine("  --selftest       do not touch any process; check the embedded payload and the known addresses");
        }

        private static int SelfTest()
        {
            Console.WriteLine("embedded payload : " + LuaBridge64.PayloadSize + " bytes (must be 641)");
            bool ok = LuaBridge64.PayloadSize == 641;
            Console.WriteLine("known addresses  : lua_State slot module+0x7B76AB8, getfield 0x471A370, pushlstring 0x471BC20,");
            Console.WriteLine("                   pcall 0x471B5C0, trampoline X 0x471A983 (build 70291 only)");
            Console.WriteLine(ok ? "selftest ok" : "selftest FAILED");
            return ok ? 0 : 1;
        }

        private static void Stats(List<double> latency, int calls, double seconds)
        {
            if (latency.Count == 0) return;
            double[] sorted = latency.ToArray();
            Array.Sort(sorted);
            double sum = 0;
            for (int i = 0; i < sorted.Length; i++) sum += sorted[i];
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[stats]     {0} rounds, {1} calls in {2:F1} s  ->  {3:F1} calls/s",
                sorted.Length, calls, seconds, calls / Math.Max(seconds, 0.001)));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[stats]     per round: min {0:F2} ms, p50 {1:F2} ms, avg {2:F2} ms, max {3:F2} ms (7 calls each)",
                sorted[0], sorted[sorted.Length / 2], sum / sorted.Length, sorted[sorted.Length - 1]));
        }

        private static string Hex(byte[] data, int offset, int count)
        {
            StringBuilder sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[offset + i].ToString("x2"));
            }
            return sb.ToString();
        }

        private static string Md5Hex(byte[] data)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(data);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string ShortGuid(string guid)
        {
            if (guid == null) return "(none)";
            return guid.Length <= 20 ? guid : guid.Substring(guid.Length - 20);
        }

        private static string Join(LuaValue[] values)
        {
            string s = string.Empty;
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) s += ", ";
                s += values[i].ToString();
            }
            return s;
        }

        private static bool Has(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Arg(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static int ToInt(string text, int fallback)
        {
            int value;
            if (text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            return fallback;
        }

        /// <summary>Accepts 0x1234 or 1234 (hex first - RVAs are printed in hex everywhere here).</summary>
        private static ulong ToU64(string text)
        {
            if (text == null) return 0;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
            ulong value;
            if (ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return value;
            if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            return 0;
        }
    }
}
