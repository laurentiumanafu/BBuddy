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
            Title = "NPC cursor diagnostics";
            Width = 560; Height = 650; MinWidth = 390; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Stop the bot. In the game select a living NPC. Return here, choose its label below, " +
                       "then switch to the game and hover THAT SAME NPC within 8 seconds. Keep the pointer still. " +
                       "The four passive buttons send no input. They record the Windows cursor fingerprint, " +
                       "which may not be the cursor drawn by the game. For the off-target control, keep an NPC selected " +
                       "but hover empty game world instead, away from NPCs and UI. Nothing here changes target selection.",
                TextWrapping = TextWrapping.Wrap
            });
            AddButton(panel, "Friendly town NPC (no dagger)", "friendly");
            AddButton(panel, "Attackable yellow monster (dagger)", "neutral_attackable");
            AddButton(panel, "Aggressive monster (dagger)", "aggressive");
            AddButton(panel, "Off-target control (no NPC or UI under pointer)", "off_target_control");
            panel.Children.Add(new TextBlock
            {
                Text = "Background posted-hover experiment (separate from passive samples): " +
                       "Step 1 records a foreground hover after 8 seconds. Then return here and press step 2. " +
                       "Keep the game in the background and hands off the mouse; after 3 seconds step 2 posts " +
                       "two mouse-move messages to the game, away and back. No clicks or keys are sent. " +
                       "Keep the player, target, camera and game window still throughout.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0)
            });
            AddPostedButton(panel, "1. Prepare foreground hover (8 seconds)", true);
            AddPostedButton(panel, "2. Test posted hover in background", false);
            panel.Children.Add(_result);
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private void AddPostedButton(StackPanel panel, string text, bool prepare)
        {
            var button = new Button { Content = text, Height = 32, Margin = new Thickness(0, 8, 0, 0) };
            button.Click += (sender, args) =>
            {
                try
                {
                    if (WowBCursorProbe.StartPosted(prepare, result => Dispatcher.BeginInvoke(new Action(() => _result.Text = result))))
                        _result.Text = prepare
                            ? "Switch to the game, hover the selected NPC and keep still for at least 10 seconds; then return here."
                            : "Leave the game in the background, release mouse buttons and keep the pointer still. Testing begins in 3 seconds.";
                    else
                        _result.Text = "Stop the bot, attach to wowb.exe and wait for any prior probe to finish.";
                }
                catch (Exception ex) { _result.Text = "Could not start posted-hover probe: " + ex.Message; }
            };
            panel.Children.Add(button);
        }

        private void AddButton(StackPanel panel, string text, string label)
        {
            var button = new Button { Content = text, Height = 32, Margin = new Thickness(0, 8, 0, 0) };
            button.Click += (sender, args) =>
            {
                try
                {
                    if (WowBCursorProbe.Start(label, result => Dispatcher.BeginInvoke(new Action(() => _result.Text = result))))
                        _result.Text = label == "off_target_control"
                            ? "Switch to the game now; keep the NPC selected but hover empty world, away from NPCs and UI. Hold still for 8 seconds."
                            : "Switch to the game now and hover the same selected NPC; hold still for 8 seconds.";
                    else
                        _result.Text = "Stop the bot, attach to wowb.exe and wait for any previous cursor sample to finish.";
                }
                catch (Exception ex) { _result.Text = "Could not start cursor probe: " + ex.Message; }
            };
            panel.Children.Add(button);
        }
    }
}
