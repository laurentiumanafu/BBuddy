using System;
using System.Threading.Tasks;
using System.Windows;
using Styx.Logic.Pathing;
using Window = System.Windows.Window;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using TextWrapping = System.Windows.TextWrapping;
using MessageBox = System.Windows.MessageBox;

namespace CopilotBuddy.UI
{
    internal sealed class GrindFactionWindow : Window
    {
        private readonly CheckBox _enabled = new CheckBox { Content = "Only pull listed faction-template IDs", Margin = new Thickness(0, 12, 0, 6) };
        private readonly TextBox _keys = new TextBox { Margin = new Thickness(0, 4, 0, 8), MinWidth = 300 };
        private readonly TextBox _report = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 150, Height = 220, Margin = new Thickness(0, 8, 0, 8) };
        private readonly Button _scan = new Button { Content = "Scan nearby NPC factions (100 horizontal yd)", Height = 32, Margin = new Thickness(0, 10, 0, 0) };

        public GrindFactionWindow()
        {
            Title = "Grind faction filter (70291)";
            Width = 610; Height = 630; MinWidth = 420; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            bool loaded = GrindFactionSettings.TryLoad(out GrindFactionRules saved, out string loadError);
            _enabled.IsChecked = saved.Enabled;
            _keys.Text = saved.KeyText;
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = "Stop the bot, then scan. No NPC needs to be selected. Results are observations, not NPC names or proof of hostility. A faction ID can include friendly NPCs; only add IDs you have verified and supervise the bot.", TextWrapping = TextWrapping.Wrap });
            _scan.Click += ScanClick;
            panel.Children.Add(_scan);
            _report.Text = loaded ? "Press Scan to read nearby NPCs without selecting them." : "Saved faction settings could not be loaded: " + loadError;
            panel.Children.Add(_report);
            panel.Children.Add(_enabled);
            panel.Children.Add(new TextBlock { Text = "Allowed IDs (decimal; separate with commas or spaces)", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(_keys);
            panel.Children.Add(new TextBlock { Text = "Default off. Unknown or unlisted IDs are skipped for new Grind pulls, including pool approaches and TAB picks. Combat defence is not filtered. The pool failsafe remains active. The list does not prove attackability and can affect many NPCs at once.", TextWrapping = TextWrapping.Wrap });
            var save = new Button { Content = "Save", Width = 110, Height = 28, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            save.Click += SaveClick;
            panel.Children.Add(save);
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private async void ScanClick(object sender, RoutedEventArgs e)
        {
            if (ClickAimUi.Running || ClickAimUi.Paused) { _report.Text = "Stop the bot and any route before scanning."; return; }
            _scan.IsEnabled = false;
            _report.Text = "Reading nearby NPC faction keys...";
            try { _report.Text = await Task.Run(() => WowBGrindFactions.ScanNearby()); }
            catch (Exception ex) { _report.Text = "Faction scan failed: " + ex.Message; }
            finally { _scan.IsEnabled = true; }
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            if (ClickAimUi.Running || ClickAimUi.Paused) { MessageBox.Show(this, "Stop the bot before changing faction settings."); return; }
            if (!GrindFactionSettings.Save(_enabled.IsChecked == true, _keys.Text, out string error))
            { MessageBox.Show(this, error, "Grind factions", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true;
        }
    }
}
