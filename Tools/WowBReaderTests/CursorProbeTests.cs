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
            Check(WowBCursorProbe.CandidateTransition(false, true), "away then selected GUID");
            Check(!WowBCursorProbe.CandidateTransition(true, true), "stale matching GUID is not transition");
            Check(!WowBCursorProbe.CandidateTransition(null, true), "unreadable away GUID is not transition");
            Check(!WowBCursorProbe.CandidateTransition(false, null), "unreadable return GUID is not transition");
            var actor = new WowBObservedUnit(new WowBGuid(1, 2), 10, 20, false, false, new WoWPoint(1, 2, 3), 0, null);
            var close = new WowBObservedUnit(new WowBGuid(1, 2), 10, 20, false, false, new WoWPoint(1.25f, 2, 3), 0, null);
            var far = new WowBObservedUnit(new WowBGuid(1, 2), 10, 20, false, false, new WoWPoint(2, 2, 3), 0, null);
            var different = new WowBObservedUnit(new WowBGuid(3, 2), 10, 20, false, false, new WoWPoint(1, 2, 3), 0, null);
            var bad = new WowBObservedUnit(new WowBGuid(1, 2), 10, 20, false, false, new WoWPoint(float.NaN, 2, 3), 0, null);
            Check(WowBCursorProbe.SameActor(actor, close), "small actor movement");
            Check(!WowBCursorProbe.SameActor(actor, far) && !WowBCursorProbe.SameActor(actor, different) &&
                  !WowBCursorProbe.SameActor(actor, bad), "movement, identity and NaN reject");
            Check(WowBCursorProbe.ActorChangeReason(actor, close, true) == "", "stationary target has no reason");
            Check(WowBCursorProbe.ActorChangeReason(actor, different, true) == "selected NPC identity changed", "target identity reason");
            Check(WowBCursorProbe.ActorChangeReason(actor, far, true).Contains("selected NPC moved"), "target movement reason");
            Check(WowBCursorProbe.ActorChangeReason(actor, bad, false).Contains("player moved"), "player unreadable position reason");
            Check(!WowBCursorProbe.StartPosted(true, _ => { }), "posted probe requires attachment");
            Check(!WowBCursorProbe.PoolNeedsCursorGate(false, true, false), "opt-out keeps legacy behavior");
            Check(!WowBCursorProbe.PoolNeedsCursorGate(true, false, false), "TAB and other paths not gated");
            Check(WowBCursorProbe.PoolNeedsCursorGate(true, true, false), "unconfirmed pool selection deferred");
            Check(!WowBCursorProbe.PoolNeedsCursorGate(true, true, true), "fresh hint proceeds to existing pool failsafe");
            var expectedGuid = new WowBGuid(123, 456);
            Check(WowBCursorProbe.ExpectedPoolTargetMatches(default, expectedGuid), "legacy fight not subject to pool identity gate");
            Check(WowBCursorProbe.ExpectedPoolTargetMatches(expectedGuid, expectedGuid), "confirmed full GUID stays selected");
            Check(!WowBCursorProbe.ExpectedPoolTargetMatches(expectedGuid, new WowBGuid(123, 457)), "same low GUID with different high rejected");
            Check(!WowBCursorProbe.TryGrindDaggerHover(IntPtr.Zero, default, 0, 0, () => false, out _), "unattached pool hover rejected before native input");
            bool cancelCalled = false;
            ClickAimAbort.Requested = true;
            Check(!WowBCursorProbe.TryGrindDaggerHover(IntPtr.Zero, default, 0, 0, () => { cancelCalled = true; return false; }, out _) &&
                  !cancelCalled, "STOP short-circuits pool hover before native input");
            ClickAimAbort.Requested = false;
            var targetGuid = new WowBGuid(11, 22); var otherGuid = new WowBGuid(33, 44);
            Check(WowBCursorProbe.DaggerTransition(targetGuid, otherGuid, targetGuid, new string('B', 64), bright), "bright dagger transition");
            Check(WowBCursorProbe.DaggerTransition(targetGuid, default, targetGuid, new string('B', 64), dark), "dark dagger transition");
            Check(!WowBCursorProbe.DaggerTransition(targetGuid, targetGuid, targetGuid, new string('B', 64), bright), "stale away rejected");
            Check(!WowBCursorProbe.DaggerTransition(targetGuid, otherGuid, otherGuid, new string('B', 64), bright), "wrong hover rejected");
            Check(!WowBCursorProbe.DaggerTransition(targetGuid, otherGuid, targetGuid, bright, bright), "unchanged cursor rejected");
            Check(!WowBCursorProbe.DaggerTransition(targetGuid, otherGuid, targetGuid, null, bright), "unreadable cursor rejected");
            Check(!WowBCursorProbe.DaggerTransition(targetGuid, otherGuid, targetGuid, new string('B', 64), new string('A', 64)), "unknown is not a dagger");
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
