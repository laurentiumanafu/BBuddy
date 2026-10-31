using System;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Window = System.Windows.Window;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using TextWrapping = System.Windows.TextWrapping;

namespace CopilotBuddy.UI
{
    /// <summary>A scrollable home for WowB gather and diagnostic controls.</summary>
    internal sealed class ClickAimToolsWindow : Window
    {
        public ClickAimToolsWindow((string Label, string Tip, Action Action)[] actions)
        {
            Title = "Gather & tools";
            Width = 350;
            Height = 460;
            MinWidth = 280;
            MinHeight = 250;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Gather settings and WowB tools",
                FontSize = 16,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });
            foreach (var action in actions)
            {
                var button = new Button
                {
                    Content = action.Label,
                    ToolTip = action.Tip,
                    Height = 30,
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center
                };
                button.Click += (sender, args) => action.Action();
                panel.Children.Add(button);
            }
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel
            };
        }
    }
}
