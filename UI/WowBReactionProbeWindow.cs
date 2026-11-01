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
    internal sealed class WowBReactionProbeWindow : Window
    {
        private readonly TextBlock _result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };

        public WowBReactionProbeWindow()
        {
            Title = "NPC reaction samples (read-only)";
            Width = 500; Height = 420; MinWidth = 380; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Stop the bot. Select an NPC in the game, then choose the label you know applies. " +
                       "Collect several different friendly town NPCs, yellow attackable neutral monsters, and red aggressive monsters; " +
                       "repeat some NPCs. Do not attack or interact for this probe. The labels are observations, not a classifier.",
                TextWrapping = TextWrapping.Wrap
            });
            AddButton(panel, "Friendly town NPC", "friendly");
            AddButton(panel, "Neutral, attackable monster", "neutral_attackable");
            AddButton(panel, "Aggressive monster", "aggressive");
            AddButton(panel, "Other / changed state (note details separately)", "other_observation");
            panel.Children.Add(_result);
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private void AddButton(StackPanel panel, string text, string label)
        {
            var button = new Button { Content = text, Height = 32, Margin = new Thickness(0, 8, 0, 0) };
            button.Click += (sender, args) => _result.Text = WowBReactionProbe.Capture(label);
            panel.Children.Add(button);
        }
    }
}
