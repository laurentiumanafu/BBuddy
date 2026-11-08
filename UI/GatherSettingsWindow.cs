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
        private readonly CheckBox _skinning = new CheckBox { Content = "Skin confirmed beasts after looting (Grind)", Margin = new Thickness(0, 10, 0, 4) };
        private readonly CheckBox _skinNearby = new CheckBox { Content = "Skin nearby confirmed dead beasts (Grind)", Margin = new Thickness(0, 4, 0, 4) };
        private readonly TextBlock _beastCount = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly CheckBox _roam = new CheckBox { Content = "Roam on the mesh when no profile is loaded", Margin = new Thickness(0, 10, 0, 4) };
        private readonly CheckBox _grind = new CheckBox { Content = "Gather nearby resources while grinding", Margin = new Thickness(0, 10, 0, 4) };
        private readonly CheckBox _ignoreMonsters = new CheckBox { Content = "Ignore monsters and keep gathering (GatherBuddy)", Margin = new Thickness(0, 10, 0, 4) };
        private readonly CheckBox _movementSkill = new CheckBox { Content = "Use movement skill when travelling (GatherBuddy, 70862)", Margin = new Thickness(0, 10, 0, 4) };
        private readonly TextBox _movementSlot = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _roamRadius = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _miningSkill = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _herbSkill = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };

        public GatherSettingsWindow()
        {
            GatherSettings.Load();
            if (!ClickAimUi.Running && !ClickAimUi.Paused) WowBSkinning.Load();
            Title = "Gather settings";
            Width = 460; Height = 550; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _grind.IsChecked = GatherSettings.GatherWhileGrinding;
            _ignoreMonsters.IsChecked = GatherSettings.IgnoreMonsters;
            _movementSkill.IsChecked = GatherSettings.UseMovementSkill;
            _movementSlot.Text = GatherSettings.MovementSkillSlot.ToString(CultureInfo.InvariantCulture);
            _roam.IsChecked = GatherSettings.RoamWithoutProfile;
            _roamRadius.Text = GatherSettings.RoamRadiusYd.ToString(CultureInfo.InvariantCulture);
            _mining.IsChecked = GatherSettings.Mining;
            _herbalism.IsChecked = GatherSettings.Herbalism;
            _skinning.IsChecked = GatherSettings.Skinning;
            _skinNearby.IsChecked = GatherSettings.SkinNearby;
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
            panel.Children.Add(_skinning);
            panel.Children.Add(_skinNearby);
            panel.Children.Add(new TextBlock { Text = "Nearby includes beasts killed by others, up to 18 yd away. Skill, skinning readiness and beast type are not readable on wowb. Stop the bot, select a living beast, then remember it. Only confirmed entry candidates are tried; this applies to other spawns of that entry. Ensure skinning is trained and you have the required skill/tool.", TextWrapping = TextWrapping.Wrap });
            var remember = new Button { Content = "Remember selected beast", Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
            remember.Click += RememberBeastClick;
            panel.Children.Add(remember);
            var clearBeasts = new Button { Content = "Clear confirmed beasts", Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
            clearBeasts.Click += ClearBeastsClick;
            panel.Children.Add(clearBeasts);
            panel.Children.Add(_beastCount);
            UpdateBeastCount();
            panel.Children.Add(_roam);
            panel.Children.Add(new TextBlock { Text = "Roaming destination radius from start (100–3000 yd)" });
            panel.Children.Add(_roamRadius);
            panel.Children.Add(new TextBlock { Text = "Experimental: zone borders are unknown. Combat, resources and route detours can go beyond this radius or into another zone. Loaded profiles take priority.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(_ignoreMonsters);
            panel.Children.Add(new TextBlock { Text = "Standalone GatherBuddy will not defend while this is checked, even when attacked. Gathering may fail or be interrupted, and the character can die. STOP and death recovery still apply. Grind is unchanged.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(_movementSkill);
            panel.Children.Add(new TextBlock { Text = "First-bar movement skill slot (1–12)" });
            panel.Children.Add(_movementSlot);
            panel.Children.Add(new TextBlock { Text = "Supported: Ghost Wolf, Travel Form and Aspect of the Cheetah on the first action bar. Put the spell on the first bar with the default 1–9, 0, - or = key binding. The bot tries once before travel and again after a node interaction; it cannot verify the form or success, and a repeat may toggle a form off. If a form changes the action bar, the existing safety guard may pause or stop the bot.", TextWrapping = TextWrapping.Wrap });
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

        private void UpdateBeastCount()
        {
            _beastCount.Text = "Confirmed entry candidates: " + WowBSkinning.Count(false) + " for 70291, "
                + WowBSkinning.Count(true) + " shared by 70707/70755/70790/70862. The entry field is experimental.";
        }

        private void RememberBeastClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Is your selected living NPC a beast? Its entry candidate will be treated as a beast for all matching spawns. Do not confirm humanoids or unknown types.",
                "Confirm beast", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool saved = WowBSkinning.RememberSelected(out string message);
            MessageBox.Show(this, message, "Skinning", MessageBoxButton.OK, saved ? MessageBoxImage.Information : MessageBoxImage.Warning);
            UpdateBeastCount();
        }

        private void ClearBeastsClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Clear confirmed beast entries for both wowb builds?", "Skinning",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!WowBSkinning.Clear()) MessageBox.Show(this, "Stop the bot first or check that the settings file is writable.", "Skinning", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateBeastCount();
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(_movementSlot.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int movementSlot) ||
                movementSlot < 0 || movementSlot > 12 || (_movementSkill.IsChecked == true && movementSlot == 0))
            {
                MessageBox.Show(this, "Enter the first-bar slot (1–12) containing your movement skill. Use 0 only when the option is off.", "Gatherbuddy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
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
            GatherSettings.IgnoreMonsters = _ignoreMonsters.IsChecked == true;
            GatherSettings.UseMovementSkill = _movementSkill.IsChecked == true;
            GatherSettings.MovementSkillSlot = movementSlot;
            GatherSettings.RoamWithoutProfile = _roam.IsChecked == true;
            GatherSettings.RoamRadiusYd = roamRadius;
            GatherSettings.Mining = _mining.IsChecked == true;
            GatherSettings.Herbalism = _herbalism.IsChecked == true;
            GatherSettings.Skinning = _skinning.IsChecked == true;
            GatherSettings.SkinNearby = _skinNearby.IsChecked == true;
            GatherSettings.MiningSkill = mining;
            GatherSettings.HerbalismSkill = herbs;
            if (!GatherSettings.Save()) { MessageBox.Show(this, "Could not save gather settings."); return; }
            DialogResult = true;
        }
    }
}
