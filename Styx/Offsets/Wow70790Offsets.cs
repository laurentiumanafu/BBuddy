namespace Styx.Offsets
{
    /// <summary>
    /// Static candidates from decoded 1.60.1.70790. No live validation; layouts
    /// inherited from 70707/70755 remain experimental and may have changed.
    /// </summary>
    internal static class Wow70790Offsets
    {
        internal const ulong PlayerGuid = 0x51B3E70;
        internal const ulong PlayerNamePrimary = 0x51B3E80;
        internal const ulong PlayerTargetGuid = 0x6BC5BF8;
        internal const ulong MouseoverGuidCandidate = 0x526F698;
        internal const ulong ObjectManager = 0x6DC8178;
        internal const ulong HandlePools = 0x48BFBE0;
        internal const ulong MapRoot = 0x6DC80F8;
        internal const ulong CameraWorldFrameGlobal = 0x4FEB5F0;
        internal const ulong PlayerVtable = 0x427CAF8;
        internal const ulong UnitVtable = 0x4290AD8; // inferred from shared method layout, not independently verified
        internal const ulong GameObjectVtableCandidate = 0x4284398;
        internal const ulong StoredActionArrayCandidate = 0x6DE68C0;
        internal const ulong ContainerIdentityTableCandidate = 0x6DE9900;
        internal const ulong InventoryLookup = 0x21FA4C0;
        internal const ulong InventoryHandler = 0x27696E0;
    }
}
