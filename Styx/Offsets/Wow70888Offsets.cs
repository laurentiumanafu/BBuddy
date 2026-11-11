namespace Styx.Offsets
{
    /// <summary>
    /// Static candidates from decoded 1.60.1.70888. Shared object, gameplay,
    /// aura and inventory layouts are inherited hypotheses, not live validation.
    /// </summary>
    internal static class Wow70888Offsets
    {
        internal const ulong PlayerGuid = 0x4C1A770;
        internal const ulong PlayerNamePrimary = 0x4C1A780;
        internal const ulong PlayerTargetGuid = 0x6A32478;
        internal const ulong MouseoverGuidCandidate = 0x4CD5F98;
        internal const ulong ObjectManager = 0x6C349F8;
        internal const ulong HandlePools = 0x4728BE0;
        internal const ulong MapRoot = 0x6C34978;
        internal const ulong CameraWorldFrameGlobal = 0x4A51EB0;
        internal const ulong PlayerVtable = 0x40E64F8;
        internal const ulong UnitVtable = 0x40FA570; // inferred; requires live observation
        internal const ulong GameObjectVtableCandidate = 0x40EED10;
        internal const ulong StoredActionArrayCandidate = 0x6C53140;
        internal const ulong ContainerIdentityTableCandidate = 0x6C56180;
        internal const ulong InventoryLookup = 0x2074710;
        internal const ulong InventoryHandler = 0x25D4E70;
    }
}
