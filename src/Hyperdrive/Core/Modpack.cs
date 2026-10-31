using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Hyperdrive.Core
{
    /// <summary>
    /// A short fingerprint of this install's mods (GameData folders, loaded plugins and their versions) and of the machine
    /// (processor, threads, graphics card): what Hyperdrive learns about an install (where its loading freezes, which of two
    /// overlapping optimizations is faster) is kept with it and learned again when either changes.
    /// </summary>
    internal static class Modpack
    {
        private static string s_fingerprint;

        internal static string Fingerprint
        {
            get
            {
                if (s_fingerprint == null)
                    s_fingerprint = Compute();
                return s_fingerprint;
            }
        }

        private static string Compute()
        {
            var names = new List<string>();
            try
            {
                string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
                foreach (string dir in Directory.GetDirectories(gameData))
                    names.Add(Path.GetFileName(dir));
            }
            catch (Exception) { }
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                if (a != null)
                    names.Add(a.name + "@" + (a.assembly != null ? a.assembly.GetName().Version.ToString() : "?"));
            names.Sort(StringComparer.Ordinal);
            names.Add("machine:" + SystemInfo.processorType + "|" + SystemInfo.processorCount + "|" + SystemInfo.graphicsDeviceName);
            uint hash = 2166136261;   // FNV-1a over the sorted names, then the machine
            foreach (string n in names)
            {
                foreach (char c in n)
                    hash = (hash ^ c) * 16777619;
                hash = (hash ^ '|') * 16777619;
            }
            return names.Count + "-" + hash.ToString("x8");
        }
    }
}
