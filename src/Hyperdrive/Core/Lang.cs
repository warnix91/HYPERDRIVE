using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Hyperdrive.Core
{
    /// <summary>
    /// Hyperdrive's own texts in the player's language: the language KSP itself uses, read the way KSP reads it
    /// (Localizer.GetLanguageIdFromFile: the last "language = xx" line of buildID.txt in the game folder, written by Steam's
    /// game language setting or by KSP's launcher), directly from the file because the loading screen starts before KSP's
    /// localization is loaded. English when the file or the line is missing, or when it is not one of KSP's languages. Texts come from
    /// GameData/Hyperdrive/Localization/*.cfg (KSP's Localization format, keys "#HD_..."); a text missing in the
    /// player's language falls back to English, then to the key. Placeholders are KSP's: &lt;&lt;1&gt;&gt;, &lt;&lt;2&gt;&gt;...
    /// </summary>
    internal static class Lang
    {
        internal const string English = "en-us";

        // KSP 1.12's languages.
        private static readonly string[] Codes = { "en-us", "fr-fr", "de-de", "es-es", "it-it", "pt-br", "ru", "zh-cn", "ja" };

        private static readonly Dictionary<string, string> s_texts = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> s_english = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool s_loaded;

        /// <summary>The player's language: one of KSP's codes (en-us, fr-fr, de-de, es-es, it-it, pt-br, ru, zh-cn, ja).</summary>
        internal static string Code { get; private set; } = English;

        /// <summary>Decimal comma (fr, de, es, it, pt, ru) or point (en, zh, ja).</summary>
        internal static bool DecimalComma { get; private set; }

        /// <summary>Reads the language and the texts once (main thread, first use).</summary>
        internal static void Ensure()
        {
            if (s_loaded)
                return;
            s_loaded = true;
            Code = Detect();
            DecimalComma = Code == "fr-fr" || Code == "de-de" || Code == "es-es" || Code == "it-it" || Code == "pt-br" || Code == "ru";
            try
            {
                string folder = Path.Combine(HyperdriveRuntime.ModDirectory ?? Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "Hyperdrive"), "Localization");
                if (Directory.Exists(folder))
                    foreach (string file in Directory.GetFiles(folder, "*.cfg"))
                        Read(file);
            }
            catch (Exception e)
            {
                Log.Warn("Lang: texts not read (" + e.Message + "), English keys used");
            }
            Log.Info("Lang: " + Code + " (" + s_texts.Count + " texts, " + s_english.Count + " in English)");
        }

        // As KSP: the last line of buildID.txt containing "language" and one '=', its value; English otherwise.
        private static string Detect()
        {
            string result = null;
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "buildID.txt");
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path))
                    {
                        if (!line.Contains("language") || !line.Contains("="))
                            continue;
                        string[] parts = line.Split('=');
                        if (parts.Length == 2)
                            result = parts[1].Trim();
                    }
            }
            catch (Exception) { }
            return string.IsNullOrEmpty(result) ? English : Match(result);
        }

        private static string Match(string value)
        {
            value = value.ToLowerInvariant().Replace('_', '-');
            foreach (string code in Codes)
                if (value == code)
                    return code;
            foreach (string code in Codes)
                if (value.Length >= 2 && code.StartsWith(value.Substring(0, 2), StringComparison.Ordinal))
                    return code;
            return English;
        }

        private static void Read(string file)
        {
            ConfigNode root = ConfigNode.Load(file);
            if (root == null)
                return;
            foreach (ConfigNode loc in root.GetNodes("Localization"))
            {
                foreach (ConfigNode lang in loc.nodes)
                {
                    if (lang.name != Code && lang.name != English)
                        continue;
                    foreach (ConfigNode.Value v in lang.values)
                    {
                        string text = v.value.Replace("\\n", "\n");
                        if (lang.name == Code)
                            s_texts[v.name] = text;
                        if (lang.name == English)
                            s_english[v.name] = text;
                    }
                }
            }
        }

        /// <summary>The text for <paramref name="key"/> in the player's language (English, then the key, when missing).</summary>
        internal static string T(string key)
        {
            Ensure();
            if (s_texts.TryGetValue(key, out string text))
                return text;
            if (s_english.TryGetValue(key, out text))
                return text;
            return key;
        }

        /// <summary>T with &lt;&lt;1&gt;&gt;, &lt;&lt;2&gt;&gt;... replaced. Allocates: never call it every frame.</summary>
        internal static string F(string key, params object[] args)
        {
            string text = T(key);
            for (int i = 0; i < args.Length; i++)
                text = text.Replace("<<" + (i + 1) + ">>", Convert.ToString(args[i], CultureInfo.InvariantCulture));
            return text;
        }

        /// <summary>A number with the language's decimal separator.</summary>
        internal static string Number(double value, int decimals)
        {
            string s = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
            return DecimalComma ? s.Replace('.', ',') : s;
        }
    }
}
