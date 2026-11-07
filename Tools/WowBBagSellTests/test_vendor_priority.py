"""Static wiring checks; the .NET fixture executes the extracted VendorDue method."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / 'Styx/Logic/Pathing/ClickAim/GrindRunner.cs').read_text(encoding='utf-8-sig')
MONITOR = (ROOT / 'Styx/Logic/Pathing/ClickAim/WowBBagSellMonitor.cs').read_text(encoding='utf-8-sig')


def section(start, end):
    i = SOURCE.index(start)
    return SOURCE[i:SOURCE.index(end, i)]


class VendorPriority(unittest.TestCase):
    def test_confirmed_auto_trip_persists_after_expired_sample(self):
        due = section('    private static bool VendorDue(', '    /// <summary>\n    /// The original CopilotBuddy selling procedure')
        self.assertIn('_vendorAutoPending = true; _vendorAutoPlayer = observed;', due)
        self.assertIn('BagSellMonitor.ObservedPlayer', due)
        pending = due[due.index('if (_vendorAutoPending)'):due.index('            if (retryBlocked) return false;')]
        self.assertIn('current.IsEmpty', pending)
        self.assertIn('second != current) return false;', pending)
        self.assertIn('BagSellMonitor.HasFreshSpace', pending)
        self.assertIn('BagSellMonitor.ObservedPlayer == _vendorAutoPlayer', pending)
        self.assertIn('return !retryBlocked', pending)
        self.assertNotIn('return due', pending)
        self.assertIn('ObservedPlayer => _player', MONITOR)

    def test_combat_and_user_stop_remain_higher_priority(self):
        due = section('    private static bool VendorDue(', '    /// <summary>\n    /// The original CopilotBuddy selling procedure')
        self.assertLess(due.index('if (UnderAttack()) return false;'), due.index('if (_vendorAutoPending)'))
        for guard in ('_stop', 'ClickAimAbort.Requested', 'RouteRunner.StopRequested', 'ObjectManager.WowB != reader'):
            self.assertIn(guard, due)
        self.assertIn('_vendorAutoPending && !ClickAimSettings.AutoSell', due)
        self.assertIn('BagSellMonitor.Reset(); // re-enabling', due)

    def test_pending_blocks_loot_and_pulls_between_retries(self):
        main = section('            while (!_stop && !ClickAimAbort.Requested && unchecked(Environment.TickCount - started) < budgetMs)', '                // Initial, timed and post-resurrection buff attempts')
        self.assertIn('!AutoVendorPending() && !VendorDue()', main)
        self.assertIn('AutoVendorPending() && (!TryReadGatherCombat(out bool pendingCombat) || !pendingCombat)', main)
        self.assertIn('ClickAimAbort.Wait(250)', main)
        self.assertLess(main.index('if (VendorDue())'), main.index('AutoVendorPending() && (!TryReadGatherCombat'))
        self.assertIn('(_vendorDue || AutoVendorPending()) && !InRealCombat()', SOURCE)

    def test_route_and_sell_revalidate_identity(self):
        trip = section('    private static bool VendorTrip(', '    /// <summary>\n    /// Replays the recorded plan')
        self.assertIn('!AutoVendorIdentityMatches(vendorReader, routeMemory, routePlayer)', trip)
        self.assertIn('!ClickAimSettings.AutoSell', trip)
        self.assertIn('ClearAutoVendorPending();', trip)
        self.assertGreater(trip.index('ClearAutoVendorPending();'), trip.index('int sent = RunSellPlan('))
        self.assertIn('BagSellMonitor.TripCompleted();', trip)
        valid = section('    private static bool VendorCandidateStillValid(', '    private static bool TryClickRecordedMerchant(')
        self.assertIn('player.Guid != _vendorAutoPlayer', valid)
        self.assertIn('!AutoVendorIdentityMatches(reader, memory, _vendorAutoPlayer)', valid)


if __name__ == '__main__':
    unittest.main()
