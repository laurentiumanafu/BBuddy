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

            Process[] wowProcesses = ClientAttachPolicy.ProcessNames()
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

                    // Match auto-attach, including experimental build 70707.
                    Process process = wowProcesses[i];
                    int build = ObjectManager.GetBuildNumber(process);
                    if (!ClientAttachPolicy.IsCandidate(build, process.ProcessName))
                        continue;

                    using var memory = new Memory(process.Id);
                    if (!memory.IsProcessOpen || (ClientAttachPolicy.IsExperimentalBuild(build) && !memory.Is64BitTarget))
                        continue;

                    string? characterName;
                    if (memory.Is64BitTarget)
                    {
                        // The 70707 name is a candidate read, not login validation.
                        // Keep the existing 70291 selector behavior unchanged.
                        characterName = ClientAttachPolicy.IsExperimentalBuild(build)
                            ? memory.ReadString64(memory.GetAbsolute64(build == ClientAttachPolicy.ExperimentalBuild70888 ? Wow70888Offsets.PlayerNamePrimary : build == ClientAttachPolicy.ExperimentalBuild70862 ? Wow70862Offsets.PlayerNamePrimary : build == ClientAttachPolicy.ExperimentalBuild70790 ? Wow70790Offsets.PlayerNamePrimary : build == ClientAttachPolicy.ExperimentalBuild70755 ? Wow70755Offsets.PlayerNamePrimary : Wow70707Offsets.PlayerNamePrimary), 32)
                            : build == ObjectManager.SupportedBuildWowB &&
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
                    var entry = new ProcessEntry(characterName, wowProcesses[i].Id, build, mutex);
                    _processes.Add(entry);
                }
                catch
                {
                    // Process may have exited or access denied — skip
                }
            }

            // The highest registered build is the default; unknown builds are not assumed compatible.
            // Keep the selector for multiple processes, including multiple
            // characters on the preferred build.
            foreach (var entry in _processes.OrderByDescending(p => ClientAttachPolicy.SelectionPriority(p.Build))
                                            .ThenBy(p => p.ProcessId))
                cbProcesses.Items.Add(entry);
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

            public ProcessEntry(string characterName, int processId, int build, Mutex mutex)
            {
                _characterName = characterName;
                ProcessId = processId;
                Build = build;
                Mutex = mutex;
            }

            /// <summary>The WoW process ID.</summary>
            public readonly int ProcessId;

            /// <summary>Observed executable build; 0 means unversioned.</summary>
            public readonly int Build;

            /// <summary>The mutex claiming ownership of this process.</summary>
            public readonly Mutex Mutex;

            public override string ToString()
            {
                string buildText = Build > 0 ? Build.ToString() : "unknown";
                string preferred = Build == ClientAttachPolicy.DefaultBuild ? " (default)" : "";
                return $"PID: {ProcessId}, build {buildText}{preferred}: {_characterName}";
            }
        }

        #endregion
    }
}
