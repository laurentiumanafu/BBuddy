using System.Windows;
using MahApps.Metro.Controls;
using Styx.Logic.Pathing;

namespace CopilotBuddy.UI
{
    internal sealed class ShamanTotemMacroOptions
    {
        internal bool Enabled, Earth, Healing, Mana;
        internal int Key, Presses;
        internal static ShamanTotemMacroOptions Current() => new ShamanTotemMacroOptions {
            Enabled = ClickAimSettings.UseShamanTotemMacro, Key = ClickAimSettings.ShamanTotemMacroKey,
            Presses = ClickAimSettings.ShamanTotemMacroPresses, Earth = ClickAimSettings.ShamanTotemEarth,
            Healing = ClickAimSettings.ShamanTotemHealing, Mana = ClickAimSettings.ShamanTotemMana
        };
        internal void Apply()
        {
            ClickAimSettings.UseShamanTotemMacro = Enabled; ClickAimSettings.ShamanTotemMacroKey = Key;
            ClickAimSettings.ShamanTotemMacroPresses = Presses; ClickAimSettings.ShamanTotemEarth = Earth;
            ClickAimSettings.ShamanTotemHealing = Healing; ClickAimSettings.ShamanTotemMana = Mana;
        }
    }

    public partial class ShamanTotemMacroWindow : MetroWindow
    {
        private readonly bool _keyOneAttack;
        internal ShamanTotemMacroOptions? Result { get; private set; }
        internal ShamanTotemMacroWindow(ShamanTotemMacroOptions options, bool keyOneAttack)
        {
            InitializeComponent(); _keyOneAttack = keyOneAttack;
            for (int i = 1; i <= 9; i++) { cmbKey.Items.Add(i); cmbPresses.Items.Add(i); }
            cmbKey.SelectedItem = options.Key >= 1 && options.Key <= 9 ? options.Key : 2;
            cmbPresses.SelectedItem = options.Presses >= 1 && options.Presses <= 9 ? options.Presses : 2;
            chkEnabled.IsChecked = options.Enabled; chkEarth.IsChecked = options.Earth;
            chkHealing.IsChecked = options.Healing; chkMana.IsChecked = options.Mana;
        }
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            int key = cmbKey.SelectedItem is int k ? k : 0;
            int presses = cmbPresses.SelectedItem is int n ? n : 0;
            bool enabled = chkEnabled.IsChecked == true, earth = chkEarth.IsChecked == true;
            bool healing = chkHealing.IsChecked == true, mana = chkMana.IsChecked == true;
            string error = healing && mana ? "Healing Stream and Mana Spring both use the Water slot. Choose only one." :
                enabled && !earth && !healing && !mana ? "Select at least one totem effect." :
                enabled && (key == 0 || presses == 0) ? "Choose a key and a number of presses." :
                enabled && key == 1 && _keyOneAttack ? "Key 1 is already reserved for Start Attack. Choose another key." : "";
            if (error.Length != 0)
            { System.Windows.MessageBox.Show(error, "Shaman totem macro", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            Result = new ShamanTotemMacroOptions { Enabled = enabled, Key = key, Presses = presses,
                Earth = earth, Healing = healing, Mana = mana };
            DialogResult = true;
        }
    }
}
