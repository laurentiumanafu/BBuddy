using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Read-only evidence collection. No Lua, casts, keyboard/mouse input, pointer
// chasing or gameplay offset promotion. The raw windows may miss the state.
internal static class WowBCombatResearch
{
    internal const int WindowSize = 0x2800, ChunkSize = 0x100;
    private const ulong UserLimit = 0x00007FFFFFFFFFFFUL;
    private static int _running;
    internal static bool Running => Volatile.Read(ref _running) != 0;

    private static bool Stopped => !ClickAimUi.Running && !ClickAimUi.Paused;

    private static bool SameSelection(WowBReader reader, WowBGuid player, WowBGuid target, out string reason)
    {
        reason = "local player GUID unavailable or changed";
        if (!reader.TryReadTraceLocalGuid(out WowBGuid local) || local != player) return false;
        reason = "selected target GUID unavailable or changed";
        if (!reader.TryReadTraceTargetGuid(out WowBGuid selected) || selected != target) return false;
        reason = "";
        return true;
    }

    private static bool SameBar(WowBReader reader, List<WowBReader.WowBActionSlot> expected)
    {
        if (!reader.TryReadExperimentalActionPreview(out List<WowBReader.WowBActionSlot> current) || current.Count != expected.Count) return false;
        for (int i = 0; i < current.Count; i++)
            if (current[i].Index != expected[i].Index || current[i].Raw != expected[i].Raw) return false;
        return true;
    }

    private static bool Stable(Memory memory, WowBReader reader, WowBObservedUnit player,
        WowBObservedUnit? target, WowBGuid selected, out string reason)
    {
        reason = "attachment changed";
        if (ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
        reason = "bot or route started or paused";
        if (!Stopped) return false;
        if (!SameSelection(reader, player.Guid, selected, out reason)) return false;
        reason = "local player identity or vtable unavailable or changed";
        if (!WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)) return false;
        reason = "selected NPC identity or vtable unavailable or changed";
        if (target.HasValue && !WowBGrindFactions.TraceIdentity70707(memory, reader, target.Value, false)) return false;
        reason = "bot or route started or paused";
        if (!Stopped) return false;
        if (!SameSelection(reader, player.Guid, selected, out reason)) return false;
        reason = "attachment changed";
        if (ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
        reason = "";
        return true;
    }

    private static bool StableWithBar(Memory memory, WowBReader reader, WowBObservedUnit player,
        WowBObservedUnit? target, WowBGuid selected, List<WowBReader.WowBActionSlot> bar, out string reason)
    {
        if (!Stable(memory, reader, player, target, selected, out reason)) return false;
        if (!SameBar(reader, bar)) { reason = "action-bar preview unavailable or changed"; return false; }
        return true;
    }

    // One bounded read in the usual case; if a page is unreadable, retain only
    // individually readable chunks, never substitute zero for a failed read.
    internal static byte[]?[] ReadWindow(Memory memory, ulong address)
    {
        var chunks = new byte[]?[WindowSize / ChunkSize];
        if (address < 0x10000 || address > UserLimit - WindowSize) return chunks;
        byte[]? full = memory.ReadBytes64(address, WindowSize);
        if (full != null && full.Length == WindowSize)
        {
            for (int i = 0; i < chunks.Length; i++)
            {
                chunks[i] = new byte[ChunkSize];
                Buffer.BlockCopy(full, i * ChunkSize, chunks[i]!, 0, ChunkSize);
            }
        }
        else
        {
            for (int i = 0; i < chunks.Length; i++)
            {
                byte[]? chunk = memory.ReadBytes64(address + (ulong)(i * ChunkSize), ChunkSize);
                if (chunk != null && chunk.Length == ChunkSize) chunks[i] = chunk;
            }
        }
        return chunks;
    }

    // Each line is a baseline, changes relative to the prior readable sample,
    // or a read failure. Bytes and offsets are uninterpreted little-endian.
    internal static string Diff(byte[]?[]? previous, byte[]?[] current, string label)
    {
        if (current.Length != WindowSize / ChunkSize || (previous != null && previous.Length != current.Length))
            throw new ArgumentException("unexpected trace window size");
        var text = new StringBuilder();
        for (int i = 0; i < current.Length; i++)
        {
            byte[]? now = current[i], old = previous == null ? null : previous[i];
            string prefix = label + "+0x" + (i * ChunkSize).ToString("X4", CultureInfo.InvariantCulture);
            if (now == null || now.Length != ChunkSize)
            {
                if (previous == null || old != null) text.Append(prefix).AppendLine(" unreadable");
                continue;
            }
            if (old == null || old.Length != ChunkSize)
            {
                text.Append(prefix).Append(" baseline ").AppendLine(Convert.ToHexString(now));
                continue;
            }
            StringBuilder? changes = null;
            for (int j = 0; j < ChunkSize; j += 4)
            {
                uint a = BitConverter.ToUInt32(old, j), b = BitConverter.ToUInt32(now, j);
                if (a == b) continue;
                changes ??= new StringBuilder();
                changes.Append(" +0x").Append((i * ChunkSize + j).ToString("X4", CultureInfo.InvariantCulture))
                       .Append(':').Append(a.ToString("X8", CultureInfo.InvariantCulture))
                       .Append('>').Append(b.ToString("X8", CultureInfo.InvariantCulture));
            }
            if (changes != null) text.Append(label).Append(" changes").Append(changes).AppendLine();
        }
        return text.ToString();
    }

    // Shorter bounded parameters allow the same production capture path to be
    // exercised in synthetic fixtures. The UI uses 100 x 250 ms (25 seconds).
    internal static string Capture(string label, CancellationToken cancel, int samples = 100, int intervalMs = 250, Action? onStarted = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return "A combat research capture is already running.";
        string? path = null;
        try
        {
            if (samples < 1 || samples > 120 || intervalMs < 10 || intervalMs > 1000) return "Invalid capture limits.";
            if (!Stopped) return "Stop the bot and routes before combat research.";
            Memory? memory = ObjectManager.Wow;
            WowBReader? reader = ObjectManager.WowB;
            if (memory == null || reader == null || !memory.Is64BitTarget || !reader.IsExperimental70707)
                return "Attach to a 70707, 70755 or 70790 client and enter the world first.";
            using var uncached = memory.TemporaryCacheState(false);
            if (!WowBGrindFactions.TraceBuild70707(memory, reader) ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? snapshot) || snapshot == null ||
                !snapshot.TryGetLocalPlayer(out WowBObservedUnit player))
                return reader.ExperimentalBuildNumber + " signatures or local player unavailable; capture not started.";
            WowBObservedUnit? target = null;
            if (!snapshot.TargetGuid.IsEmpty && snapshot.TryGetSelectedTarget(out WowBObservedUnit selectedUnit) &&
                !selectedUnit.IsLocalPlayer && !selectedUnit.HasPlayerVtable)
                target = selectedUnit;
            if (!Stable(memory, reader, player, target, snapshot.TargetGuid, out string why))
                return "Capture not started: " + why + ".";
            if (!reader.TryReadExperimentalActionPreview(out var bar)) return "Action-bar preview unreadable or changed; capture not started.";
            if (cancel.IsCancellationRequested) return "Capture cancelled before recording.";
            if (!StableWithBar(memory, reader, player, target, snapshot.TargetGuid, bar, out why))
                return "Capture not started: " + why + ".";

            string cleanLabel = (label ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (cleanLabel.Length > 80) cleanLabel = cleanLabel.Substring(0, 80);
            string folder = System.IO.Path.Combine(Logging.ApplicationPath, "Logs", "CombatResearch");
            Directory.CreateDirectory(folder);
            path = System.IO.Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".txt");
            int saved = 0;
            string reason = "completed";
            using (var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Encoding.UTF8))
            {
                output.WriteLine(reader.ExperimentalBuildNumber + " COMBAT RESEARCH v1: raw observations only; not verified casting, cooldown or aura state.");
                output.WriteLine("UTC " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "; label=" + cleanLabel);
                output.WriteLine("player=" + player.Guid + " unit=0x" + player.UnitAddress.ToString("X", CultureInfo.InvariantCulture) +
                    " record=0x" + player.RecordAddress.ToString("X", CultureInfo.InvariantCulture) + "; target=" + snapshot.TargetGuid +
                    (target.HasValue ? " targetUnit=0x" + target.Value.UnitAddress.ToString("X", CultureInfo.InvariantCulture) : " target window not captured"));
                output.WriteLine("Player and optional selected NPC: unit+0x0000..0x27FF; 256-byte chunks; baseline hex bytes or 32-bit LE changes; unreadable != zero.");
                output.WriteLine("Reads are non-atomic; game events, effects and other causes can change fields. No keyboard, mouse, Lua or game writes were used.");
                foreach (var slot in bar)
                    output.WriteLine("bar slot=" + slot.Index.ToString(CultureInfo.InvariantCulture) + " raw=0x" + slot.Raw.ToString("X16", CultureInfo.InvariantCulture));
                var watch = Stopwatch.StartNew();
                onStarted?.Invoke();
                byte[]?[]? priorPlayer = null, priorTarget = null;
                for (int i = 0; i < samples; i++)
                {
                    if (cancel.IsCancellationRequested) { reason = "cancelled"; break; }
                    if (!StableWithBar(memory, reader, player, target, snapshot.TargetGuid, bar, out why))
                    { reason = "state changed before read (" + why + "); last sample discarded"; break; }
                    long started = watch.ElapsedMilliseconds;
                    byte[]?[] playerBytes = ReadWindow(memory, player.UnitAddress);
                    byte[]?[]? targetBytes = target.HasValue ? ReadWindow(memory, target.Value.UnitAddress) : null;
                    if (!StableWithBar(memory, reader, player, target, snapshot.TargetGuid, bar, out why))
                    { reason = "state changed during read (" + why + "); last sample discarded"; break; }
                    output.WriteLine("sample=" + i.ToString(CultureInfo.InvariantCulture) + " startMs=" + started.ToString(CultureInfo.InvariantCulture) +
                        " endMs=" + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    output.Write(Diff(priorPlayer, playerBytes, "player"));
                    if (targetBytes != null) output.Write(Diff(priorTarget, targetBytes, "target"));
                    priorPlayer = playerBytes; priorTarget = targetBytes; saved++;
                    int wait = (int)Math.Max(0, intervalMs - (watch.ElapsedMilliseconds - started));
                    if (cancel.WaitHandle.WaitOne(wait)) { reason = "cancelled"; break; }
                }
                output.WriteLine("end: " + reason + "; samples=" + saved.ToString(CultureInfo.InvariantCulture));
            }
            return "Combat research " + reason + ": " + saved.ToString(CultureInfo.InvariantCulture) + " sample(s). Sensitive trace: " + path;
        }
        catch (Exception ex) { return "Combat research could not complete: " + ex.Message + (path == null ? "" : "; a partial sensitive trace may exist: " + path); }
        finally { Volatile.Write(ref _running, 0); }
    }
}
