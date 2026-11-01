#if WOWB_READER_TESTS
using System;
using Styx.Logic.Pathing;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing
{
    internal static class CursorProbeTests
    {
        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception("Cursor probe fixture failed: " + message);
        }

        public static void Run()
        {
            Check(WowBCursorProbe.SameCursor(1, 123, 50, 30, 1, 123, 52, 28), "small coordinate tolerance");
            Check(!WowBCursorProbe.SameCursor(1, 123, 50, 30, 1, 123, 53, 30), "cursor moved");
            Check(!WowBCursorProbe.SameCursor(1, 123, 50, 30, 0, 123, 50, 30), "cursor flags changed");
            Check(!WowBCursorProbe.SameCursor(1, 123, 50, 30, 1, 124, 50, 30), "cursor handle changed");
            Check(!WowBCursorProbe.SameCursor(1, 123, int.MinValue, 30, 1, 123, int.MaxValue, 30), "coordinate arithmetic overflow");
            byte[] mask = { 1, 2, 3, 4 }, color = { 5, 6, 7, 8 };
            string fingerprint = WowBCursorProbe.Fingerprint(1, 2, 2, 2, mask, 1, 1, color);
            Check(fingerprint == "B0409BA19FD6594A76BCE0D19A56CEF68DF82751397175B046A45A8F5CF03930", "deterministic SHA256");
            Check(fingerprint != WowBCursorProbe.Fingerprint(2, 2, 2, 2, mask, 1, 1, color), "hotspot affects fingerprint");
            color[0] = 99;
            Check(fingerprint != WowBCursorProbe.Fingerprint(1, 2, 2, 2, mask, 1, 1, color), "pixels affect fingerprint");
            const string bright = "A3672FD4057306DCC48DADA960AE5F4BA98C0D111FC1D5A733B67F36F9FA1587";
            const string dark = "90D89DC029CDCC1BD699CA22E46676485C43AEEF015EBC476FB539E5613039E3";
            Check(WowBCursorProbe.DaggerHint("neutral_attackable", true, true, bright) == "observed_highlighted_dagger", "bright known hint");
            Check(WowBCursorProbe.DaggerHint("aggressive", true, true, dark) == "observed_dark_dagger", "dark known hint");
            Check(WowBCursorProbe.DaggerHint("friendly", true, true, dark) == "observed_dark_dagger", "hint does not trust labels");
            Check(WowBCursorProbe.DaggerHint("off_target_control", true, true, dark) == "unknown", "off target cannot prove dagger on selection");
            Check(WowBCursorProbe.DaggerHint("aggressive", false, true, dark) == "unknown", "hidden cursor");
            Check(WowBCursorProbe.DaggerHint("aggressive", true, false, dark) == "unknown", "mouseover mismatch");
            Check(WowBCursorProbe.DaggerHint("aggressive", true, true, null) == "unknown", "missing image");
            Check(WowBCursorProbe.DaggerHint("friendly", true, true, new string('A', 64)) == "unknown", "other cursor is not classified friendly");
            Check(!WowBCursorProbe.Start("other", _ => { }), "unknown label not started");
            Check(!WowBCursorProbe.Start("friendly", _ => { }), "no attachment not started");
            ClickAimUi.Running = true;
            Check(!WowBCursorProbe.Start("friendly", _ => { }), "running bot not started");
            ClickAimUi.Running = false; ClickAimUi.Paused = true;
            Check(!WowBCursorProbe.Start("friendly", _ => { }), "paused bot not started");
            ClickAimUi.Paused = false;
        }
    }
}
#endif
