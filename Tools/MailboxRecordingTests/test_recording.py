"""Static wiring checks; executable persistence cases require the .NET SDK."""
from pathlib import Path
import unittest

root = Path(__file__).resolve().parents[2]
v = (root / 'Styx/Logic/Pathing/ClickAim/VendorFile.cs').read_text(encoding='utf-8-sig')
x = (root / 'UI/ClickAimCombatSettingsWindow.xaml').read_text(encoding='utf-8-sig')
c = (root / 'UI/ClickAimCombatSettingsWindow.xaml.cs').read_text(encoding='utf-8-sig')

class MailboxRecording(unittest.TestCase):
    def test_record_is_scoped_and_setup_only(self):
        record = v[v.index('public static bool SetMailbox'):v.index('// Change only the selected point')]
        for guard in ('ValidPosition', 'townMap != map', 'Save(doc, file)', '"Mailbox"'):
            self.assertIn(guard, record)
        self.assertIn('btnRecordMailbox_Click', x)
        handler = c[c.index('private void btnRecordMailbox_Click'):c.index('private void btnRememberMerchant_Click')]
        for guard in ('ClickAimUi.Running', 'ObjectManager.Wow != memory', 'finalMap != map', 'ClickAimMath.Len', 'Sales and mail are not independently verified'):
            self.assertIn(guard, handler)
    def test_town_rerecord_clears_old_metadata(self):
        method = v[v.index('public static bool SetTown'):v.index('public static bool AddMerchant')]
        self.assertIn('"Mailbox"', method)
        self.assertIn('"Merchant"', method)

if __name__ == '__main__': unittest.main()
