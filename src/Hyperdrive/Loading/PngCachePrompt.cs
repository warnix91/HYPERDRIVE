using System;
using System.Collections.Generic;
using System.IO;
using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Optional (FastLoader pngCache = ask; by default the cache is simply on): as KSPCommunityFixes does for its own
    /// cache, the player is asked once, at the first
    /// load that has PNG textures to decode, with what it would save on this install (decoding time) and cost (disk space),
    /// estimated from the PNGs' sizes (measured on the Mac: 16 ms of load and 0.95 MB of cache per megapixel decoded).
    /// The answer goes to Hyperdrive.user.cfg (FastLoader pngCache)
    /// and can be changed in KSP's settings (Esc) or the Hyperdrive panel. Drawn with KSP's own window skin above the
    /// loading screen; the texture loading waits for the answer, as KSPCF's does. Never asked during a developer benchmark.
    /// </summary>
    internal sealed class PngCachePrompt : MonoBehaviour
    {
        private const double SecondsPerMegapixel = 0.0165, MegabytesPerMegapixel = 0.95;
        internal static bool? Answer { get; private set; }

        /// <summary>The question is on screen (the loading screen's titles step aside).</summary>
        internal static bool Shown { get; private set; }
        private string _title, _text, _yes, _no;
        internal bool Test;   // developer: screenshot, then yes
        private float _shownAt = -1f;
        private Rect _window;
        private float _scale = 1f;

        /// <summary>Megapixels of the PNG files (their headers), for the estimate.</summary>
        internal static double Megapixels(List<UrlDir.UrlFile> pngs)
        {
            double pixels = 0;
            var header = new byte[24];
            foreach (UrlDir.UrlFile f in pngs)
            {
                try
                {
                    using (var s = new FileStream(f.fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64))
                    {
                        if (s.Read(header, 0, 24) < 24 || header[0] != 0x89 || header[1] != (byte)'P')
                            continue;
                        long w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                        long h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                        pixels += (double)w * h;
                    }
                }
                catch (Exception) { }
            }
            return pixels / 1e6;
        }

        internal static PngCachePrompt Show(double megapixels)
        {
            Answer = null;
            Shown = true;
            var go = new GameObject("Hyperdrive.PngCachePrompt");
            DontDestroyOnLoad(go);
            var view = go.AddComponent<PngCachePrompt>();
            int seconds = Math.Max(1, (int)Math.Round(megapixels * SecondsPerMegapixel));
            int megabytes = Math.Max(1, (int)Math.Round(megapixels * MegabytesPerMegapixel));
            Lang.Ensure();
            view._title = "Hyperdrive";
            view._text = Lang.F("#HD_Png_Text", "<color=#ff9933>" + Lang.F("#HD_Png_Seconds", seconds) + "</color>",
                                "<color=#ff9933>" + megabytes + " " + Lang.T("#HD_Png_MB") + "</color>");
            view._yes = Lang.T("#HD_Png_Yes");
            view._no = Lang.T("#HD_Png_No");
            Log.Info("PngCache: player asked (" + megapixels.ToString("F0") + " megapixels of PNG: about " + seconds + " s saved, " + megabytes + " MB)");
            return view;
        }

        private void Update()
        {
            if (!Test)
                return;
            if (_shownAt < 0f)
                _shownAt = Time.realtimeSinceStartup;
            float t = Time.realtimeSinceStartup - _shownAt;
            if (t > 1.5f && t < 1.6f)
                ScreenCapture.CaptureScreenshot(Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), "png_prompt.png"));
            if (t > 3f)
                Decide(true);
        }

        private void OnGUI()
        {
            GUI.depth = -30000;   // over the loading screens (-10000) and the load problems notice (-20000)
            if (HighLogic.Skin != null)
                GUI.skin = HighLogic.Skin;
            _scale = Mathf.Max(1f, Screen.height / 1080f) * Mathf.Clamp(GameSettings.UI_SCALE, 0.5f, 2.5f);   // as KSP's own windows
            float w = Mathf.Min(560f * _scale, Screen.width - 40f), h = Mathf.Min(430f * _scale, Screen.height - 40f);
            if (_window.width < 1f)
                _window = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            // Drawn in place (a window would be drawn after every other OnGUI, under the loading screen's own drawing).
            GUI.Box(_window, _title, GUI.skin.window);
            GUILayout.BeginArea(new Rect(_window.x + 16f * _scale, _window.y + 34f * _scale, _window.width - 32f * _scale, _window.height - 50f * _scale));
            Draw();
            GUILayout.EndArea();
        }

        private void Draw()
        {
            var text = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = Mathf.RoundToInt(16f * _scale) };
            var button = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(16f * _scale) };
            GUILayout.Label(_text, text);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_yes, button, GUILayout.Height(34f * _scale)))
                Decide(true);
            if (GUILayout.Button(_no, button, GUILayout.Height(34f * _scale)))
                Decide(false);
        }

        private void Decide(bool yes)
        {
            Answer = yes;
            Shown = false;
            UserConfig.Set("FastLoader", "pngCache", yes ? "true" : "false");
            Log.Info("PngCache: the player chose " + (yes ? "to keep" : "not to keep") + " the PNG cache");
            Destroy(gameObject);
        }
    }
}
