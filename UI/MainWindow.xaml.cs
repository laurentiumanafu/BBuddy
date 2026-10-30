using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MahApps.Metro.Controls;
using GreenMagic;
using Styx;
using Styx.Common;
using Styx.CommonBot;
using Styx.Helpers;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Profiles;
using Styx.WoWInternals;

using WpfFontFamily = System.Windows.Media.FontFamily;

namespace CopilotBuddy.UI
{
    /// <summary>
    /// Main window for CopilotBuddy - HB 5.4.8 style interface.
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        #region Fields

        private Memory? _memory;
        private ExecutorRand? _executor;
        private Paragraph? _logParagraph;
        private WpfFontFamily? _logFont;
        private int _logCount;
        private const int MaxLogLines = 1000;
        private bool _isRunning;
        private DispatcherTimer? _infoTimer;

        /// <summary>
        /// True while the Start button is running the ClickAim grind loop instead of the
        /// behaviour tree - the case on wowb.exe, where the tree refuses to start.
        /// </summary>
        private bool _grindStarted;

        /// <summary>
        /// Mutex that claims ownership of the attached WoW process.
        /// Prevents another CopilotBuddy from attaching to the same WoW.
        /// HB 4.3.4 pattern: held for entire session lifetime.
        /// </summary>
        private Mutex? _processMutex;

        /// <summary>
        /// Selected WoW process ID from the process selector.
        /// null = pending selection, 0 = user cancelled.
        /// HB 4.3.4 pattern.
        /// </summary>
        private int? _selectedWoWProc;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            // Setup logging
            InitializeLogging();

            // Setup info panel timer
            InitializeInfoTimer();

            // Log version (HB 4.3.4: "Honorbuddy v{0} started.")
            // AssemblyVersion is intentionally not set in .csproj (causes BAML crash under .NET 10).
            const string BotVersion = "1.6.7.5";
            Title = $"CopilotBuddy v{BotVersion}";
            Logging.Write("CopilotBuddy v{0} started. Original HonorBuddy by Apoc, raphus, highvoltz, bobby53, xanathos, chinajade, Natfoth. Ported to WotLK 3.3.5a by Likon69.", BotVersion);
        }

        #endregion

        #region Initialization

        private void InitializeLogging()
        {
            // Initialize RichTextBox
            rtbLog.Document.Blocks.Clear();
            _logParagraph = new Paragraph();
            rtbLog.Document.Blocks.Add(_logParagraph);
            _logFont = new WpfFontFamily("Consolas");
            _logCount = 0;

            // Setup log file path
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            var timestamp = DateTime.Now;
            Logging.LogFilePath = Path.Combine(logDir,
                $"{timestamp.Year}-{timestamp.Month:D2}-{timestamp.Day:D2}_{timestamp.Hour:D2}{timestamp.Minute:D2}_{Process.GetCurrentProcess().Id}.log");

            // Subscribe to logging events
            Logging.OnLogMessage += OnLogMessage;
        }

        private void InitializeInfoTimer()
        {
            _infoTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            /* The panel reads ObjectManager.Me, which on wowb.exe is a 32-bit wrapper over a
             * client that does not have one. A throw here would kill the window from a timer
             * tick, so the tick swallows it and the panel simply shows the counters. */
            _infoTimer.Tick += (s, e) =>
            {
                try { UpdateInfoPanel(); }
                catch (Exception ex) { Logging.WriteDebug("info panel: " + ex.Message); }
            };
            
            // Subscribe to StatusText changes — updates StatusBar like HB 4.3.4
            TreeRoot.OnStatusTextChanged += (sender, e) =>
            {
                Dispatcher.BeginInvoke(() => lblActivityText.Content = e.NewStatus ?? "Ready");
            };
            
            // Show default values immediately
            UpdateInfoPanel();
        }

        /// <summary>
        /// WPF best practice: restore window geometry here — fires when the Win32 handle is
        /// created but BEFORE the window is shown.  Position applied here takes effect on the
        /// first rendered pixel with no visual flash (unlike Window_Loaded which fires after
        /// the window is already visible and the XAML size has been applied).
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            try
            {
                var s = UISettings.Instance; // singleton already loaded in its ctor

                // Size — must be positive and non-default
                if (s.MainWindowWidth > 0)  this.Width  = s.MainWindowWidth;
                if (s.MainWindowHeight > 0) this.Height = s.MainWindowHeight;

                // Position — only restore if explicitly saved (-1 = never saved / first run)
                if (s.MainWindowLocationX >= 0) this.Left = s.MainWindowLocationX;
                if (s.MainWindowLocationY >= 0) this.Top  = s.MainWindowLocationY;

                // State — never restore Minimized (window would start hidden)
                if (s.MainWindowState != WindowState.Minimized)
                    this.WindowState = s.MainWindowState;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[UISettings] Failed to restore window geometry: " + ex.Message);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Window geometry is already restored in OnSourceInitialized above.
            // Only do non-geometry UI setup here.
            try
            {
                // Restore Enhanced Mode checkbox state
                if (UISettings.Instance.EnhancedMode)
                    EnhancedMode.IsChecked = true;
            }
            catch { /* Ignore errors on first run */ }

            SetStatus("Searching for WoW...");
            ToggleButtons(false);

            Task.Run(() =>
            {
                try
                {
                    if (!FindAndAttachToWoW())
                        return;

                    if (ObjectManager.IsInitialized)
                    {
                        Logging.Write("Attached to WoW with ID {0}", _selectedWoWProc!.Value);

                        if (ObjectManager.Wow?.Is64BitTarget == true)
                        {
                            // Until the x64 object model and dependencies are
                            // ported, plugin/bot constructors and legacy SQLite
                            // cannot be treated as safe or available. Leave
                            // normal x86 startup unchanged.
                            /* The behaviour tree and the combat routines still cannot start on
                             * this client, but the window is now the way in: Load Profile ->
                             * Grind -> Start runs the ClickAim grind loop. Say that instead of
                             * saying everything is disabled, which stopped being true. */
                            Logging.Write("[Attach] WowB mode: the behaviour tree is not used here. "
                                          + "Load Profile, pick \"Grind\", press Start - that runs the "
                                          + "ClickAim loop on the loaded profile.");

                            // Movement does not need the object model - it needs the
                            // player's X/Y, which the WowB reader already provides. Arm
                            // the click-aim pilot so it can be exercised from this mode.
                            try
                            {
                                Styx.Logic.Pathing.ClickAimDiagnostics.EnableAfterAttach();
                            }
                            catch (Exception ex)
                            {
                                Logging.Write("[ClickAim] pilot not armed: " + ex.Message);
                            }

                            Dispatcher.Invoke(() =>
                            {
                                SetStatus("Attached (read-only)");
                                // The legacy Developer Tools uses 32-bit
                                // wrappers. Expose the separate read-only
                                // x64 snapshot viewer instead.
                                btnDevTools.Content = "World snapshot";
                                btnDevTools.ToolTip = "View a read-only WowB snapshot";
                                btnDevTools.Visibility = Visibility.Visible;
                                btnDevTools.IsEnabled = true;
                                btnAimWalk.Visibility = Visibility.Visible;
                                btnAimWalk.IsEnabled = true;
                                btnAimHotspot.Visibility = Visibility.Visible;
                                btnAimHotspot.IsEnabled = true;
                                btnAimHotspots.Visibility = Visibility.Visible;
                                btnAimHotspots.IsEnabled = true;
                                btnAimPlanHotspots.Visibility = Visibility.Visible;
                                btnAimPlanHotspots.IsEnabled = true;
                                btnAimClearHotspots.Visibility = Visibility.Visible;
                                btnAimClearHotspots.IsEnabled = true;

                                /* Enable the three controls that do something on this client.
                                 * Everything else stays as ToggleButtons(false) left it, because
                                 * it still needs the object model this client does not expose. */
                                try
                                {
                                    /* Deliberately NOT BotManager.Instance.Bots: touching it
                                     * instantiates every BotBase in the assembly, and some of
                                     * them own a SQLite database and a 32-bit object model -
                                     * on the UI thread, on a client where none of them can
                                     * run. Grind is the one bot with a 64-bit implementation,
                                     * and it is the only entry the list needs. */
                                    cmbBotSelector.Items.Clear();
                                    cmbBotSelector.Items.Add(Styx.Logic.Pathing.ClickAimUi.GrindBotName);
                                    cmbBotSelector.SelectedIndex = 0;
                                    cmbBotSelector.IsEnabled = true;
                                }
                                catch (Exception ex)
                                {
                                    Logging.Write("[Attach] bot list: " + ex.Message);
                                }

                                btnLoadProfile.IsEnabled = true;
                                btnStart.IsEnabled = true;
                                EnhancedMode.IsEnabled = true;

                                // The grind loop can end on its own; the info timer is what puts
                                // the buttons back when it does.
                                _infoTimer?.Start();
                            });
                            return;
                        }

                        // HB 6.2.3 smethod_3: Start minimize guard thread + event handlers
                        TreeRoot.Initialize();
                        BotEvents.OnBotStarted += BotEvents_OnBotStarted;
                        BotEvents.OnBotStopped += BotEvents_OnBotStopped;

                        // Log character info (HB 4.3.4 pattern after attach)
                        if (ObjectManager.Me != null)
                        {
                            Logging.Write("Character is a level {0} {1} {2}",
                                ObjectManager.Me.Level,
                                ObjectManager.Me.Race,
                                ObjectManager.Me.Class);
                            // Honorbuddy 3.3.5a simply logged RealZoneText
                            Logging.Write("Current zone is {0}", ObjectManager.Me.RealZoneText);
                        }

                        // Reinitialize and load settings for this character (HB 4.3.4 pattern)
                        LoadSettings();

                        // Initialize Combat Routines (compile and load from Routines folder)
                        // This must be done AFTER attachment when ObjectManager.Me is available
                        try
                        {
                            Styx.Logic.Combat.RoutineManager.Init();
                        }
                        catch (Exception ex)
                        {
                            Logging.Write(Colors.Red, "Failed to initialize combat routines: {0}", ex.Message);
                            Logging.WriteException(ex);
                        }

                        // Load additional bots from Bots folder (external/custom bots)
                        try
                        {
                            string botsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bots");
                            if (Directory.Exists(botsPath))
                            {
                                BotManager.Instance.LoadBots(botsPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logging.WriteException(ex);
                        }

                        // Initialize plugins (compile and load from Plugins folder)
                        try
                        {
                            Logging.Write("Initializing Plugins...");
                            
                            // Pre-load System.Drawing.Common for plugin compilation
                            try
                            {
                                // Force load System.Drawing.Common into AppDomain
                                var _ = System.Drawing.Font.FromHdc(IntPtr.Zero);
                            }
                            catch { /* Ignore, just ensure assembly is loaded */ }
                            
                            // Load previously enabled plugins from CharacterSettings
                            // CharacterSettings.Instance is null until a player is detected
                            // (never, in WowB read-only mode) — tolerate it.
                            var enabledPlugins = CharacterSettings.Instance?.EnabledPlugins ?? new string[0];
                            Styx.Plugins.PluginManager.Initialize(enabledPlugins);
                        }
                        catch (Exception ex)
                        {
                            Logging.Write(Colors.Red, "Failed to initialize plugins: {0}", ex.Message);
                            Logging.WriteException(ex);
                        }

                        Dispatcher.Invoke(() =>
                        {
                            // Populate bot selector with all loaded bots (built-in + external)
                            cmbBotSelector.Items.Clear();
                            foreach (var bot in BotManager.Instance.Bots)
                            {
                                cmbBotSelector.Items.Add(bot.Key);
                            }

                            // Restore last selected bot (HB 4.3.4 pattern)
                            if (cmbBotSelector.Items.Count > 0)
                            {
                                int selectedIndex = CharacterSettings.Instance?.SelectedBotIndex ?? 0;
                                if (selectedIndex < 0 || selectedIndex >= cmbBotSelector.Items.Count)
                                {
                                    // Invalid index, reset to 0
                                    if (CharacterSettings.Instance != null)
                                        CharacterSettings.Instance.SelectedBotIndex = 0;
                                    selectedIndex = 0;
                                }
                                cmbBotSelector.SelectedIndex = selectedIndex;
                            }

                            ToggleButtons(true);
                            SetStatus("CopilotBuddy Startup Complete");
                            _infoTimer.Start();

                            // AfterInitOnAuthSuccess equivalent for HBRelog integration.
                            // Mirrors Honorbuddy 4.3.4 MainWindow.xaml.cs:419-437 + Start():847-866.
                            // CommandLine.AutoLoadProfile / AutoStartBotName are parsed in
                            // Styx/Helpers/CommandLine.cs but were never consumed prior to this.
                            ApplyAutoStartConfig();
                        });
                    }
                    else
                    {
                        Logging.WriteQuiet(Colors.Red, "Failed to initialize ObjectManager");
                        Dispatcher.Invoke(() => SetStatus("Initialization failed"));
                    }
                }
                catch (Exception ex)
                {
                    Logging.WriteException(ex);
                    Dispatcher.Invoke(() =>
                    {
                        SetStatus("Error");
                        System.Windows.MessageBox.Show(
                            $"Injection failed!\n\nError: {ex.Message}",
                            "CopilotBuddy - Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });
                }
            });
        }

        /// <summary>
        /// Finds available WoW processes, handles auto-attach or process selection.
        /// Ported from HB 4.3.4 MainWindow.FindAndAttachToWoW() with 6.2.3 improvements.
        /// 
        /// Logic:
        ///   1. Check /pid= command line arg for explicit process selection
        ///   2. Find all Wow.exe processes matching build 12340
        ///   3. Filter: logged in + not claimed by another CopilotBuddy (via mutex)
        ///   4. If 0 available: show error
        ///   5. If 1 available: auto-attach
        ///   6. If 2+: show ProcessSelectorWindow
        ///   7. Attach to selected process
        /// </summary>
        private bool FindAndAttachToWoW()
        {
            Logging.Write("Searching for WoW process...");
            Logging.Write("[Attach] Executable: {0} ({1})",
                Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory,
                Environment.Is64BitProcess ? "x64" : "x86");

            // Step 1: Check for /pid= command line argument
            string? pidArg = Environment.GetCommandLineArgs()
                .FirstOrDefault(s => s.StartsWith("/pid=", StringComparison.OrdinalIgnoreCase));

            _selectedWoWProc = null;
            var candidates = new List<Process>();
            int wowProcessesFound = 0;

            if (pidArg == null)
            {
                // Find ALL Wow processes (covers the standard client, the WoW.exe spelling,
                // and the Luacct.exe variant used by some private-server clients)
                string[] wowProcessNames = { "Wow", "WoW", "Luacct", "WowB" };
                var wowProcesses = wowProcessNames.SelectMany(n => Process.GetProcessesByName(n))
                    .DistinctBy(p => p.Id).ToArray();
                wowProcessesFound = wowProcesses.Length;

                foreach (var proc in wowProcesses)
                {
                    try
                    {
                        if (proc.HasExited) continue;

                        int build = ObjectManager.GetBuildNumber(proc);
                        if (build < 0)
                        {
                            // Typical cause: 32-bit CopilotBuddy inspecting a native
                            // 64-bit client (e.g. WowB.exe) — the OS refuses module
                            // enumeration across the bitness boundary.
                            Logging.Write(Colors.Orange, "{0} (PID {1}) found, but its build could not be read.", proc.ProcessName, proc.Id);
                            if (!Environment.Is64BitProcess)
                                Logging.Write(Colors.Orange, "CopilotBuddy is running as x86 — use the x64 build to attach to a 64-bit client.");
                            continue;
                        }

                        // build == 0 means no version resource (custom/private server client).
                        // WowB.exe reports FileVersion 1.60.1.x (FilePrivatePart = 1) — its
                        // real build number (70291) is NOT in that field, so accept WowB by
                        // process name: only the Classic Beta client is named WowB.
                        if (ObjectManager.IsSupportedBuild(build) || build == 0
                            || proc.ProcessName.Equals("WowB", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(proc);
                            Logging.WriteDebug("Candidate: {0} (PID {1}), build {2}", proc.ProcessName, proc.Id, build);
                        }
                        else
                        {
                            Logging.Write(Colors.Orange, "{0} (PID {1}) skipped: unsupported build {2} (expected {3} or {4}).",
                                proc.ProcessName, proc.Id, build, ObjectManager.SupportedBuild, ObjectManager.SupportedBuildWowB);
                        }
                    }
                    catch { /* Access denied or process exited */ }
                }
            }
            else
            {
                // Explicit PID from command line
                string pidStr = pidArg.Substring("/pid=".Length).Trim();
                if (int.TryParse(pidStr, out int pid))
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        if (!proc.HasExited)
                        {
                            int build = ObjectManager.GetBuildNumber(proc);
                            if (build < 0)
                            {
                                Logging.Write(Colors.Orange, "PID {0}: build could not be read.", pid);
                                if (!Environment.Is64BitProcess)
                                    Logging.Write(Colors.Orange, "CopilotBuddy is running as x86 — use the x64 build to attach to a 64-bit client.");
                            }
                            else if (ObjectManager.IsSupportedBuild(build) || build == 0
                                || proc.ProcessName.Equals("WowB", StringComparison.OrdinalIgnoreCase))
                                candidates.Add(proc);
                            else
                                Logging.Write(Colors.Orange, "PID {0} skipped: unsupported build {1} (expected {2} or {3}).", pid, build, ObjectManager.SupportedBuild, ObjectManager.SupportedBuildWowB);
                        }
                    }
                    catch
                    {
                        Logging.Write(Colors.Red, "Process with PID {0} not found or inaccessible.", pid);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                if (wowProcessesFound == 0)
                    Logging.WriteQuiet(Colors.Red, "WoW.exe not found! Please launch WoW (3.3.5a) or WowB (Classic Beta) first.");
                else
                    Logging.WriteQuiet(Colors.Red, "{0} WoW process(es) found but none could be used — see the messages above.", wowProcessesFound);
                Dispatcher.Invoke(() => SetStatus("WoW not found"));
                return false;
            }

            // Step 2: Filter by logged-in state + mutex availability
            var available = new List<KeyValuePair<int, Mutex>>();
            foreach (var proc in candidates)
            {
                try
                {
                        using var tempMemory = new Memory(proc.Id);

                        // Check if player is logged in
                        bool isLoggedIn;
                        if (tempMemory.Is64BitTarget)
                        {
                            // WowB (modern x64 client): the 3.3.5a InGame offset does
                            // not apply here. Accept the process for now — the
                            // build-specific check arrives with the WowB offset table.
                            isLoggedIn = true;
                            Logging.WriteDebug("Login check skipped for 64-bit client PID {0} (no InGame offset for this build yet).", proc.Id);
                        }
                        else
                        {
                            isLoggedIn = tempMemory.Read<byte>(Styx.Offsets.GlobalOffsets.InGame) != 0;
                        }
                        if (!isLoggedIn)
                            continue;

                    // Try to claim the process via mutex
                    var mutex = ProcessMutex.Create(proc.Id, out bool createdNew);
                    if (!createdNew)
                    {
                        // Another CopilotBuddy already owns this process
                        Logging.Write(Colors.Orange, "PID {0} is already attached by another CopilotBuddy instance.", proc.Id);
                        mutex.Close();
                        continue;
                    }

                    available.Add(new KeyValuePair<int, Mutex>(proc.Id, mutex));
                }
                catch (Exception ex)
                {
                    Logging.Write(Colors.Orange, "PID {0} dropped during availability check: {1}", proc.Id, ex.Message);
                }
            }

            if (available.Count == 0)
            {
                Logging.WriteQuiet(Colors.Red, "No available WoW processes. They may not be logged in or already attached by another CopilotBuddy.");
                Dispatcher.Invoke(() => SetStatus("No available WoW"));
                return false;
            }

            // Step 3: Auto-attach if only one, otherwise show selector
            if (available.Count == 1)
            {
                _selectedWoWProc = available[0].Key;
                _processMutex = available[0].Value;
            }
            else
            {
                // Release all mutexes — ProcessSelectorWindow will acquire its own
                foreach (var kvp in available)
                    kvp.Value.Close();

                // Show selector on UI thread (HB 4.3.4 pattern)
                _selectedWoWProc = null;
                ProcessSelectorWindow.ProcessEntry? selectedEntry = null;

                Dispatcher.Invoke(() =>
                {
                    var psw = new ProcessSelectorWindow { Owner = this };
                    if (psw.ShowDialog() == true && psw.Entry != null)
                    {
                        selectedEntry = psw.Entry;
                        _selectedWoWProc = psw.Entry.ProcessId;
                    }
                    else
                    {
                        _selectedWoWProc = 0; // Cancelled
                    }
                });

                if (_selectedWoWProc == null || _selectedWoWProc == 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatus("Cancelled");
                        Close();
                    });
                    return false;
                }

                // Use the mutex from the dialog directly — it's already acquired
                _processMutex = selectedEntry!.Mutex;
            }

            // Step 4: Attach to selected process
            int wowPid = _selectedWoWProc.Value;
            Logging.Write(Colors.Green, "Please wait a few seconds while CopilotBuddy initializes.");
            Logging.WriteDebug("Using WoW with process ID {0}", wowPid);
            Logging.WriteDebug("Platform: {0}", Environment.Is64BitProcess ? "x64" : "x86");
            Logging.WriteDebug("Executable Path: {0}", AppDomain.CurrentDomain.BaseDirectory);

            try
            {
                var memory = new Memory(wowPid);

                // Verify still logged in before committing. 3.3.5a only —
                // WowB has no InGame offset yet (its login check was skipped above).
                if (!memory.Is64BitTarget && memory.Read<byte>(Styx.Offsets.GlobalOffsets.InGame) == 0)
                {
                    memory.Dispose();
                    Logging.Write(Colors.Red, "Process ID {0} is not logged in game!", wowPid);
                    Dispatcher.Invoke(() => SetStatus("Not logged in"));
                    return false;
                }

                ObjectManager.Initialize(memory);

                // HB 6.2.3: Initialize hotkeys against the WoW window handle so that
                // hotkeys activate when WoW is in the foreground, not CopilotBuddy.
                HotkeysManager.Initialize(ObjectManager.WoWProcess!.MainWindowHandle);
                HotkeysManager.Register("Core_BringBotToForeground", Keys.F7, Styx.Common.ModifierKeys.Shift, BringBotToForeground);

                _memory = memory;
                _executor = ObjectManager.Executor;
                return true;
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, "Error attaching to WoW! - {0}", ex.Message);
                Logging.WriteException(ex);
                Dispatcher.Invoke(() => SetStatus("Attach failed"));
                return false;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // HB 6.2.3/Legion pattern: close all auxiliary windows first.
            foreach (Window window in System.Windows.Application.Current.Windows.Cast<Window>().ToList())
            {
                if (!ReferenceEquals(window, this))
                    window.Close();
            }

            // Stop bot and wait for worker shutdown before process teardown.
            if (TreeRoot.State != TreeRootState.Stopped)
            {
                TreeRoot.Stop("Main window is closing");
                if (!WaitForTreeRootStop(5000))
                    Logging.Write("Bot thread hung during window close");
            }

            // Disable all plugins so overlay/hotkeys and plugin-owned resources are released.
            // IsTearingDown keeps these cleanup-disables from wiping the saved EnabledPlugins list.
            Styx.Plugins.PluginManager.IsTearingDown = true;
            foreach (var pluginContainer in Styx.Plugins.PluginManager.Plugins)
            {
                pluginContainer.Enabled = false;
            }

            // Persist window geometry FIRST so that SaveSettings() below
            // already writes the correct coordinates to UISettings.xml.
            // Use RestoreBounds so we always capture the Normal-state rect
            // even when the window is currently maximized.
            try
            {
                // Always use RestoreBounds to get the Normal-state rect,
                // even if currently maximized — avoids saving the -8 maximized offset.
                Rect bounds = this.RestoreBounds;
                if (bounds.IsEmpty || double.IsNaN(bounds.X))
                    bounds = new Rect(this.Left, this.Top, this.Width, this.Height);

                if (!bounds.IsEmpty && !double.IsNaN(bounds.X) && bounds.X >= 0 && bounds.Y >= 0)
                {
                    UISettings.Instance.MainWindowLocationX = (int)bounds.X;
                    UISettings.Instance.MainWindowLocationY = (int)bounds.Y;
                    UISettings.Instance.MainWindowWidth     = (int)bounds.Width;
                    UISettings.Instance.MainWindowHeight    = (int)bounds.Height;
                }
                UISettings.Instance.MainWindowState = this.WindowState;
                UISettings.Instance.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[UISettings] Failed to save window geometry: " + ex.Message);
            }

            // Save all other per-character settings (HB 4.3.4 pattern).
            // UISettings is already flushed above with the correct geometry.
            SaveSettings();

            // Release process mutex so another CopilotBuddy can claim this WoW
            try
            {
                _processMutex?.Dispose();
                _processMutex = null;
            }
            catch { /* Mutex may already be released */ }

            // Cleanup
            Logging.OnLogMessage -= OnLogMessage;
            BotEvents.OnBotStarted -= BotEvents_OnBotStarted;
            BotEvents.OnBotStopped -= BotEvents_OnBotStopped;
            _infoTimer?.Stop();
        }

        private static bool WaitForTreeRootStop(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (TreeRoot.State == TreeRootState.Stopped)
                    return true;

                Thread.Sleep(50);
            }

            return TreeRoot.State == TreeRootState.Stopped;
        }

        #endregion

        #region Logging

        private void OnLogMessage(ReadOnlyCollection<Logging.LogMessage> messages)
        {
            if (Thread.CurrentThread != Dispatcher.Thread)
            {
                // DispatcherPriority.Background ensures input events (clicks, keyboard)
                // at DispatcherPriority.Input are processed before log rendering.
                // Without this, heavy navigation debug logging starves the UI thread.
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Logging.LogMessageDelegate(OnLogMessage), messages);
                return;
            }

            // Clear if too many lines
            if (_logCount >= MaxLogLines)
            {
                _logParagraph.Inlines.Clear();
                _logCount = 0;
            }

            foreach (var message in messages)
            {
                // Filter by log level - only show messages at or below current LoggingLevel
                if (message.Level > Logging.LoggingLevel)
                    continue;

                // Create colored run
                var brush = new SolidColorBrush(message.Color);
                brush.Freeze();

                var run = new Run(message.ToString() + Environment.NewLine)
                {
                    Foreground = brush,
                    FontFamily = _logFont
                };

                _logParagraph.Inlines.Add(run);
                _logCount++;
            }

            // Auto-scroll to end
            scrollLog.ScrollToEnd();
        }

        #endregion

        #region Info Panel

        private void UpdateInfoPanel()
        {
            /* The grind loop can end on its own - a ring it cannot reach, an empty area, the
             * user's "stop" request. The window must go back to Start when it does, or the
             * next click does nothing and looks broken. */
            if (_grindStarted && !Styx.Logic.Pathing.ClickAimUi.Running)
            {
                _grindStarted = false;
                _isRunning = false;
                btnStart.Visibility = Visibility.Visible;
                btnStopGroup.Visibility = Visibility.Hidden;
                btnLoadProfile.IsEnabled = true;
                cmbBotSelector.IsEnabled = true;
                // Settings needs the object model; on wowb it stays as the attach left it.
                btnSettings.IsEnabled = !Styx.Logic.Pathing.ClickAimUi.IsWowB;
                SetStatus("Grind finished");
            }

            // HB 4.3.4 pattern: always display current values; no IsMeasuring gate.
            // Values start at 0 and accumulate once the bot starts.
            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"XP/HR: {GameStats.XPPerHour:F0}");
            sb.AppendLine($"Kills: {GameStats.MobsKilled} ({GameStats.MobsPerHour:F0}/hr)");
            sb.AppendLine($"Deaths: {GameStats.Deaths} ({GameStats.DeathsPerHour:F0}/hr)");
            sb.AppendLine($"Loots: {GameStats.Loots} ({GameStats.LootsPerHour:F0}/hr)");
            sb.AppendLine($"Honor Gained: {GameStats.HonorGained} ({GameStats.HonorPerHour:F0}/hr)");
            sb.AppendLine($"BGs Won: {GameStats.BGsWon} Lost: {GameStats.BGsLost} Total: {GameStats.BGsCompleted} ({GameStats.BGsPerHour:F0}/hr)");

            // TTL only meaningful below max level (80 in WotLK 3.3.5a)
            Styx.WoWInternals.WoWObjects.LocalPlayer? me = null;
            try { me = ObjectManager.Me; } catch { me = null; }
            if (me != null && me.Level < 80 && GameStats.TimeToLevel > TimeSpan.Zero)
            {
                var ttl = GameStats.TimeToLevel;
                sb.AppendLine($"Time to Level: {(int)ttl.TotalHours} hour(s) {ttl.Minutes:00} minute(s) {ttl.Seconds:00} second(s)");
            }

            // Goal text — polled every second like HB 4.3.4
            var goalText = TreeRoot.GoalText;
            if (!string.IsNullOrEmpty(goalText))
            {
                sb.AppendLine();
                sb.AppendLine($"Goal: {goalText}");
            }

            tbInfoBlock.Text = sb.ToString();
            tbInfoBlock.TextWrapping = System.Windows.TextWrapping.Wrap;
        }

        #endregion

        #region UI Helpers

        private void ToggleButtons(bool enabled)
        {
            btnLoadProfile.IsEnabled = enabled;
            btnStart.IsEnabled = enabled;
            btnSettings.IsEnabled = enabled;
            cmbBotSelector.IsEnabled = enabled;
            btnBotConfig.IsEnabled = enabled;
            btnClassConfig.IsEnabled = enabled;
            btnDevTools.IsEnabled = enabled;
            btnPlugins.IsEnabled = enabled;
            EnhancedMode.IsEnabled = enabled;
        }

        private void SetStatus(string status)
        {
            lblActivityText.Content = status;
        }

        #endregion

        #region Bot Control

        /// <summary>
        /// AfterInitOnAuthSuccess equivalent for CopilotBuddy.
        /// Mirrors Honorbuddy 4.3.4 MainWindow.xaml.cs:419-437 (AfterInitOnAuthSuccess)
        /// and Start():847-866. Consumes command-line arguments forwarded by HBRelog
        /// (and any other external launcher) — loads the profile first, then selects
        /// the requested botbase, then auto-starts the bot.
        ///
        /// Command-line contract (see Styx/Helpers/CommandLine.cs):
        ///   /loadprofile=&lt;absolute path&gt;   — load this profile before starting
        ///   /botname=&lt;name fragment&gt;       — substring-match a BotBase key, case-insensitive
        ///   /autostart                       — start the bot without waiting for user click
        ///
        /// If none of these are present this method is a no-op and the user has to
        /// pick the botbase/profile/Start manually.
        /// </summary>
        private void ApplyAutoStartConfig()
        {
            try
            {
                string autoLoadProfile = CommandLine.AutoLoadProfile;
                string autoStartBot = CommandLine.AutoStartBotName;

                // Step 1 — load profile. Mirrors 4.3.4 AfterInitOnAuthSuccess:428-431.
                if (!string.IsNullOrEmpty(autoLoadProfile))
                {
                    Logging.Write("Auto-load profile from /loadprofile: {0}", autoLoadProfile);
                    try
                    {
                        /* Same split as the button: on wowb the legacy profile object is not
                         * built, because the loop that uses the file does not need it. */
                        if (Styx.Logic.Pathing.ClickAimUi.IsWowB)
                        {
                            string loaded = Styx.Logic.Pathing.ClickAimUi.Load(autoLoadProfile);
                            Logging.Write("{0}", loaded);
                            if (cmbBotSelector.Items.Contains(Styx.Logic.Pathing.ClickAimUi.GrindBotName))
                                cmbBotSelector.SelectedItem = Styx.Logic.Pathing.ClickAimUi.GrindBotName;
                        }
                        else
                        {
                            ProfileManager.LoadNew(autoLoadProfile);
                        }
                        Logging.Write("Profile loaded: {0}", System.IO.Path.GetFileName(autoLoadProfile));
                    }
                    catch (Exception ex)
                    {
                        Logging.Write(Colors.Red, "Failed to auto-load profile '{0}': {1}", autoLoadProfile, ex.Message);
                        Logging.WriteException(ex);
                    }
                }

                // Step 2 — select botbase. Mirrors 4.3.4 MainWindow.xaml.cs:851-859 exactly:
                // substring match on bot.Name (which is the dictionary key). Honorbuddy "start
                // legion" method_18:819-836 uses strict OrdinalIgnoreCase equality on bot.Name.
                // We follow 4.3.4 since it's the version HBRelog was built for.
                //
                // IMPORTANT: bot.Name in CopilotBuddy is the *display* name (QuestBot class returns
                // "Questing", LevelBot returns "Grind", DiscoBot returns "Party Bot"). In HBRelog's
                // BotBase field you must type the bot.Name value, NOT the class name.
                // So /botname=Questing (not /botname=QuestBot) selects the questing bot.
                if (!string.IsNullOrEmpty(autoStartBot))
                {
                    string needle = autoStartBot.ToLowerInvariant();
                    var match = BotManager.Instance.Bots
                        .FirstOrDefault(b => b.Key != null && b.Key.ToLowerInvariant().Contains(needle));
                    if (match.Value != null)
                    {
                        Logging.Write("Auto-select botbase from /botname: {0}", match.Key);
                        BotManager.Instance.SetCurrent(match.Value);
                        // Sync the combobox so the UI matches what HBRelog requested; mirrors
                        // cmbBotSelector.SelectedItem = value at 4.3.4 Start():856.
                        if (cmbBotSelector.Items.Contains(match.Key))
                            cmbBotSelector.SelectedItem = match.Key;
                        else if (CharacterSettings.Instance != null)
                            CharacterSettings.Instance.SelectedBotIndex = 0;
                    }
                    else
                    {
                        Logging.Write(Colors.Red, "/botname='{0}' did not match any loaded bot base (available: {1})",
                            autoStartBot, string.Join(", ", BotManager.Instance.Bots.Keys));
                    }
                }

                // Step 3 — auto-start. Mirrors 4.3.4 AfterInitOnAuthSuccess:432-435.
                // We treat *any* auto-config (loadprofile or botname) as an implicit /autostart,
                // because HBRelog only forwards those two args together with /autostart.
                bool shouldAutoStart = !string.IsNullOrEmpty(autoLoadProfile) || !string.IsNullOrEmpty(autoStartBot);
                if (shouldAutoStart)
                    StartBot();
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, "ApplyAutoStartConfig failed: {0}", ex.Message);
                Logging.WriteException(ex);
            }
        }

        private void StartBot()
        {
            if (_isRunning) return;

            /* wowb.exe: the behaviour tree cannot run, so Start means "run the grind loop on
             * the profile this window loaded". The bot name comes from the list, not from
             * BotManager.Current, because a bot that failed to instantiate still has a name
             * the user can pick - and Grind only needs its name. Doing this before the UI is
             * disabled means a refusal leaves the window usable instead of half-stopped. */
            if (Styx.Logic.Pathing.ClickAimUi.IsWowB)
            {
                string? chosen = cmbBotSelector.SelectedItem?.ToString();
                string report = Styx.Logic.Pathing.ClickAimUi.Start(chosen);
                if (!Styx.Logic.Pathing.ClickAimUi.Running)
                {
                    Logging.Write(Colors.Red, "{0}", report);
                    SetStatus("Not started");
                    return;
                }

                Logging.Write("{0}", report);
                _grindStarted = true;
                _isRunning = true;
                btnStart.Visibility = Visibility.Hidden;
                btnStopGroup.Visibility = Visibility.Visible;
                btnLoadProfile.IsEnabled = false;
                cmbBotSelector.IsEnabled = false;
                btnSettings.IsEnabled = false;
                SetStatus("Grinding...");
                return;
            }

            if (BotManager.Current == null)
            {
                Logging.Write(Colors.Red, "No bot selected!");
                return;
            }

            // Save settings before starting bot (HB 4.3.4 pattern)
            SaveSettings();

            _isRunning = true;
            btnStart.Visibility = Visibility.Hidden;
            btnStopGroup.Visibility = Visibility.Visible;
            btnLoadProfile.IsEnabled = false;
            cmbBotSelector.IsEnabled = false;
            btnSettings.IsEnabled = false;

            try
            {
                Logging.Write("Starting the bot.");
                Logging.Write("Currently Using BotBase : {0}", BotManager.Current.Name);
                if (ObjectManager.Me != null)
                {
                    Logging.Write("Character is a level {0} {1} {2}",
                        ObjectManager.Me.Level,
                        ObjectManager.Me.Race,
                        ObjectManager.Me.Class);
                    Task.Run(() =>
                    {
                        Logging.Write("Current zone is {0}", ObjectManager.Me.RealZoneText);
                    });
                }
                TreeRoot.Start();
                SetStatus("Running...");
            }
            catch (HonorbuddyUnableToStartException ex)
            {
                Logging.Write(Colors.Red, ex.Message);
                Logging.WriteException(ex);
                StopBot();
            }
            catch (UserException ex)
            {
                Logging.Write(Colors.Red, ex.Message);
                Logging.WriteException(ex);
                StopBot();
            }
            catch (Exception ex)
            {
                Logging.Write(Colors.Red, ex.Message);
                Logging.WriteException(ex);
                StopBot();
            }
        }

        private void StopBot()
        {
            if (!_isRunning) return;

            _isRunning = false;
            btnStart.Visibility = Visibility.Visible;
            btnStopGroup.Visibility = Visibility.Hidden;
            // Re-enable all controls that StartBot() disabled. Mirrors BotEvents_OnBotStopped
            // so the UI unlocks even when the worker thread never started (e.g. no profile loaded).
            btnLoadProfile.IsEnabled = true;
            cmbBotSelector.IsEnabled = true;
            btnSettings.IsEnabled = true;
            btnBotConfig.IsEnabled = true;
            btnClassConfig.IsEnabled = true;
            SetStatus("Stopping...");

            Logging.Write("Stopping the bot!");
            if (_grindStarted)
            {
                _grindStarted = false;
                Logging.Write("{0}", Styx.Logic.Pathing.ClickAimUi.Stop());
                SetStatus("Stopped");
                return;
            }
            TreeRoot.Stop();
        }

        #endregion

        #region Button Click Handlers

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            StartBot();
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            StopBot();
        }

        private void BringBotToForeground(Hotkey _)
        {
            if (Dispatcher.Thread != Thread.CurrentThread)
            {
                Dispatcher.Invoke(() => BringBotToForeground(_));
                return;
            }
            if (Visibility == Visibility.Hidden)
                Visibility = Visibility.Visible;
            Activate();
        }

        private void btnStopArrow_Click(object sender, RoutedEventArgs e)
        {
            if (btnStopArrow.ContextMenu != null)
            {
                btnStopArrow.ContextMenu.PlacementTarget = btnStopArrow;
                btnStopArrow.ContextMenu.IsOpen = true;
            }
        }

        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            var item = (System.Windows.Controls.MenuItem)sender;

            /* The grind loop has no tree to pause: Pause stops it and Resume starts the same
             * profile again, which is what the user means by pausing a grind. */
            if (_grindStarted || Styx.Logic.Pathing.ClickAimUi.Paused)
            {
                if (Styx.Logic.Pathing.ClickAimUi.Paused)
                {
                    string resumed = Styx.Logic.Pathing.ClickAimUi.Resume(BotManager.Current?.Name);
                    item.Header = "Pause";
                    Logging.Write("{0}", resumed);
                    SetStatus(Styx.Logic.Pathing.ClickAimUi.Running ? "Grinding..." : resumed);
                }
                else
                {
                    string paused = Styx.Logic.Pathing.ClickAimUi.Pause(BotManager.Current?.Name);
                    item.Header = "Resume";
                    Logging.Write("{0}", paused);
                    SetStatus("Paused");
                }
                return;
            }

            if (TreeRoot.IsPaused)
            {
                item.Header = "Pause";
                TreeRoot.Resume();
                SetStatus("Running...");
            }
            else
            {
                item.Header = "Resume";
                TreeRoot.Pause();
                SetStatus("Paused");
            }
        }

        private void BotEvents_OnBotStopped(EventArgs args)
        {
            if (Dispatcher.Thread != Thread.CurrentThread)
            {
                Dispatcher.BeginInvoke(new Action<EventArgs>(BotEvents_OnBotStopped), args);
                return;
            }

            _isRunning = false;
            btnStart.Visibility = Visibility.Visible;
            btnStopGroup.Visibility = Visibility.Hidden;
            // Reset Pause MenuItem header back to "Pause" (HB 6.2.3 method_36 pattern).
            if (btnStopArrow.ContextMenu?.Items[0] is System.Windows.Controls.MenuItem pauseItem)
                pauseItem.Header = "Pause";
            btnLoadProfile.IsEnabled = true;
            cmbBotSelector.IsEnabled = true;
            btnSettings.IsEnabled = true;
            SetStatus("CopilotBuddy Stopped");
        }

        private void BotEvents_OnBotStarted(EventArgs args)
        {
            if (Dispatcher.Thread != Thread.CurrentThread)
            {
                Dispatcher.BeginInvoke(new Action<EventArgs>(BotEvents_OnBotStarted), args);
                return;
            }

            _isRunning = true;
            btnStart.Visibility = Visibility.Hidden;
            btnStopGroup.Visibility = Visibility.Visible;
            btnLoadProfile.IsEnabled = false;
            cmbBotSelector.IsEnabled = false;
            btnSettings.IsEnabled = false;
            SetStatus("Running...");
        }

        private void cmbBotSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbBotSelector.SelectedItem == null) return;

            string? botName = cmbBotSelector.SelectedItem.ToString();
            /* wowb.exe: only the NAME matters (Start passes it to ClickAimUi) and the bot's
             * configuration form is built on the object model, so nothing else happens here -
             * a config button that throws on click is worse than no config button. */
            if (Styx.Logic.Pathing.ClickAimUi.IsWowB)
            {
                Logging.Write("[Attach] bot selected: {0}", botName ?? "(none)");
                return;
            }

            if (botName != null && BotManager.Instance.Bots.TryGetValue(botName, out BotBase? bot) && bot != null)
            {
                BotManager.Instance.SetCurrent(bot);
                
                // Show Bot Config button — always visible when a bot is selected.
                // The handler checks ConfigurationForm at click time.
                btnBotConfig.Visibility = Visibility.Visible;
                
                // Update selected bot index (no immediate save - HB 4.3.4 pattern)
                if (CharacterSettings.Instance != null)
                    CharacterSettings.Instance.SelectedBotIndex = cmbBotSelector.SelectedIndex;
            }
        }

        private void btnLoadProfile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Profile files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Load Profile",
                InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Default Profiles")
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    /* wowb.exe cannot build the legacy profile object, and the grind loop does
                     * not need it: it reads the XML itself. So Load means "remember this path
                     * and tell me what is in it". */
                    if (Styx.Logic.Pathing.ClickAimUi.IsWowB)
                    {
                        string report = Styx.Logic.Pathing.ClickAimUi.Load(openFileDialog.FileName);
                        Logging.Write("{0}", report);
                        SetStatus(report);
                        return;
                    }

                    ProfileManager.LoadNew(openFileDialog.FileName);
                    Logging.Write($"Profile loaded: {Path.GetFileName(openFileDialog.FileName)}");
                }
                catch (Exception ex)
                {
                    Logging.WriteException(ex);
                    System.Windows.MessageBox.Show(
                        $"Failed to load profile!\n\nError: {ex.Message}",
                        "CopilotBuddy - Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
        }

        private void btnBotConfig_Click(object sender, RoutedEventArgs e)
        {
            if (BotManager.Current == null)
            {
                Logging.Write("No bot selected.");
                return;
            }

            var configWindow = BotManager.Current.ConfigurationWindow;
            if (configWindow != null)
            {
                configWindow.Owner = this;
                configWindow.ShowDialog();
                return;
            }

            var configForm = BotManager.Current.ConfigurationForm;
            if (configForm == null)
            {
                Logging.Write("[{0}] has no configuration window.", BotManager.Current.Name);
                return;
            }

            configForm.ShowDialog();
        }

        private void btnClassConfig_Click(object sender, RoutedEventArgs e)
        {
            // Open class/routine config
            var routine = Styx.Logic.Combat.RoutineManager.Current;
            if (routine != null && routine.WantButton)
            {
                try
                {
                    routine.OnButtonPress();
                }
                catch (Exception ex)
                {
                    Logging.WriteException(ex);
                    System.Windows.MessageBox.Show($"Error opening routine config: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("No combat routine loaded or routine has no configuration.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void btnPlugins_Click(object sender, RoutedEventArgs e)
        {
            var pluginsWindow = new PluginsWindow
            {
                Owner = this
            };
            pluginsWindow.ShowDialog();
        }

        /// <summary>
        /// x64 diagnostic only: walk 40 yd ahead of the character using the
        /// synthesised click mover. Nothing else in the bot is running when this
        /// button exists, so it is the lowest-friction way to test movement.
        /// </summary>
        private void btnAimWalk_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Styx.Logic.Pathing.ClickAimDiagnostics.WalkRelative(40.0);
            }
            catch (Exception ex)
            {
                Logging.Write("[ClickAim] walk failed: " + ex.Message);
            }
        }

        /// <summary>
        /// x64 diagnostic only: remember the current position as a hotspot. Press it
        /// where you want a waypoint; the list is Settings\clickaim-hotspots.xml, which
        /// is an ordinary profile and can be walked or replaced with a real one.
        /// </summary>
        private void btnAimHotspot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Styx.Logic.Pathing.ClickAimDiagnostics.CaptureHotspot())
                    SetStatus(Styx.Logic.Pathing.HotspotFile.Describe());
            }
            catch (Exception ex)
            {
                Logging.Write("[Route] capturing a hotspot failed: " + ex.Message);
            }
        }

        private void btnAimHotspots_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Styx.Logic.Pathing.ClickAimDiagnostics.WalkHotspots();
            }
            catch (Exception ex)
            {
                Logging.Write("[Route] walking the hotspots failed: " + ex.Message);
            }
        }

        private void btnAimPlanHotspots_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Styx.Logic.Pathing.ClickAimDiagnostics.PlanHotspots();
            }
            catch (Exception ex)
            {
                Logging.Write("[Route] planning the hotspots failed: " + ex.Message);
            }
        }

        private void btnAimClearHotspots_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Styx.Logic.Pathing.ClickAimDiagnostics.ClearHotspots())
                    SetStatus("Hotspot list cleared");
            }
            catch (Exception ex)
            {
                Logging.Write("[Route] clearing the hotspots failed: " + ex.Message);
            }
        }

        private void btnDevTools_Click(object sender, RoutedEventArgs e)
        {
            if (ObjectManager.Wow?.Is64BitTarget == true)
            {
                var snapshotWindow = new WowBSnapshotWindow { Owner = this };
                snapshotWindow.Show();
                return;
            }
            var devTools = new DeveloperToolsWindow();
            devTools.Owner = this;
            devTools.Show();
        }

        private void EnhancedMode_Checked(object sender, RoutedEventArgs e)
        {
            // Show all hidden buttons
            btnBotConfig.Visibility = Visibility.Visible;
            btnClassConfig.Visibility = Visibility.Visible;
            btnPlugins.Visibility = Visibility.Visible;
            btnDevTools.Visibility = Visibility.Visible;
            
            // Save setting
            UISettings.Instance.EnhancedMode = true;
            UISettings.Instance.Save();
        }

        private void EnhancedMode_Unchecked(object sender, RoutedEventArgs e)
        {
            // Hide optional buttons
            btnBotConfig.Visibility = Visibility.Hidden;
            btnClassConfig.Visibility = Visibility.Hidden;
            btnPlugins.Visibility = Visibility.Hidden;
            // on wowb this button is the read-only world snapshot view, not Developer Tools
            if (!Styx.Logic.Pathing.ClickAimUi.IsWowB)
                btnDevTools.Visibility = Visibility.Hidden;
            
            // Save setting
            UISettings.Instance.EnhancedMode = false;
            UISettings.Instance.Save();
        }

        #endregion

        #region Settings Management (HB 5.4.8 Pattern)

        /// <summary>
        /// Loads all settings after game attachment.
        /// Creates CharacterSettings instance (per-realm/per-character folder).
        /// Pattern from HB 5.4.8.
        /// </summary>
        private static void LoadSettings()
        {
            if (ObjectManager.Wow == null || !StyxWoW.IsInGame || StyxWoW.Me == null)
                return;

            try
            {
                // Create CharacterSettings instance for the current character
                // Pattern from HB 5.4.8 smethod_0() — called after game attachment
                CharacterSettings.Initialize();

                // Load other per-character settings
                UISettings.Instance.Load();
                StyxSettings.Instance.Load();
                LevelbotSettings.Instance.Load();
                
                Logging.WriteDebug("[Settings] Loaded settings for character: {0}", StyxWoW.Me?.Name ?? "Unknown");
            }
            catch (Exception ex)
            {
                Logging.WriteException(ex);
            }
        }

        /// <summary>
        /// Saves all settings. Called at app close and before bot start.
        /// Pattern from HB 4.3.4.
        /// </summary>
        private static void SaveSettings()
        {
            try
            {
                if (ObjectManager.Wow == null || !ObjectManager.IsInGame)
                    return;

                UISettings.Instance.Save();
                CharacterSettings.Instance?.Save(); // null in WowB read-only mode
                StyxSettings.Instance.Save();
                LevelbotSettings.Instance.Save();
                
                Logging.WriteDebug("[Settings] Saved all settings");
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[Settings] Error saving settings");
                Logging.WriteException(ex);
            }
        }

        #endregion
    }
}
