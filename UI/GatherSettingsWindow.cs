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
        private readonly CheckBox _roam = new CheckBox { Content = "Roam on the mesh when no profile is loaded", Margin = new Thickness(0, 10, 0, 4) };
        private readonly CheckBox _grind = new CheckBox { Content = "Gather nearby resources while grinding", Margin = new Thickness(0, 10, 0, 4) };
        private readonly TextBox _roamRadius = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _miningSkill = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _herbSkill = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };

        public GatherSettingsWindow()
        {
            GatherSettings.Load();
            Title = "Gather settings";
            Width = 460; Height = 550; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _grind.IsChecked = GatherSettings.GatherWhileGrinding;
            _roam.IsChecked = GatherSettings.RoamWithoutProfile;
            _roamRadius.Text = GatherSettings.RoamRadiusYd.ToString(CultureInfo.InvariantCulture);
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
            panel.Children.Add(_roam);
            panel.Children.Add(new TextBlock { Text = "Roaming destination radius from start (100–3000 yd)" });
            panel.Children.Add(_roamRadius);
            panel.Children.Add(new TextBlock { Text = "Experimental: zone borders are unknown. Combat, resources and route detours can go beyond this radius or into another zone. Loaded profiles take priority.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(_grind);
            panel.Children.Add(new TextBlock { Text = "Grind fights first. Gathering waits if combat or a nearby living NPC is detected; friendly NPCs can also block it because hostility is not known (100 yd).", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "Gathering works with WoW in the foreground or background. Moving the mouse in the foreground can interrupt a hover; the bot retries automatically.", TextWrapping = TextWrapping.Wrap });
            var save = new Button { Content = "Save", Width = 110, Height = 28, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            save.Click += SaveClick;
            panel.Children.Add(save);
            Content = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = panel
            };
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(_roamRadius.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int roamRadius)
                || roamRadius < 100 || roamRadius > 3000
                || !uint.TryParse(_miningSkill.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint mining)
                || !uint.TryParse(_herbSkill.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint herbs)
                || mining > 1000 || herbs > 1000
                || (_mining.IsChecked == true && mining == 0) || (_herbalism.IsChecked == true && herbs == 0)
                || (_grind.IsChecked == true && _mining.IsChecked != true && _herbalism.IsChecked != true))
            {
                MessageBox.Show(this, "Enter a roaming radius of 100–3000 yd and skill 1–1000 for each selected profession; Grind gathering needs at least one profession.", "Gatherbuddy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            GatherSettings.GatherWhileGrinding = _grind.IsChecked == true;
            GatherSettings.RoamWithoutProfile = _roam.IsChecked == true;
            GatherSettings.RoamRadiusYd = roamRadius;
            GatherSettings.Mining = _mining.IsChecked == true;
            GatherSettings.Herbalism = _herbalism.IsChecked == true;
            GatherSettings.MiningSkill = mining;
            GatherSettings.HerbalismSkill = herbs;
            if (!GatherSettings.Save()) { MessageBox.Show(this, "Could not save gather settings."); return; }
            DialogResult = true;
        }
    }
}
