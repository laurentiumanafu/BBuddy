using System;
using GreenMagic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Read-only active-session candidate; never calls the client or sends input.
// Caller's safety predicate must check combat, death, map, focus and session.
internal static class WowBMailCandidateReader
{
    // 70938 leads. 70959 has its own image-relative pair, derived the same way.
    private const ulong TableRva70938 = 0x4E6CA90, MarkerRva70938 = 0x26B4F78;
    private const string Marker70938 = "FFC883F8107338488D0C80488D05067B7B02488D04C84885C0";
    private const ulong TableRva70959 = 0x4D66A60, MarkerRva70959 = 0x25B1AA8;
    private const string Marker70959 = "FFC883F8107338488D0C80488D05A64F7B02488D04C84885C0";
    private static ulong TableRva(WowBReader reader) => reader.IsExperimental70959 ? TableRva70959 : TableRva70938;
    private static ulong MarkerRva(WowBReader reader) => reader.IsExperimental70959 ? MarkerRva70959 : MarkerRva70938;
    private static string Marker(WowBReader reader) => reader.IsExperimental70959 ? Marker70959 : Marker70938;
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
        bool Current() => reader.Uses70938Features && reader.ExperimentalGrindActive && ClickAimUi.Running &&
            !ClickAimUi.Paused && !ClickAimAbort.Requested && !RouteRunner.StopRequested &&
            ObjectManager.Wow == memory && ObjectManager.WowB == reader && safe();
        try
        {
            using var uncached = memory.TemporaryCacheState(false);
            if (!Current() || !WowBGrindFactions.TraceBuild70707(memory, reader) ||
                !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? world) || world == null ||
                !world.TryGetLocalPlayer(out WowBObservedUnit player) || player.Guid != expected ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)) return false;
            ulong address = memory.GetAbsolute64(TableRva(reader)), marker = memory.GetAbsolute64(MarkerRva(reader));
            byte[]? code = memory.ReadBytes64(marker, Marker(reader).Length / 2);
            if (code == null || Convert.ToHexString(code) != Marker(reader) ||
                !memory.IsCommittedReadableRange64(address, WowBMailCandidateTable.Size)) return false;
            byte[]? first = memory.ReadBytes64(address, WowBMailCandidateTable.Size);
            byte[]? second = memory.ReadBytes64(address, WowBMailCandidateTable.Size);
            if (first == null || first.Length != WowBMailCandidateTable.Size || !Equal(first, second) ||
                !Equal(code, memory.ReadBytes64(marker, Marker(reader).Length / 2)) || !Current() ||
                !WowBGrindFactions.TraceBuild70707(memory, reader) ||
                !WowBGrindFactions.TraceIdentity70707(memory, reader, player, true) || !Current()) return false;
            raw = first; return true;
        }
        catch { return false; }
    }
}
