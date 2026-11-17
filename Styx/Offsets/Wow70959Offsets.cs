namespace Styx.Offsets
{
    /// <summary>
    /// Static candidates for decoded 1.60.1.70959, derived from 1.60.1.70938 by
    /// carrying each code cross-reference across the two images rather than by
    /// applying a delta: for every recorded global the referencing instructions
    /// were located in the 70938 image, matched as masked byte windows in the
    /// 70959 image, and their RIP-relative displacements resolved again. Where
    /// several independent sites agreed the result is strong; where one site
    /// matched uniquely it is a single lead. Shared object, gameplay, aura and
    /// inventory layouts are inherited hypotheses, not live validation.
    ///
    /// Method validation: the same procedure reproduced the recorded 70938
    /// values for the action array (0x6D674C0), the container identity helper
    /// (0x257E080) and the mail attachment table (0x4E6CA90) before any 70959
    /// number was taken from it. Evidence and per-candidate confidence are in
    /// hunt/WOW_70959_PORT.md; the tool is Tools/WowBBuildPort/derive_build_anchors.py.
    ///
    /// None of these is a live-validated offset. A readable value is not proof of
    /// the field it is read as, and an unreadable one is not a match.
    /// </summary>
    internal static class Wow70959Offsets
    {
        // Many independent referencing sites agreed on each of these.
        internal const ulong PlayerGuid = 0x4C28A30;
        internal const ulong PlayerNamePrimary = 0x4C28A40;
        internal const ulong PlayerTargetGuid = 0x6A407B8;
        internal const ulong MouseoverGuidCandidate = 0x4CE4258;
        internal const ulong ObjectManager = 0x6C42D38;
        internal const ulong HandlePools = 0x4736BE0;
        // MapRoot is one site short of unanimous: 174 of 175 unique carried sites
        // agree, and it keeps the recorded +0x80 distance from ObjectManager.
        internal const ulong MapRoot = 0x6C42CB8;
        internal const ulong CameraWorldFrameGlobal = 0x4A60170;
        internal const ulong StoredActionArrayCandidate = 0x6C61480;
        internal const ulong ContainerIdentityTableCandidate = 0x6C644C0;

        // Vtables: two referencing sites each. Votes do not prove object type.
        internal const ulong PlayerVtable = 0x40F23A8;
        internal const ulong UnitVtable = 0x4106190; // inferred; requires live observation
        internal const ulong GameObjectVtableCandidate = 0x40FA278;

        // InventoryLookup came from 83 independent call sites. InventoryHandler
        // has no direct call sites in either image: it comes from its recorded
        // prologue, which is unique in the 70959 image.
        internal const ulong InventoryLookup = 0x207EF20;
        internal const ulong InventoryHandler = 0x25DDD60;

        // User-supplied static leads, re-derived in this image: the getter is
        // reached by 2265 call sites in 70938 and the same window in 70959. The
        // member offsets below were re-read inside the carried functions, not
        // assumed: every one of them is byte-identical to 70938.
        // Diagnostic use only. The target values are qwords, NOT WowBGuid128.
        internal const ulong PlayerGetterCandidateRva = 0x1E74780;
        internal const int AutoAttackCandidate = 0x2C9;
        internal const int CurrentTargetCandidate = 0x2C0;
        internal const int ShapeshiftFlagsCandidate = 0x2D0;
        internal const uint ShapeshiftFlagCandidate = 0x10000000;
        internal const int AttackTargetCandidate = 0x1D40;
        // Combo points: the reader functions carry no direct call sites, so the
        // value is inherited. The 0x1E1D field encoding is still present in the
        // 70959 image (six movsx and four movzx forms), which is weaker evidence
        // than the others on this list.
        internal const int ComboPointsCandidate = 0x1E1D;
        internal const int ShapeshiftCandidate = 0x1EEF;
        internal const int AttackStateFlagsCandidate = 0x250C;
        internal const int AttackEffectCandidate = 0x25C8;
    }
}
