namespace Styx.Offsets
{
    /// <summary>
    /// Experimental 70707 image-relative candidates from static analysis only.
    /// No member layouts, vtables or safe gameplay behavior have been verified.
    /// Selected only for an explicitly opted-in 70707 attachment.
    /// </summary>
    internal static class Wow70707Offsets
    {
        internal const ulong PlayerGuid = 0x5110D50;
        internal const ulong PlayerNamePrimary = 0x5110D60;
        internal const ulong PlayerTargetGuid = 0x6B1DFB8;
        internal const ulong ObjectManager = 0x6D20538;
        internal const ulong HandlePools = 0x481CBE0;
        internal const ulong MapRoot = 0x6D204B8;
        internal const ulong CameraWorldFrameGlobal = 0x4F48610;
        internal const ulong CameraPointerOffset = 0x480;
        internal const ulong CameraZoomOffset = 0x274;
    }
}
