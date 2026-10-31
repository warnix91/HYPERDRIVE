using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Diagnostics
{
    /// <summary>
    /// Problems during the game's first load, from any mod or the stock game, shown to the player so that they can find
    /// and fix them: errors and exceptions logged while loading, warnings that mean a broken install (unmet dependency,
    /// assembly not loaded), DLLs in GameData that KSP did not load, and loaded DLLs some of whose types cannot load (a
    /// missing dependency). Each problem is charged to a GameData folder: the first mod code in its stack trace, else a
    /// GameData path in its message, else the stock game. While loading, a short notice in the loading screens' style
    /// (any loading screen: drawn by its own OnGUI) fades in when a problem appears, names the last ones for a few seconds
    /// and fades out; at the main menu a window gives the full list, also written to KSP.log.
    /// Read-only: nothing is changed in the game or in the other mods.
    /// </summary>
    internal sealed class LoadProblems : Subsystem
    {
        private sealed class Problem
        {
            public string Key, Condition, Stack, Owner, Summary;
            public LogType Type;
            public int Count;
        }

        private const int MaxProblems = 200;
        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, Problem> s_byKey = new Dictionary<string, Problem>(StringComparer.Ordinal);
        private static readonly List<Problem> s_problems = new List<Problem>();
        private static volatile bool s_capturing;
        private static int s_added;           // under s_lock: problems added so far
        private static int s_built = -1;      // main thread: s_added when the lines were last built
        // ".XmlSerializers": .NET's XmlSerializer always probes for an optional pre-generated assembly "X.XmlSerializers",
        // then generates the serializer itself; the binder's "Cannot resolve assembly" for it is not a problem.
        private static string[] s_ignore = { "Steamworks is not initialized", "Failed to initialize Steam API", ".XmlSerializers" };

        // Display (main thread): built when problems change, drawn without allocating.
        internal static string Title = "", Footer = "";
        internal static string[] Lines = new string[0];   // the last problems, newest first
        internal static int Total;
        internal static int Version;                       // bumped when a problem is added (the notice shows again)

        private bool _screen = true, _menuWindow = true;
        private static int s_maxLines = 2;
        private LoadProblemsView _view;
        private bool _menuShown;

        public override string Name => "LoadProblems";
        public override bool ReadOnly => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _screen = settings.GetBool("showOnLoadingScreen", true);
            _menuWindow = settings.GetBool("menuWindow", true);
            s_maxLines = Mathf.Clamp(settings.GetInt("lines", s_maxLines), 1, 12);
            string ignore = settings.GetString("ignore", string.Join(";", s_ignore));
            s_ignore = ignore.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        protected internal override void OnEnable()
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING)
                throw new SubsystemUnavailableException("only during the game's first load");
            Lang.Ensure();
            s_capturing = true;
            Application.logMessageReceivedThreaded += OnLog;
            CheckAssemblies();
            if (_screen)
            {
                var host = new GameObject("Hyperdrive.LoadProblems");
                UnityEngine.Object.DontDestroyOnLoad(host);
                _view = host.AddComponent<LoadProblemsView>();
            }
        }

        protected internal override void OnDisable()
        {
            s_capturing = false;
            Application.logMessageReceivedThreaded -= OnLog;
            if (_view != null)
                UnityEngine.Object.Destroy(_view.gameObject);
            _view = null;
        }

        protected internal override void OnUpdate()
        {
            int added;
            lock (s_lock)
                added = s_added;
            if (added != s_built)
                Build(added);
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene != GameScenes.MAINMENU || _menuShown)
                return;
            _menuShown = true;
            s_capturing = false;   // the first load is over; later errors are the game's business, as before
            Application.logMessageReceivedThreaded -= OnLog;
            Build(s_added);
            if (_view != null)
                _view.FadeOut();
            Report();
        }

        // ------------------------------------------------------------------ capture (any thread: no Unity API)

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (!s_capturing || condition == null)
                return;
            if (type == LogType.Log || type == LogType.Warning)
            {
                if (!IsInstallWarning(condition))
                    return;
            }
            for (int i = 0; i < s_ignore.Length; i++)
                if (condition.IndexOf(s_ignore[i], StringComparison.Ordinal) >= 0)
                    return;
            if (condition.StartsWith("[Hyperdrive] LogSpamGuard", StringComparison.Ordinal))
                return;
            NoteEvidence(condition);
            Add(condition, stackTrace, type);
        }

        // A failure that confirms a missing dependency: the name in "Cannot resolve assembly: X, ..." or a type-load failure.
        private static void NoteEvidence(string c)
        {
            int at = c.IndexOf("Cannot resolve assembly: ", StringComparison.Ordinal);
            bool typeLoad = c.IndexOf("TypeLoadException", StringComparison.Ordinal) >= 0;
            if (at < 0 && !typeLoad)
                return;
            lock (s_lock)
            {
                if (at >= 0)
                {
                    int start = at + "Cannot resolve assembly: ".Length;
                    int end = c.IndexOf(',', start);
                    s_evidenceNames.Add((end > start ? c.Substring(start, end - start) : c.Substring(start)).Trim());
                }
                if (typeLoad)
                    s_evidenceTypeLoad = true;
                s_evidence++;
            }
        }

        private static bool IsInstallWarning(string c)
        {
            return c.IndexOf("has not met dependency", StringComparison.Ordinal) >= 0
                   || c.IndexOf("is missing", StringComparison.Ordinal) >= 0 && c.IndexOf("AssemblyLoader", StringComparison.Ordinal) >= 0
                   || c.IndexOf("ReflectionTypeLoadException", StringComparison.Ordinal) >= 0
                   || c.IndexOf("Exception loading", StringComparison.Ordinal) >= 0
                   || c.IndexOf("failed to load", StringComparison.OrdinalIgnoreCase) >= 0 && c.IndexOf("ssembly", StringComparison.Ordinal) >= 0;
        }

        /// <summary>A notice from Hyperdrive itself (untested game version, prudent start), shown like the problems.</summary>
        internal static void Notice(string text) => Add(text, "", LogType.Warning, "Hyperdrive");

        private static void Add(string condition, string stackTrace, LogType type, string owner = null)
        {
            string first = FirstLine(condition);
            string frame = FirstLine(stackTrace ?? "");
            string key = (int)type + "|" + first + "|" + frame;
            lock (s_lock)
            {
                if (s_byKey.TryGetValue(key, out Problem p))
                {
                    p.Count++;
                    return;
                }
                if (s_problems.Count >= MaxProblems)
                    return;
                p = new Problem { Key = key, Condition = condition, Stack = stackTrace ?? "", Type = type, Count = 1, Owner = owner };
                s_byKey[key] = p;
                s_problems.Add(p);
                s_added++;
            }
        }

        // The first line with content, rich-text tags removed: a message that starts with a bare "[Tag]" line (ModuleManager's
        // multi-line errors) is summarised by its next line.
        private static string FirstLine(string s)
        {
            foreach (string raw in s.Split('\n'))
            {
                string line = StripTags(raw).Trim();
                if (line.Length == 0 || line.StartsWith("####", StringComparison.Ordinal)
                    || (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal) && line.IndexOf(' ') < 0))
                    continue;
                return line;
            }
            return StripTags(s).Trim();
        }

        private static string StripTags(string s)
        {
            if (s.IndexOf('<') < 0)
                return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<')
                {
                    int end = s.IndexOf('>', i);
                    string tag = end > i ? s.Substring(i + 1, end - i - 1).TrimStart('/') : null;
                    if (tag != null && (tag == "b" || tag == "i" || tag.StartsWith("color", StringComparison.Ordinal) || tag.StartsWith("size", StringComparison.Ordinal)))
                    {
                        i = end;
                        continue;
                    }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ install checks

        // DLLs under GameData (outside PluginData folders, which KSP never loads) that are not among the loaded assemblies,
        // and loaded mod assemblies whose types do not all load: both mean a missing or wrong dependency. The type check
        // runs on a worker thread (reflection only).
        private static void CheckAssemblies()
        {
            try
            {
                var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var mods = new List<KeyValuePair<string, Assembly>>();
                foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
                {
                    if (loaded == null)
                        continue;
                    if (!string.IsNullOrEmpty(loaded.path))
                        loadedPaths.Add(Path.GetFullPath(loaded.path));
                    if (loaded.assembly != null)
                        mods.Add(new KeyValuePair<string, Assembly>(TopFolder(loaded.url) ?? loaded.name, loaded.assembly));
                }
                string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
                foreach (string dll in Directory.GetFiles(gameData, "*.dll", SearchOption.AllDirectories))
                {
                    string rel = dll.Substring(gameData.Length).TrimStart('/', '\\').Replace('\\', '/');
                    if (rel.IndexOf("/PluginData/", StringComparison.OrdinalIgnoreCase) >= 0 || rel.StartsWith("PluginData/", StringComparison.OrdinalIgnoreCase))
                        continue;
                    s_dllFolders[Path.GetFileNameWithoutExtension(dll)] = TopFolder(rel);
                    if (loadedPaths.Contains(Path.GetFullPath(dll)))
                        continue;
                    Add(Lang.F("#HD_LP_DllNotLoaded", Path.GetFileName(dll), rel), "", LogType.Error, TopFolder(rel));
                }
                // Assemblies loaded from GameData that KSP dropped from its list (it does when their attributes cannot be read).
                string fullGameData = Path.GetFullPath(gameData);
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    bool known = false;
                    foreach (KeyValuePair<string, Assembly> mod in mods)
                        if (mod.Value == a) { known = true; break; }
                    if (known || a.IsDynamic)
                        continue;
                    string location;
                    try { location = a.Location; }
                    catch (Exception) { location = null; }
                    string folder = null;
                    if (!string.IsNullOrEmpty(location) && Path.GetFullPath(location).StartsWith(fullGameData, StringComparison.OrdinalIgnoreCase))
                        folder = TopFolder(Path.GetFullPath(location).Substring(fullGameData.Length).Replace('\\', '/'));
                    else if (s_dllFolders.TryGetValue(a.GetName().Name, out string byName))
                        folder = byName;   // loaded from bytes (no location): matched by its DLL's name in GameData
                    if (folder != null)
                        mods.Add(new KeyValuePair<string, Assembly>(folder, a));
                }
                CheckReferences(mods);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    int broken = 0;
                    foreach (KeyValuePair<string, Assembly> mod in mods)
                    {
                        try { mod.Value.GetTypes(); }
                        catch (ReflectionTypeLoadException e)
                        {
                            string missing = null;
                            foreach (Exception le in e.LoaderExceptions)
                                if (le != null) { missing = FirstLine(le.Message); break; }
                            lock (s_lock)
                            {
                                s_broken[mod.Value.GetName().Name] = mod.Key;
                                s_brokenVersion++;
                            }
                            Add(Lang.F("#HD_LP_TypesFail", mod.Value.GetName().Name, missing ?? "?", mod.Key), "", LogType.Error, mod.Key);
                            broken++;
                        }
                        catch (Exception) { }
                    }
                    Log.Info("LoadProblems: types of " + mods.Count + " mod assemblies checked, " + broken + " with types that cannot load");
                });
            }
            catch (Exception e)
            {
                Log.Debug("LoadProblems: assembly check skipped (" + e.Message + ")");
            }
        }

        // Each mod assembly's declared dependencies: one that is not loaded, not one of the game's / Unity's / Mono's
        // assemblies and not a DLL in GameData is a suspect: reported (the mod needs another mod that is not installed)
        // only once the game actually fails on it ("Cannot resolve assembly: X" or a ReflectionTypeLoadException), since
        // a mod may reference an optional assembly it only uses when present.
        private static void CheckReferences(List<KeyValuePair<string, Assembly>> mods)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                available.Add(a.GetName().Name);
            try
            {
                string managed = Path.GetDirectoryName(typeof(GameDatabase).Assembly.Location);
                if (!string.IsNullOrEmpty(managed))
                    foreach (string dll in Directory.GetFiles(managed, "*.dll"))
                        available.Add(Path.GetFileNameWithoutExtension(dll));
            }
            catch (Exception) { }
            foreach (KeyValuePair<string, Assembly> mod in mods)
            {
                AssemblyName[] refs;
                try { refs = mod.Value.GetReferencedAssemblies(); }
                catch (Exception) { continue; }
                foreach (AssemblyName r in refs)
                {
                    string name = r.Name;
                    if (available.Contains(name) || s_dllFolders.ContainsKey(name) || name == "mscorlib" || name == "netstandard"
                        || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Mono.", StringComparison.Ordinal)
                        || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("UnityEngine", StringComparison.Ordinal))
                        continue;
                    lock (s_lock)
                        s_suspects.Add(new[] { name, mod.Value.GetName().Name, mod.Key });
                }
            }
            lock (s_lock)
                s_evidenceSeen = s_evidence;   // failures logged before this check count as well
            Confirm();
        }

        // Suspects named by a failure (or all of them on a type-load failure that names nobody) become problems.
        private static void Confirm()
        {
            List<string[]> confirmed = null;
            lock (s_lock)
            {
                for (int i = s_suspects.Count - 1; i >= 0; i--)
                {
                    string[] s = s_suspects[i];
                    if (!s_evidenceTypeLoad && !s_evidenceNames.Contains(s[0]))
                        continue;
                    (confirmed ?? (confirmed = new List<string[]>())).Add(s);
                    s_suspects.RemoveAt(i);
                    s_broken[s[1]] = s[2];
                    s_brokenVersion++;
                }
            }
            if (confirmed != null)
                foreach (string[] s in confirmed)
                    Add(Lang.F("#HD_LP_MissingDep", s[1], s[0], s[2]), "", LogType.Error, s[2]);
        }

        private static string TopFolder(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            url = url.Replace('\\', '/').TrimStart('/');
            int slash = url.IndexOf('/');
            return slash > 0 ? url.Substring(0, slash) : url;
        }

        // ------------------------------------------------------------------ attribution and display (main thread)

        private static Dictionary<Assembly, string> s_owners;
        private static readonly List<string[]> s_suspects = new List<string[]>();   // { missing, assembly, folder }; under s_lock
        private static readonly HashSet<string> s_evidenceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // under s_lock
        private static bool s_evidenceTypeLoad;   // under s_lock
        private static int s_evidence, s_evidenceSeen;
        private static readonly Dictionary<string, string> s_broken = new Dictionary<string, string>(StringComparer.Ordinal);   // under s_lock
        private static int s_brokenVersion, s_brokenSeen;   // s_brokenVersion under s_lock
        private static readonly Dictionary<string, string> s_dllFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // DLL name -> folder
        private static HashSet<string> s_folders;

        private static void Build(int added)
        {
            bool evidence;
            lock (s_lock)
                evidence = s_evidence != s_evidenceSeen && s_suspects.Count > 0;
            if (evidence)
            {
                lock (s_lock)
                    s_evidenceSeen = s_evidence;
                Confirm();
                lock (s_lock)
                    added = s_added;
            }
            List<Problem> copy;
            int brokenVersion;
            lock (s_lock)
            {
                copy = new List<Problem>(s_problems);
                brokenVersion = s_brokenVersion;
            }
            s_built = added;
            bool reattribute = brokenVersion != s_brokenSeen;
            s_brokenSeen = brokenVersion;
            if (s_owners == null)
            {
                s_owners = new Dictionary<Assembly, string>();
                foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
                    if (loaded?.assembly != null && !s_owners.ContainsKey(loaded.assembly))
                        s_owners[loaded.assembly] = TopFolder(loaded.url) ?? loaded.name;
                s_folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string d in Directory.GetDirectories(Path.Combine(KSPUtil.ApplicationRootPath, "GameData")))
                        s_folders.Add(Path.GetFileName(d));
                }
                catch (Exception) { }
            }
            var owners = new List<string>();
            string stock = Lang.T("#HD_LP_Stock");
            foreach (Problem p in copy)
            {
                if (p.Owner == null || (reattribute && p.Owner == stock))
                {
                    p.Owner = Attribute(p, out string where);
                    p.Summary = Summarize(p.Condition) + (where != null ? " (" + where + ")" : "");
                }
                else if (p.Summary == null)
                    p.Summary = Summarize(p.Condition);
                if (!owners.Contains(p.Owner))
                    owners.Add(p.Owner);
            }
            if (copy.Count == Total && !reattribute)
                return;
            Total = copy.Count;
            Version++;
            Title = Total > 1 ? Lang.F("#HD_LP_TitleN", Total) : Lang.T("#HD_LP_Title1");
            Footer = Lang.T("#HD_LP_Footer");
            int n = Math.Min(copy.Count, s_maxLines);
            var lines = new string[n];
            for (int i = 0; i < n; i++)
            {
                Problem p = copy[copy.Count - 1 - i];
                string text = p.Owner + "  —  " + p.Summary;
                lines[i] = text.Length > 120 ? text.Substring(0, 117) + "..." : text;
            }
            Lines = lines;
        }

        // The mod charged with a problem: the first frame of its stack trace whose type lives in a mod assembly (not the
        // log plumbing), else a GameData folder named in the message, else the stock game.
        private static string Attribute(Problem p, out string where)
        {
            where = null;
            foreach (string raw in p.Stack.Split('\n'))
            {
                string type = FrameType(raw.Trim(), out string method);
                if (type == null || type.StartsWith("UnityEngine.", StringComparison.Ordinal) || type.StartsWith("System.", StringComparison.Ordinal)
                    || type.StartsWith("Mono.", StringComparison.Ordinal) || type.IndexOf("LogHandler", StringComparison.Ordinal) >= 0
                    || type.IndexOf("WorkerLogFilter", StringComparison.Ordinal) >= 0 || type.IndexOf("UnityLogHandle", StringComparison.Ordinal) >= 0)
                    continue;
                foreach (KeyValuePair<Assembly, string> owner in s_owners)
                {
                    Type t;
                    try { t = owner.Key.GetType(type, false); }
                    catch (Exception) { t = null; }
                    if (t == null)
                        continue;
                    where = ShortType(type) + "." + method;
                    return owner.Value;
                }
                if (where == null && method != null)
                    where = ShortType(type) + "." + method;   // stock code: kept as the place, in case no mod is found
            }
            // "... in loading Some.Mod, Version=..." (ModuleManager, Harmony): the assembly named in the message.
            foreach (KeyValuePair<Assembly, string> owner in s_owners)
            {
                string asm = owner.Key.GetName().Name;
                if (p.Condition.IndexOf(asm + ", Version=", StringComparison.Ordinal) >= 0 || p.Condition.IndexOf("'" + asm + "'", StringComparison.Ordinal) >= 0)
                    return owner.Value;
            }
            // An assembly KSP did not load, named the same way: its DLL file's folder.
            int version = p.Condition.IndexOf(", Version=", StringComparison.Ordinal);
            if (version > 0)
            {
                int start = p.Condition.LastIndexOfAny(new[] { ' ', '\'', '"', '(' }, version - 1) + 1;
                string name = p.Condition.Substring(start, version - start);
                if (s_dllFolders.TryGetValue(name, out string dllFolder) && dllFolder != null)
                    return dllFolder;
            }
            string folder = FolderInText(p.Condition);
            if (folder != null)
                return folder;
            // The game's loader reporting a type that cannot load (it does not name the assembly): the broken mod
            // assemblies found by the type check.
            if (p.Condition.IndexOf("TypeLoadException", StringComparison.Ordinal) >= 0 || p.Condition.IndexOf("Cannot resolve assembly", StringComparison.Ordinal) >= 0
                || p.Condition.IndexOf("Could not load file or assembly", StringComparison.Ordinal) >= 0)
            {
                lock (s_lock)
                    if (s_broken.Count > 0)
                        return string.Join(", ", new List<string>(s_broken.Values).ToArray());
            }
            return Lang.T("#HD_LP_Stock");
        }

        // "  at NS.Type.Method (args) [0x..] in <...>:0" or "NS.Type:Method(args)" -> "NS.Type", "Method".
        private static string FrameType(string line, out string method)
        {
            method = null;
            if (line.Length == 0)
                return null;
            if (line.StartsWith("at ", StringComparison.Ordinal))
                line = line.Substring(3);
            int paren = line.IndexOf('(');
            string head = paren > 0 ? line.Substring(0, paren).Trim() : line;
            int colon = head.LastIndexOf(':');
            int cut = colon > 0 ? colon : head.LastIndexOf('.');
            if (cut <= 0)
                return null;
            method = head.Substring(cut + 1).Trim();
            string type = head.Substring(0, cut).Trim();
            int generic = type.IndexOf('[');
            if (generic > 0)
                type = type.Substring(0, generic);
            int angle = type.IndexOf('<');   // compiler-made types: <Method>d__1
            if (angle > 0)
                type = type.Substring(0, angle).TrimEnd('+', '.');
            return type.Replace('/', '+');
        }

        private static string ShortType(string type)
        {
            int dot = type.LastIndexOf('.');
            return dot >= 0 ? type.Substring(dot + 1) : type;
        }

        private static string FolderInText(string text)
        {
            int at = text.IndexOf("GameData", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                string rest = text.Substring(at + 8).TrimStart('/', '\\');
                int end = rest.IndexOfAny(new[] { '/', '\\', ' ', '\'', '"' });
                string folder = end > 0 ? rest.Substring(0, end) : end < 0 && rest.Length > 0 ? rest : null;
                if (folder != null && s_folders.Contains(folder))
                    return folder;
            }
            // "'Folder/..." as in "Compiling Part 'Folder/Parts/...'"
            int q = text.IndexOf('\'');
            while (q >= 0 && q < text.Length - 1)
            {
                int slash = text.IndexOf('/', q + 1);
                if (slash > q + 1)
                {
                    string candidate = text.Substring(q + 1, slash - q - 1);
                    if (s_folders.Contains(candidate))
                        return candidate;
                }
                q = text.IndexOf('\'', q + 1);
            }
            return null;
        }

        private static string Summarize(string condition)
        {
            string first = FirstLine(condition);
            // A long dump (ModuleManager copies its whole log into one error): the first line that reports an error.
            if (condition.IndexOf('\n') >= 0)
                foreach (string raw in condition.Split('\n'))
                {
                    string line = StripTags(raw).Trim();
                    if (line.IndexOf("[ERR", StringComparison.Ordinal) >= 0 || line.IndexOf("Exception", StringComparison.Ordinal) >= 0
                        || line.IndexOf("rror", StringComparison.Ordinal) >= 0)
                    {
                        first = line;
                        break;
                    }
                }
            int gameData = first.IndexOf(" — GameData/", StringComparison.Ordinal);
            if (gameData > 0)
                first = first.Substring(0, gameData);   // the folder is shown in front of the line already
            int colon = first.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && first.Substring(0, colon).EndsWith("Exception", StringComparison.Ordinal))
            {
                string name = first.Substring(0, colon);
                int dot = name.LastIndexOf('.');
                return dot >= 0 ? name.Substring(dot + 1) : name;
            }
            return first.Length > 110 ? first.Substring(0, 107) + "..." : first;
        }

        private void Report()
        {
            if (Total == 0)
            {
                Log.Info("LoadProblems: no errors while loading");
                return;
            }
            List<Problem> copy;
            lock (s_lock)
                copy = new List<Problem>(s_problems);
            var log = new StringBuilder();
            log.Append("LoadProblems: ").Append(Total).Append(" problem(s) while loading:");
            var window = new StringBuilder();
            window.Append(Lang.T("#HD_LP_WindowIntro")).Append("\n\n");
            int shown = 0;
            foreach (Problem p in copy)
            {
                log.Append("\n  [").Append(p.Owner).Append("] ").Append(FirstLine(p.Condition));
                if (p.Count > 1)
                    log.Append(" (x").Append(p.Count).Append(')');
                string frame = FirstLine(p.Stack);
                if (frame.Length > 0)
                    log.Append("\n      at ").Append(frame);
                if (shown < 15)
                {
                    window.Append("• ").Append(p.Owner).Append(" : ").Append(p.Summary);
                    if (p.Count > 1)
                        window.Append(" ×").Append(p.Count);
                    window.Append('\n');
                    shown++;
                }
            }
            if (copy.Count > shown)
                window.Append(Lang.F("#HD_LP_More", copy.Count - shown));
            Log.Warn(log.ToString());
            if (!_menuWindow)
                return;
            try
            {
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "HyperdriveLoadProblems",
                    Lang.T("#HD_LP_WindowTitle"),
                    window.ToString(), Lang.T("#HD_LP_OK"), false, HighLogic.UISkin);
            }
            catch (Exception e)
            {
                Log.Warn("LoadProblems: menu window not shown (" + e.Message + ")");
            }
        }
    }

    /// <summary>
    /// The loading-screen notice, in the loading screens' look (KSP's Calibri, thin pale text, aligned under the
    /// HYPERDRIVE mark, a hairline accent; no panel): fades in when a problem is added, stays a few seconds, fades out.
    /// </summary>
    internal sealed class LoadProblemsView : MonoBehaviour
    {
        private const float FadeIn = 0.35f, Hold = 7f, FadeAway = 1.2f;
        private GUIStyle _title, _line, _footer;
        private readonly GUIContent _content = new GUIContent();
        private int _shownVersion;
        private float _shownAt = -100f;
        private bool _closing;

        internal void FadeOut() => _closing = true;

        private void OnGUI()
        {
            GUI.depth = -20000;   // over the loading screens (WarpScreen and SceneLoadScreen draw at -10000)
            if (Event.current.type != EventType.Repaint || LoadProblems.Total == 0)
                return;
            if (Rendering.WarpScreenView.IntroPlaying)
            {
                _shownVersion = -1;   // shown once the intro's cards are gone
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (LoadProblems.Version != _shownVersion && !_closing)
            {
                // A new problem: show again (from where the fade is, so it does not blink).
                float t0 = now - _shownAt;
                _shownAt = t0 > FadeIn && t0 < FadeIn + Hold ? now - FadeIn : now;
                _shownVersion = LoadProblems.Version;
            }
            float t = now - _shownAt;
            float alpha = t < FadeIn ? t / FadeIn : t < FadeIn + Hold ? 1f : 1f - (t - FadeIn - Hold) / FadeAway;
            if (alpha <= 0f)
            {
                if (_closing)
                    Destroy(gameObject);
                return;
            }
            alpha = alpha * alpha * (3f - 2f * alpha);

            float w = Screen.width, h = Screen.height;
            EnsureStyles(h);
            float left = w * 0.075f, y = h * 0.108f, width = w * 0.42f;
            string[] lines = LoadProblems.Lines;
            Color old = GUI.color;

            _content.text = LoadProblems.Title;
            float titleH = _title.CalcHeight(_content, width);
            float block = titleH + h * 0.006f;
            for (int i = 0; i < lines.Length; i++)
            {
                _content.text = lines[i];
                block += _line.CalcHeight(_content, width);
            }
            _content.text = LoadProblems.Footer;
            float footerH = _footer.CalcHeight(_content, width);
            block += h * 0.004f + footerH;

            GUI.color = new Color(1f, 0.72f, 0.38f, 0.55f * alpha);
            GUI.DrawTexture(new Rect(left - w * 0.008f, y + h * 0.002f, Mathf.Max(1f, h * 0.0013f), block), Texture2D.whiteTexture);

            GUI.color = new Color(1f, 0.74f, 0.42f, 0.8f * alpha);
            _content.text = LoadProblems.Title;
            GUI.Label(new Rect(left, y, width, titleH), _content, _title);
            float ly = y + titleH + h * 0.006f;
            GUI.color = new Color(0.95f, 0.96f, 0.99f, 0.72f * alpha);
            for (int i = 0; i < lines.Length; i++)
            {
                _content.text = lines[i];
                float lh = _line.CalcHeight(_content, width);
                GUI.Label(new Rect(left, ly, width, lh), _content, _line);
                ly += lh;
            }
            GUI.color = new Color(0.95f, 0.96f, 0.99f, 0.38f * alpha);
            _content.text = LoadProblems.Footer;
            GUI.Label(new Rect(left, ly + h * 0.004f, width, footerH), _content, _footer);
            GUI.color = old;
        }

        private void EnsureStyles(float h)
        {
            int lineSize = Mathf.Max(11, Mathf.RoundToInt(h * 0.0155f)), smallSize = Mathf.Max(10, Mathf.RoundToInt(h * 0.0118f));
            if (_line != null && _line.fontSize == lineSize)
                return;
            Font light = null, regular = null;
            foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f == null || !f.dynamic)
                    continue;
                if (string.Equals(f.name, "calibril", StringComparison.OrdinalIgnoreCase)) light = f;
                else if (string.Equals(f.name, "calibri", StringComparison.OrdinalIgnoreCase)) regular = f;
            }
            _line = new GUIStyle { font = light ?? regular, fontSize = lineSize, wordWrap = true, richText = false, alignment = TextAnchor.UpperLeft };
            _line.normal.textColor = Color.white;
            _title = new GUIStyle(_line) { font = regular ?? light, fontSize = smallSize };
            _footer = new GUIStyle(_line) { font = light ?? regular, fontSize = smallSize };
        }
    }
}
