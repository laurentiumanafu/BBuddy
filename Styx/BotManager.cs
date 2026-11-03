#nullable disable
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Styx.Helpers;
using Styx.Logic.BehaviorTree;

namespace Styx
{
    /// <summary>
    /// Manages bot loading, compilation, and selection.
    /// </summary>
    public class BotManager
    {
        #region Fields

        private readonly Dictionary<string, BotBase> _bots = new Dictionary<string, BotBase>(StringComparer.OrdinalIgnoreCase);
        private static BotBase _current;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the currently active bot.
        /// </summary>
        public static BotBase Current
        {
            get { return _current; }
            private set { _current = value; }
        }

        /// <summary>
        /// Gets all loaded bots.
        /// </summary>
        public Dictionary<string, BotBase> Bots
        {
            get { return GetBots(); }
        }

        /// <summary>
        /// Singleton instance.
        /// </summary>
        public static BotManager Instance { get; } = new BotManager();

        #endregion

        #region Constructor

        public BotManager()
        {
            // Load built-in bots from the current assembly
            LoadBotsFromCurrentAssembly();
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gets the internal bot dictionary.
        /// </summary>
        public Dictionary<string, BotBase> GetBots()
        {
            return _bots;
        }

        /// <summary>
        /// Loads built-in bots from the current assembly (CopilotBuddy.exe).
        /// </summary>
        private void LoadBotsFromCurrentAssembly()
        {
            try
            {
                Assembly currentAssembly = Assembly.GetExecutingAssembly();
                Type botBaseType = typeof(BotBase);

                Logging.WriteDebug("Loading built-in bots from assembly: {0}", currentAssembly.GetName().Name);

                foreach (Type type in currentAssembly.GetTypes())
                {
                    if (type.IsSubclassOf(botBaseType) && !type.IsAbstract)
                    {
                        try
                        {
                            Logging.WriteDebug("Found bot type: {0}", type.FullName);
                            BotBase bot = (BotBase)Activator.CreateInstance(type);
                            if (bot != null && !string.IsNullOrEmpty(bot.Name))
                            {
                                Add(bot);
                                Logging.WriteDebug("Loaded built-in bot: {0}", bot.Name);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logging.Write("Could not create instance of bot type: {0}", type.FullName);
                            Logging.WriteException(ex);
                        }
                    }
                }

                Logging.WriteDebug("Built-in bot loading complete. {0} bots loaded.", _bots.Count);
            }
            catch (Exception ex)
            {
                Logging.Write("Error loading built-in bots from assembly");
                Logging.WriteException(ex);
            }
        }

        /// <summary>
        /// Sets the current active bot.
        /// </summary>
        public void SetCurrent(BotBase bot)
        {
            if (Current != null && TreeRoot.IsRunning)
            {
                TreeRoot.Stop();
            }
            BotBase oldBot = Current;
            Logging.Write("Changing current bot to: {0}", bot.Name);
            Current = bot;

            // Fire bot changed event
            BotEvents.RaiseBotChanged(new BotEvents.BotChangedEventArgs 
            { 
                OldBot = oldBot?.Name ?? string.Empty, 
                NewBot = bot.Name 
            });
        }

        /// <summary>
        /// Loads bots from a directory.
        /// Compiles .cs files and loads .dll assemblies.
        /// </summary>
        public void LoadBots(string path)
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException("Bots directory not found: " + path);
            }

            Logging.Write("Loading bots from: {0}", path);

            // Use ClassCollection to compile and load
            var classCollection = new ClassCollection<BotBase>();
            var sourceFiles = new List<string>();

            // Gather all .cs files recursively
            sourceFiles.AddRange(Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories));
            Logging.Write("Found {0} source files", sourceFiles.Count);

            // Gather all .dll files - load directly, excluding Temp/ subdirectories
            var dllFiles = Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Temp" + Path.DirectorySeparatorChar))
                .ToArray();
            Logging.Write("Found {0} dll files", dllFiles.Length);
            
            foreach (string dllFile in dllFiles)
            {
                try
                {
                    LoadBotsFromAssembly(dllFile);
                }
                catch (Exception ex)
                {
                    Logging.Write("Could not load bot assembly: {0}", dllFile);
                    Logging.WriteException(ex);
                }
            }

            // Group source files by directory so multi-file bots compile together.
            // Files in the root Bots/ folder compile individually.
            // Files in a subdirectory (e.g. Bots/LazyRaider/) compile as a batch.
            var rootFiles = new List<string>();
            var dirGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string sourceFile in sourceFiles)
            {
                string? dir = Path.GetDirectoryName(sourceFile);
                // Files directly in the root bots folder → compile individually
                if (dir != null && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    rootFiles.Add(sourceFile);
                }
                else if (dir != null)
                {
                    // Find the immediate subdirectory under path
                    string relative = Path.GetRelativePath(path, dir);
                    string topDir = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                    string groupKey = Path.Combine(path, topDir);
                    if (!dirGroups.ContainsKey(groupKey))
                        dirGroups[groupKey] = new List<string>();
                    dirGroups[groupKey].Add(sourceFile);
                }
            }

            // Compile root-level files individually
            foreach (string sourceFile in rootFiles)
            {
                Logging.Write("Compiling: {0}", Path.GetFileName(sourceFile));
                CompilerResults compilerResults;
                classCollection.CompileAndLoadFrom(sourceFile, out compilerResults);

                if (compilerResults != null && compilerResults.Errors.HasErrors)
                {
                    Logging.Write("Could not compile bot: {0}", sourceFile);
                    Logging.Write(Utilities.FormatCompilerErrors(compilerResults));
                }
                else if (compilerResults != null)
                {
                    Logging.Write("Compiled successfully: {0}", Path.GetFileName(sourceFile));
                }
            }

            // Compile subdirectory groups as a batch (pass directory path to SourceCompiler)
            foreach (var kvp in dirGroups)
            {
                string dirPath = kvp.Key;
                string dirName = Path.GetFileName(dirPath);
                Logging.Write("Compiling directory: {0}/ ({1} files)", dirName, kvp.Value.Count);
                CompilerResults compilerResults;
                classCollection.CompileAndLoadFrom(dirPath, out compilerResults);

                if (compilerResults != null && compilerResults.Errors.HasErrors)
                {
                    Logging.Write("Could not compile bot directory: {0}/", dirName);
                    Logging.Write(Utilities.FormatCompilerErrors(compilerResults));
                }
                else if (compilerResults != null)
                {
                    Logging.Write("Compiled successfully: {0}/", dirName);
                }
            }

            // Add all compiled bots
            Logging.Write("ClassCollection contains {0} compiled bots", classCollection.Count);
            foreach (BotBase bot in classCollection)
            {
                Logging.Write("Adding bot: {0}", bot.Name);
                Add(bot.Name, bot);
            }

            Logging.Write("Bot loading complete. {0} bots loaded.", _bots.Count);
        }

        /// <summary>
        /// Loads bots from an assembly file.
        /// </summary>
        private void LoadBotsFromAssembly(string assemblyPath)
        {
            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            Type botBaseType = typeof(BotBase);

            foreach (Type type in assembly.GetTypes())
            {
                if (type.IsSubclassOf(botBaseType) && !type.IsAbstract)
                {
                    try
                    {
                        BotBase bot = (BotBase)Activator.CreateInstance(type);
                        Add(bot);
                    }
                    catch (Exception ex)
                    {
                        Logging.Write("Could not create instance of bot type: {0}", type.FullName);
                        Logging.WriteException(ex);
                    }
                }
            }
        }

        /// <summary>
        /// Adds a bot to the manager.
        /// </summary>
        public void Add(string name, BotBase bot)
        {
            if (_bots != null && !_bots.ContainsKey(name))
            {
                _bots.Add(name, bot);
                Logging.Write("New bot added: {0}", name);
            }
        }

        /// <summary>
        /// Adds a bot to the manager using its Name property.
        /// </summary>
        public void Add(BotBase bot)
        {
            if (_bots != null && !_bots.ContainsKey(bot.Name))
            {
                Add(bot.Name, bot);
            }
        }

        /// <summary>
        /// Removes a bot by name.
        /// </summary>
        public bool Remove(string name)
        {
            if (Current != null && string.Equals(Current.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                TreeRoot.Stop();
            }
            if (_bots != null && _bots.ContainsKey(name))
            {
                return _bots.Remove(name);
            }
            Logging.Write(LogLevel.Normal, "Bot is not present: {0}", name);
            return false;
        }

        /// <summary>
        /// Gets a bot by name.
        /// </summary>
        public BotBase GetBotByName(string name)
        {
            if (_bots.TryGetValue(name, out BotBase bot))
            {
                return bot;
            }
            return null;
        }

        /// <summary>
        /// Checks if a bot exists.
        /// </summary>
        public bool Contains(string name)
        {
            return _bots.ContainsKey(name);
        }

        /// <summary>
        /// Clears all bots.
        /// </summary>
        public void Clear()
        {
            _bots.Clear();
        }

        #endregion
    }
}
