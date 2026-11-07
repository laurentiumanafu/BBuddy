namespace Styx.Offsets
{
    /// <summary>
    /// Static candidates from decoded 1.60.1.70862. No live validation; shared
    /// object, gameplay and inventory layouts remain experimental.
    /// </summary>
    internal static class Wow70862Offsets
    {
        internal const ulong PlayerGuid = 0x4C1A770;
        internal const ulong PlayerNamePrimary = 0x4C1A780;
        internal const ulong PlayerTargetGuid = 0x6A32478;
        internal const ulong MouseoverGuidCandidate = 0x4CD5F98;
        internal const ulong ObjectManager = 0x6C349F8;
        internal const ulong HandlePools = 0x4728BE0;
        internal const ulong MapRoot = 0x6C34978;
        internal const ulong CameraWorldFrameGlobal = 0x4A51EB0;
        internal const ulong PlayerVtable = 0x40E6498;
        internal const ulong UnitVtable = 0x40FA510; // inferred; needs live verification
        internal const ulong GameObjectVtableCandidate = 0x40EECB0;
        internal const ulong StoredActionArrayCandidate = 0x6C53140;
        internal const ulong ContainerIdentityTableCandidate = 0x6C56180;
        internal const ulong InventoryLookup = 0x2074700;
        internal const ulong InventoryHandler = 0x25D4E10;
    }
}
