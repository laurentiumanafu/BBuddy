using System;
using System.Windows;
using System.Windows.Controls;
using Styx.Logic.Pathing;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Window = System.Windows.Window;
using StackPanel = System.Windows.Controls.StackPanel;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using TextWrapping = System.Windows.TextWrapping;

namespace CopilotBuddy.UI
{
    internal sealed class WowBCursorProbeWindow : Window
    {
        private readonly TextBlock _result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };

        public WowBCursorProbeWindow()
        {
            Title = "NPC cursor probe (read-only)";
            Width = 520; Height = 450; MinWidth = 390; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Stop the bot. In the game select a living NPC. Return here, choose its label below, " +
                       "then switch to the game and hover THAT SAME NPC within 8 seconds. Keep the pointer still. " +
                       "The tool sends no mouse or keyboard input. It records the Windows cursor fingerprint, " +
                       "which may not be the cursor drawn by the game. Nothing here changes target selection.",
                TextWrapping = TextWrapping.Wrap
            });
            AddButton(panel, "Friendly town NPC (no dagger)", "friendly");
            AddButton(panel, "Attackable yellow monster (dagger)", "neutral_attackable");
            AddButton(panel, "Aggressive monster (dagger)", "aggressive");
            panel.Children.Add(_result);
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private void AddButton(StackPanel panel, string text, string label)
        {
            var button = new Button { Content = text, Height = 32, Margin = new Thickness(0, 8, 0, 0) };
            button.Click += (sender, args) =>
            {
                try
                {
                    if (WowBCursorProbe.Start(label, result => Dispatcher.BeginInvoke(new Action(() => _result.Text = result))))
                        _result.Text = "Switch to the game now and hover the same selected NPC; hold still for 8 seconds.";
                    else
                        _result.Text = "Stop the bot, attach to wowb.exe and wait for any previous cursor sample to finish.";
                }
                catch (Exception ex) { _result.Text = "Could not start cursor probe: " + ex.Message; }
            };
            panel.Children.Add(button);
        }
    }
}
