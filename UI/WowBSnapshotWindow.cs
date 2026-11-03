using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Styx.WoWInternals;
using Window = System.Windows.Window;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using DockPanel = System.Windows.Controls.DockPanel;
using StackPanel = System.Windows.Controls.StackPanel;
using Orientation = System.Windows.Controls.Orientation;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;

namespace CopilotBuddy.UI
{
    /// <summary>
    /// Read-only 70291 snapshot viewer. It intentionally does not create a
    /// legacy WoWObject, execute game functions, or classify hostility.
    /// </summary>
    public sealed class WowBSnapshotWindow : Window
    {
        private const int MaxDisplayedUnits = 500;
        private readonly Button _refresh;
        private readonly TextBlock _status;
        private readonly TextBox _details;
        private bool _closed;
        private WowBWorldSnapshot? _previous;

        public WowBSnapshotWindow()
        {
            Title = "WowB world snapshot — read-only";
            Width = 850;
            Height = 600;
            MinWidth = 600;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(12) };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            _refresh = new Button { Content = "Refresh snapshot", Padding = new Thickness(12, 5, 12, 5) };
            _status = new TextBlock { Text = "Not yet captured", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(_refresh);
            header.Children.Add(_status);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _details = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12
            };
            root.Children.Add(_details);
            Content = root;

            _refresh.Click += RefreshClick;
            Loaded += RefreshClick;
            Closed += (_, _) => _closed = true;
        }

        private async void RefreshClick(object sender, RoutedEventArgs e)
        {
            if (!_refresh.IsEnabled || _closed)
                return;
            _refresh.IsEnabled = false;
            _status.Text = "Capturing…";
            _details.Text = string.Empty;
            try
            {
                WowBWorldSnapshot? snapshot = await Task.Run(() =>
                {
                    if (ObjectManager.Wow?.Is64BitTarget != true)
                        return null;
                    ObjectManager.Update();
                    return ObjectManager.WowBWorld;
                });
                if (_closed)
                    return;
                if (snapshot == null)
                {
                    _previous = null;
                    _status.Text = "Snapshot unavailable";
                    _details.Text = "Capture failed or changed during the scan. Select Refresh to try again.";
                    return;
                }
                WowBSnapshotDifference? difference = _previous == null ? null : snapshot.CompareTo(_previous);
                _previous = snapshot;
                _status.Text = $"Captured {snapshot.CapturedUtc.ToLocalTime():HH:mm:ss}; {snapshot.Units.Count} units";
                _details.Text = Describe(snapshot, difference);
            }
            catch (Exception ex)
            {
                if (!_closed)
                {
                    _previous = null;
                    _status.Text = "Snapshot unavailable";
                    _details.Text = $"Capture failed: {ex.Message}";
                }
            }
            finally
            {
                if (!_closed)
                    _refresh.IsEnabled = true;
            }
        }

        private static void AppendHealthCandidate(StringBuilder text, string label, WowBObservedUnit unit)
        {
            if (!unit.CandidateHealth.HasValue)
            {
                text.AppendLine($"{label} candidate health words: unavailable");
                return;
            }
            WowBCandidateHealthWords words = unit.CandidateHealth.Value;
            text.AppendLine($"{label} candidate health words (unverified): current=0x{words.CurrentRaw64:X16}, max=0x{words.MaximumRaw64:X16}; low32={words.CurrentLow32}/{words.MaximumLow32}");
        }

        private static string Describe(WowBWorldSnapshot snapshot, WowBSnapshotDifference? difference)
        {
            var text = new StringBuilder();
            text.AppendLine("READ-ONLY OBSERVATIONS — not gameplay objects or durable pointers");
            text.AppendLine("Positions and identity are best-effort; target hostility and health are unknown.");
            if (difference.HasValue)
            {
                WowBSnapshotDifference change = difference.Value;
                text.AppendLine($"Since previous capture: newly observed={change.NewlyObserved}, no longer observed={change.NoLongerObserved}, record addresses changed={change.RecordAddressChanged}, unit addresses changed={change.UnitAddressChanged}");
                text.AppendLine($"Selected GUID changed={change.TargetGuidChanged}, local GUID changed={change.LocalGuidChanged} (observation differences only)");
            }
            else
                text.AppendLine("No previous successful capture for comparison.");
            text.AppendLine($"Slots scanned: {snapshot.PoolSlotsScanned}");
            text.AppendLine($"Local GUID:  {snapshot.LocalGuid}");
            text.AppendLine($"Target GUID: {snapshot.TargetGuid}");
            if (snapshot.TryGetLocalPlayer(out WowBObservedUnit local))
            {
                text.AppendLine($"Player position: ({local.Location.X:F1}, {local.Location.Y:F1}, {local.Location.Z:F1})");
                AppendHealthCandidate(text, "Player", local);
            }
            if (snapshot.TargetGuid.IsEmpty)
                text.AppendLine("Selected target: none");
            else if (snapshot.TryGetSelectedTarget(out WowBObservedUnit target))
            {
                text.AppendLine($"Selected target position: ({target.Location.X:F1}, {target.Location.Y:F1}, {target.Location.Z:F1})");
                AppendHealthCandidate(text, "Target", target);
                if (snapshot.TryGetLocalPlayer(out WowBObservedUnit player))
                    text.AppendLine($"Snapshot distance: {player.Location.Distance(target.Location):F1} yd");
            }
            else
                text.AppendLine("Selected target: not in this snapshot (may be unsupported or changed)");

            text.AppendLine();
            text.AppendLine("GUID                                kind       position (x, y, z)");
            int count = 0;
            foreach (WowBObservedUnit unit in snapshot.Units)
            {
                if (count++ >= MaxDisplayedUnits)
                {
                    text.AppendLine($"… display limited to {MaxDisplayedUnits} units");
                    break;
                }
                string kind = unit.IsLocalPlayer ? "local" : unit.HasPlayerVtable ? "player-vt" : "unit-vt";
                text.AppendLine($"{unit.Guid}  {kind,-9}  ({unit.Location.X:F1}, {unit.Location.Y:F1}, {unit.Location.Z:F1})");
            }
            return text.ToString();
        }
    }
}
