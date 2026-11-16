#if WOWB_READER_TESTS
using System;
using Styx.Logic.Pathing;
using Styx.Offsets;

internal static class PlayerState70938Tests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("70938 supplied offsets: " + message); }
    internal static void Run()
    {
        var chunks = new byte[]?[0x28];
        for (int i = 0; i < chunks.Length; i++) chunks[i] = new byte[0x100];
        void Put(int offset, byte[] bytes) => Array.Copy(bytes, 0, chunks[offset / 0x100]!, offset % 0x100, bytes.Length);
        Put(Wow70938Offsets.AutoAttackCandidate, new byte[] { 1 });
        Put(Wow70938Offsets.CurrentTargetCandidate, BitConverter.GetBytes(0x123456789ABCDEF0UL));
        Put(Wow70938Offsets.AttackTargetCandidate, BitConverter.GetBytes(0x456789ABCDEF1234UL));
        Put(Wow70938Offsets.ShapeshiftFlagsCandidate, BitConverter.GetBytes(0x10000000U));
        Put(Wow70938Offsets.ShapeshiftCandidate, new byte[] { 3 });
        Put(Wow70938Offsets.ComboPointsCandidate, new byte[] { 255 });
        Put(Wow70938Offsets.AttackStateFlagsCandidate, BitConverter.GetBytes(0x16U));
        Put(Wow70938Offsets.AttackEffectCandidate, BitConverter.GetBytes(0x1F23456789ABCDEFUL));
        Put((int)Wow70707Offsets.HealthCandidateOffset, BitConverter.GetBytes(90UL));
        Put((int)Wow70707Offsets.MaxHealthCandidateOffset, BitConverter.GetBytes(100UL));
        byte[] hp = new byte[16]; BitConverter.GetBytes(90UL).CopyTo(hp, 0); BitConverter.GetBytes(100UL).CopyTo(hp, 8);
        string result = WowBPlayerState70938.Format(chunks, hp);
        foreach (string value in new[] { "attackByte=0x01", "currentTargetQword=0x123456789ABCDEF0", "alternateAttackTargetQword=0x456789ABCDEF1234", "formByte=0x03", "formGate=set", "comboByte=0xFF comboSigned=-1", "attackFlags=0x00000016", "attackEffectPointer=0x1F23456789ABCDEF", "hp=90/100, plausible=True, rereadMatch=True" })
            Check(result.Contains(value), value);
        hp[0] = 89;
        Check(WowBPlayerState70938.Format(chunks, hp).Contains("rereadMatch=False"), "health reread mismatch");
        Put(Wow70938Offsets.ShapeshiftFlagsCandidate, BitConverter.GetBytes(0U));
        Check(WowBPlayerState70938.Format(chunks, hp).Contains("formGate=clear"), "cleared gate");
        chunks[0x1E] = null; chunks[0x02] = null; chunks[0x25] = new byte[4];
        result = WowBPlayerState70938.Format(chunks, hp);
        Check(result.Contains("attackByte=unreadable") && result.Contains("formGate=unreadable") &&
              result.Contains("comboByte=unreadable comboSigned=unreadable") && result.Contains("attackFlags=unreadable") &&
              result.Contains("pool-base health check: unreadable"), "missing and short chunks stay unknown");
        Check(WowBPlayerState70938.Format(Array.Empty<byte[]?>(), null).Contains("attackByte=unreadable"), "short window");
    }
}
#endif
