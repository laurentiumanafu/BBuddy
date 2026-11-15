using System;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Read-only active-session candidate; never calls the client or sends input.
// Caller's safety predicate must check combat, death, map, focus and session.
internal static class WowBMailCandidateReader
{
    private const ulong TableRva = 0x4E6CA90, MarkerRva = 0x26B4F78;
    private const string Marker = "FFC883F8107338488D0C80488D05067B7B02488D04C84885C0";
    private static bool Equal(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    internal static bool TryRead(Memory memory, WowBReader reader, WowBGuid expected, Func<bool> safe, out byte[]? raw)
    {
        raw = null;
        if (memory == null || reader == null || safe == null || expected.IsEmpty) return false;
        bool Current() => reader.IsExperimental70938 && reader.ExperimentalGrindActive && ClickAimUi.Running &&
            !ClickAimUi.Paused && !ClickAimAbort.Requested && !RouteRunner.StopRequested &&
            ObjectManager.Wow == memory && ObjectManager.WowB == reader && safe();
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!Current() || !WowBGrindFactions.TraceBuild70707(memory, reader) ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? world) || world == null ||
                !world.TryGetLocalPlayer(out WowBObservedUnit player) || player.Guid != expected ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)) return false;
            ulong address = memory.GetAbsolute64(TableRva), marker = memory.GetAbsolute64(MarkerRva);
            byte[]? code = memory.ReadBytes64(marker, Marker.Length / 2);
            if (code == null || Convert.ToHexString(code) != Marker ||
                !memory.IsCommittedReadableRange64(address, WowBMailCandidateTable.Size)) return false;
            byte[]? first = memory.ReadBytes64(address, WowBMailCandidateTable.Size);
            byte[]? second = memory.ReadBytes64(address, WowBMailCandidateTable.Size);
            if (first == null || first.Length != WowBMailCandidateTable.Size || !Equal(first, second) ||
                !Equal(code, memory.ReadBytes64(marker, Marker.Length / 2)) || !Current() ||
                !WowBGrindFactions.TraceBuild70707(memory, reader) ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) || !Current()) return false;
            raw = first; return true;
        }
        catch { return false; }
    }
}
