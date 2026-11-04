namespace Styx.Offsets
{
    /// <summary>
    /// Static candidates from the decoded 1.60.1.70755 image. Not yet live-validated.
    /// Structure layouts and behavior remain experimental and may change independently.
    /// </summary>
    internal static class Wow70755Offsets
    {
        internal const ulong PlayerGuid = 0x5017E50;
        internal const ulong PlayerNamePrimary = 0x5017E60;
        internal const ulong PlayerTargetGuid = 0x6A29BB8;
        internal const ulong MouseoverGuidCandidate = 0x50D3678;
        internal const ulong ObjectManager = 0x6C2C138;
        internal const ulong HandlePools = 0x4723BE0;
        internal const ulong MapRoot = 0x6C2C0B8;
        internal const ulong CameraWorldFrameGlobal = 0x4E4F640;
        internal const ulong PlayerVtable = 0x40E15D8;
        internal const ulong UnitVtable = 0x40F55D8;
        internal const ulong GameObjectVtableCandidate = 0x40EA0F0;
        internal const ulong StoredActionArrayCandidate = 0x6C4A880;
        internal const ulong ContainerIdentityTableCandidate = 0x6C4D8C0;
        internal const ulong InventoryLookup = 0x20733B0;
        internal const ulong InventoryHandler = 0x25D2A70;
    }
}
