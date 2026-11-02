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
        internal const ulong MouseoverGuidCandidate = 0x51C7A78; // static counterpart of 70291 hover GUID; live transition not yet validated
        internal const ulong ObjectManager = 0x6D20538;
        internal const ulong HandlePools = 0x481CBE0;
        internal const ulong MapRoot = 0x6D204B8;
        internal const ulong MapIdCandidateOffset = 0x20; // static counterpart; diagnostic only
        internal const ulong CameraWorldFrameGlobal = 0x4F48610;
        internal const ulong CameraPointerOffset = 0x480;
        internal const ulong CameraZoomOffset = 0x274;
        internal const ulong PlayerVtable = 0x41DA390; // one player observation
        internal const ulong UnitVtable = 0x41EE168; // one selected-unit observation
        internal const ulong GameObjectVtableCandidate = 0x41E1480; // static counterpart; not yet observed live
        internal const ulong HealthCandidateOffset = 0x1E28; // static path + two reported current HP values
        internal const ulong MaxHealthCandidateOffset = 0x1E30; // static path; both observations were equal
        internal const ulong LevelCandidateOffset = 0x1E38; // static path + two user-confirmed levels
        internal const ulong FactionKeyCandidateOffset = 0x1E54; // static reaction path only
        internal const ulong StoredActionArrayCandidate = 0x6D3EC60; // static references only, read-only
        internal const ulong PowerArrayCandidateOffset = 0x1BC0; // indexed; power type mapping unverified
        internal const ulong MaxPowerArrayCandidateOffset = 0x20F0; // indexed; power type mapping unverified
    }
}
