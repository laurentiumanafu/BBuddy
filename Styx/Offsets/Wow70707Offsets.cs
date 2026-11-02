namespace Styx.Offsets
{
    /// <summary>
    /// Experimental 70707 image-relative candidates. Vtables have one live
    /// player/selected-unit observation; member layouts and safe gameplay are
    /// not established. Selected only when attached to build 70707.
    /// </summary>
    internal static class Wow70707Offsets
    {
        internal const ulong PlayerGuid = 0x5110D50;
        internal const ulong PlayerNamePrimary = 0x5110D60;
        internal const ulong PlayerTargetGuid = 0x6B1DFB8;
        internal const ulong ObjectManager = 0x6D20538;
        internal const ulong HandlePools = 0x481CBE0;
        internal const ulong MapRoot = 0x6D204B8;
        internal const ulong MapIdCandidateOffset = 0x20; // static counterpart; diagnostic only
        internal const ulong CameraWorldFrameGlobal = 0x4F48610;
        internal const ulong CameraPointerOffset = 0x480;
        internal const ulong CameraZoomOffset = 0x274;
        internal const ulong PlayerVtable = 0x41DA390; // one player observation
        internal const ulong UnitVtable = 0x41EE168; // one selected-unit observation
        internal const ulong FactionKeyCandidateOffset = 0x1E54; // static reaction path only
    }
}
