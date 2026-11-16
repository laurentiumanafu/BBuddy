namespace Styx.Offsets
{
    /// <summary>
    /// Static candidates from decoded 1.60.1.70938. Shared object, gameplay,
    /// aura and inventory layouts are inherited hypotheses, not live validation.
    /// </summary>
    internal static class Wow70938Offsets
    {
        internal const ulong PlayerGuid = 0x4D2EAE0;
        internal const ulong PlayerNamePrimary = 0x4D2EAF0;
        internal const ulong PlayerTargetGuid = 0x6B467F8;
        internal const ulong MouseoverGuidCandidate = 0x4DEA308;
        internal const ulong ObjectManager = 0x6D48D78;
        internal const ulong HandlePools = 0x483CBE0;
        internal const ulong MapRoot = 0x6D48CF8;
        internal const ulong CameraWorldFrameGlobal = 0x4B661F0;
        internal const ulong PlayerVtable = 0x41F8070;
        internal const ulong UnitVtable = 0x420C0D8; // inferred; requires live observation
        internal const ulong GameObjectVtableCandidate = 0x41FFF48;
        internal const ulong StoredActionArrayCandidate = 0x6D674C0;
        internal const ulong ContainerIdentityTableCandidate = 0x6D6A500;
        internal const ulong InventoryLookup = 0x21796E0;
        internal const ulong InventoryHandler = 0x26E1230;

        // User-supplied static leads; getter identity and runtime semantics have
        // not been independently validated against the pool-derived unit pointer.
        // Diagnostic use only. The target values are qwords, NOT WowBGuid128.
        internal const ulong PlayerGetterCandidateRva = 0x1F6EC10;
        internal const int AutoAttackCandidate = 0x2C9;
        internal const int CurrentTargetCandidate = 0x2C0;
        internal const int ShapeshiftFlagsCandidate = 0x2D0;
        internal const uint ShapeshiftFlagCandidate = 0x10000000;
        internal const int AttackTargetCandidate = 0x1D40;
        internal const int ComboPointsCandidate = 0x1E1D;
        internal const int ShapeshiftCandidate = 0x1EEF;
        internal const int AttackStateFlagsCandidate = 0x250C;
        internal const int AttackEffectCandidate = 0x25C8;
    }
}
