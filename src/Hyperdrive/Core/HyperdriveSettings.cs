using System;
using System.Globalization;
using System.IO;

namespace Hyperdrive.Core
{
    /// <summary>
    /// Reads GameData/Hyperdrive/Hyperdrive.cfg (shipped defaults) and the optional
    /// GameData/Hyperdrive/PluginData/Hyperdrive.user.cfg (local overrides, never overwritten by updates).
    /// Both files contain a root HYPERDRIVE node; values from the user file win.
    /// Loaded at KSPAddon.Startup.Instantly, before GameDatabase, so ModuleManager patches do not apply here.
    /// </summary>
    internal sealed class HyperdriveSettings
    {
        public const string RootNodeName = "HYPERDRIVE";
        public const string SubsystemsNodeName = "SUBSYSTEMS";

        private readonly ConfigNode _defaults;
        private readonly ConfigNode _user;

        public string DefaultsPath { get; }
        public string UserPath { get; }
        public bool DefaultsFound => _defaults != null;
        public bool UserFound => _user != null;

        private HyperdriveSettings(string defaultsPath, ConfigNode defaults, string userPath, ConfigNode user)
        {
            DefaultsPath = defaultsPath;
            UserPath = userPath;
            _defaults = defaults;
            _user = user;
        }

        public static HyperdriveSettings Load(string modDirectory)
        {
            string defaultsPath = Path.Combine(modDirectory, "Hyperdrive.cfg");
            string userPath = Path.Combine(Path.Combine(modDirectory, "PluginData"), "Hyperdrive.user.cfg");
            return new HyperdriveSettings(defaultsPath, LoadRoot(defaultsPath), userPath, LoadRoot(userPath));
        }

        private static ConfigNode LoadRoot(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                ConfigNode file = ConfigNode.Load(path);
                ConfigNode root = file?.GetNode(RootNodeName);
                if (root == null)
                    Log.Warn("Settings file has no " + RootNodeName + " node, ignored: " + path);
                return root;
            }
            catch (Exception e)
            {
                Log.Exception("Failed to read settings file " + path, e);
                return null;
            }
        }

        public SettingsSection Global => new SettingsSection(_defaults, _user);

        public SettingsSection Section(string name) => new SettingsSection(_defaults?.GetNode(name), _user?.GetNode(name));

        public bool IsSubsystemEnabled(string name, bool defaultValue) => Section(SubsystemsNodeName).GetBool(name, defaultValue);
    }

    /// <summary>A pair of nodes (shipped defaults, user overrides) with typed, culture-invariant getters.</summary>
    internal readonly struct SettingsSection
    {
        private readonly ConfigNode _defaults;
        private readonly ConfigNode _user;

        public SettingsSection(ConfigNode defaults, ConfigNode user)
        {
            _defaults = defaults;
            _user = user;
        }

        private string Raw(string key)
        {
            string value = _user?.GetValue(key);
            return value ?? _defaults?.GetValue(key);
        }

        public string GetString(string key, string defaultValue) => Raw(key)?.Trim() ?? defaultValue;

        /// <summary>Child nodes named <paramref name="name"/> from the user file first, then from the shipped defaults.</summary>
        public ConfigNode[] GetNodes(string name)
        {
            ConfigNode[] user = _user?.GetNodes(name) ?? new ConfigNode[0];
            ConfigNode[] defaults = _defaults?.GetNodes(name) ?? new ConfigNode[0];
            var all = new ConfigNode[user.Length + defaults.Length];
            user.CopyTo(all, 0);
            defaults.CopyTo(all, user.Length);
            return all;
        }

        public bool GetBool(string key, bool defaultValue)
        {
            string raw = Raw(key);
            return raw != null && bool.TryParse(raw.Trim(), out bool value) ? value : defaultValue;
        }

        public int GetInt(string key, int defaultValue)
        {
            string raw = Raw(key);
            return raw != null && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : defaultValue;
        }

        public float GetFloat(string key, float defaultValue)
        {
            string raw = Raw(key);
            return raw != null && float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : defaultValue;
        }
    }
}
