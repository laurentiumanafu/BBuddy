using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Styx.Logic.Pathing;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CopilotBuddy.UI
{
    /// <summary>Manual settings because wowb profession and skill offsets are not known.</summary>
    internal sealed class GatherSettingsWindow : Window
    {
        private readonly CheckBox _mining = new CheckBox { Content = "Mining", Margin = new Thickness(0, 8, 0, 4) };
        private readonly CheckBox _herbalism = new CheckBox { Content = "Herbalism", Margin = new Thickness(0, 8, 0, 4) };
        private readonly TextBox _miningSkill = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _herbSkill = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };

        public GatherSettingsWindow()
        {
            GatherSettings.Load();
            Title = "Gatherbuddy settings";
            Width = 390; Height = 320; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _mining.IsChecked = GatherSettings.Mining;
            _herbalism.IsChecked = GatherSettings.Herbalism;
            _miningSkill.Text = GatherSettings.MiningSkill.ToString(CultureInfo.InvariantCulture);
            _herbSkill.Text = GatherSettings.HerbalismSkill.ToString(CultureInfo.InvariantCulture);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = "Select your trained professions and enter their current skill levels. These are manual: the client skill offsets are not yet known.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(_mining);
            panel.Children.Add(new TextBlock { Text = "Mining skill" });
            panel.Children.Add(_miningSkill);
            panel.Children.Add(_herbalism);
            panel.Children.Add(new TextBlock { Text = "Herbalism skill" });
            panel.Children.Add(_herbSkill);
            var save = new Button { Content = "Save", Width = 110, Height = 28, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            save.Click += SaveClick;
            panel.Children.Add(save);
            Content = panel;
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            if (!uint.TryParse(_miningSkill.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint mining)
                || !uint.TryParse(_herbSkill.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint herbs)
                || mining > 1000 || herbs > 1000
                || (_mining.IsChecked == true && mining == 0) || (_herbalism.IsChecked == true && herbs == 0)
                || (_mining.IsChecked != true && _herbalism.IsChecked != true))
            {
                MessageBox.Show(this, "Select at least one profession and enter its skill (1–1000).", "Gatherbuddy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            GatherSettings.Mining = _mining.IsChecked == true;
            GatherSettings.Herbalism = _herbalism.IsChecked == true;
            GatherSettings.MiningSkill = mining;
            GatherSettings.HerbalismSkill = herbs;
            if (!GatherSettings.Save()) { MessageBox.Show(this, "Could not save gather settings."); return; }
            DialogResult = true;
        }
    }
}
