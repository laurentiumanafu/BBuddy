using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using GreenMagic;
using MahApps.Metro.Controls;
using Styx;
using Styx.Offsets;
using Styx.WoWInternals;

namespace CopilotBuddy.UI
{
    /// <summary>
    /// Process selector dialog shown when multiple WoW instances are running.
    /// Ported from HB 4.3.4 ProcessSelectorWindow with 6.2.3 improvements.
    /// </summary>
    public partial class ProcessSelectorWindow : MetroWindow
    {
        #region Fields

        private readonly List<ProcessEntry> _processes = new List<ProcessEntry>();

        #endregion

        #region Constructor

        public ProcessSelectorWindow()
        {
            InitializeComponent();
            RefreshProcesses();
        }

        #endregion

        #region Properties

        /// <summary>The selected process entry, set when user clicks Select.</summary>
        public ProcessEntry? Entry { get; set; }

        #endregion

        #region Button Handlers

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshProcesses();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            // Release all mutexes on cancel
            foreach (var pe in _processes)
                pe.Mutex?.Close();
            _processes.Clear();
            DialogResult = false;
        }

        private void btnSelect_Click(object sender, RoutedEventArgs e)
        {
            Entry = cbProcesses.SelectedItem as ProcessEntry;
            // Release mutexes for non-selected entries before closing
            foreach (var pe in _processes)
            {
                if (pe != Entry)
                    pe.Mutex?.Close();
            }
            DialogResult = true;
        }

        #endregion

        #region Process Enumeration

        /// <summary>
        /// Enumerates all WoW.exe processes, reads character names from memory,
        /// filters out processes already claimed by another CopilotBuddy instance,
        /// and populates the combo box.
        /// Pattern from HB 4.3.4 ProcessSelectorWindow.RefreshProcesses().
        /// </summary>
        private void RefreshProcesses()
        {
            // Close previously held mutexes
            foreach (var pe in _processes)
                pe.Mutex?.Close();

            _processes.Clear();
            cbProcesses.Items.Clear();

            Process[] wowProcesses = new[] { "Wow", "WoW", "Luacct", "WowB" }
                .SelectMany(n => Process.GetProcessesByName(n))
                .DistinctBy(p => p.Id)
                .ToArray();

            for (int i = 0; i < wowProcesses.Length; i++)
            {
                try
                {
                    // HB 6.2.3: check HasExited before reading memory
                    if (wowProcesses[i].HasExited)
                        continue;

                    // Match the auto-attach discovery rules, including build
                    // 70291's split version number and the WowB process name.
                    Process process = wowProcesses[i];
                    int build = ObjectManager.GetBuildNumber(process);
                    if (build < 0 || (!ObjectManager.IsSupportedBuild(build) && build != 0 &&
                        !process.ProcessName.Equals("WowB", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    using var memory = new Memory(process.Id);
                    if (!memory.IsProcessOpen)
                        continue;

                    string? characterName;
                    if (memory.Is64BitTarget)
                    {
                        // The 3.3.5a InGame and PlayerName offsets do not
                        // apply to WowB. Like auto-attach, do not assume a
                        // login offset; read the known name only for 70291.
                        characterName = build == ObjectManager.SupportedBuildWowB &&
                            process.ProcessName.Equals("WowB", StringComparison.OrdinalIgnoreCase)
                            ? memory.ReadString64(memory.GetAbsolute64(WowBOffsets.PlayerNamePrimary), 48)
                            : null;
                    }
                    else
                    {
                        if (memory.Read<byte>(GlobalOffsets.InGame) == 0)
                            continue;
                        characterName = memory.ReadString(GlobalOffsets.PlayerName);
                    }
                    if (string.IsNullOrEmpty(characterName))
                        characterName = "Unknown";

                    // Try to acquire the process mutex
                    Mutex mutex = ProcessMutex.Create(wowProcesses[i].Id, out bool createdNew);
                    if (!createdNew)
                    {
                        // Another CopilotBuddy already owns this process — skip
                        mutex.Close();
                        continue;
                    }

                    // Available! Add to list (keep mutex held so another instance can't grab it)
                    var entry = new ProcessEntry(characterName, wowProcesses[i].Id, mutex);
                    _processes.Add(entry);
                    cbProcesses.Items.Add(entry);
                }
                catch
                {
                    // Process may have exited or access denied — skip
                }
            }

            if (cbProcesses.Items.Count > 0)
                cbProcesses.SelectedIndex = 0;
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Represents a WoW process available for attachment.
        /// Ported from HB 4.3.4 ProcessSelectorWindow.ProcessEntry.
        /// </summary>
        public class ProcessEntry
        {
            private readonly string _characterName;

            public ProcessEntry(string characterName, int processId, Mutex mutex)
            {
                _characterName = characterName;
                ProcessId = processId;
                Mutex = mutex;
            }

            /// <summary>The WoW process ID.</summary>
            public readonly int ProcessId;

            /// <summary>The mutex claiming ownership of this process.</summary>
            public readonly Mutex Mutex;

            public override string ToString()
            {
                return $"PID: {ProcessId} logged in on: {_characterName}";
            }
        }

        #endregion
    }
}
