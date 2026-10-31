using System;
using System.Collections.Generic;
using System.IO;

namespace Hyperdrive.Core
{
    /// <summary>
    /// The player's overrides in PluginData/Hyperdrive.user.cfg (never overwritten by updates or builds), written by the
    /// Hyperdrive panel and the Hyperdrive section of KSP's settings; read at the next start. Values written during this
    /// session are remembered so the panels show them before the restart.
    /// </summary>
    internal static class UserConfig
    {
        private static readonly Dictionary<string, string> s_written = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>A switch of Hyperdrive.cfg's SUBSYSTEMS as it will be at the next start.</summary>
        internal static bool Subsystem(string name, bool defaultValue)
        {
            if (s_written.TryGetValue("SUBSYSTEMS/" + name, out string v))
                return v == "true";
            HyperdriveSettings settings = HyperdriveRuntime.Settings;
            return settings != null ? settings.IsSubsystemEnabled(name, defaultValue) : defaultValue;
        }

        /// <summary>A setting of a Hyperdrive.cfg section as it will be at the next start.</summary>
        internal static bool Bool(string section, string key, bool defaultValue)
        {
            if (s_written.TryGetValue(section + "/" + key, out string v))
                return v == "true";
            HyperdriveSettings settings = HyperdriveRuntime.Settings;
            return settings != null ? settings.Section(section).GetBool(key, defaultValue) : defaultValue;
        }

        internal static void SetSubsystem(string name, bool on) => Set("SUBSYSTEMS", name, on ? "true" : "false");

        internal static void SetBool(string section, string key, bool on) => Set(section, key, on ? "true" : "false");

        /// <summary>HYPERDRIVE { section { key = value } }, keeping everything else in the file.</summary>
        internal static void Set(string section, string key, string value)
        {
            string path = HyperdriveRuntime.UserConfigPath;
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                ConfigNode root = File.Exists(path) ? ConfigNode.Load(path) : null;
                if (root == null)
                    root = new ConfigNode();
                ConfigNode hd = root.GetNode("HYPERDRIVE") ?? root.AddNode("HYPERDRIVE");
                ConfigNode node = hd.GetNode(section) ?? hd.AddNode(section);
                node.SetValue(key, value, true);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                root.Save(path, "Local overrides of Hyperdrive.cfg for this install (never overwritten by updates or builds).");
                s_written[section + "/" + key] = value;
                Log.Info("Settings: " + section + " " + key + " = " + value + " written to Hyperdrive.user.cfg (next start)");
            }
            catch (Exception e)
            {
                Log.Warn("Settings: could not write Hyperdrive.user.cfg (" + e.Message + ")");
            }
        }
    }
}
