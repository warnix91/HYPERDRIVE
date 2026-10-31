using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Highlighting;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// The part highlighting effect (HighlightingSystem on the flight and editor cameras, "Highlight FX" in the settings)
    /// renders every frame, even with no part highlighted: its command buffer clears a full-screen (multisampled) buffer,
    /// downsamples and blurs it, and OnRenderImage composites the blurred buffer over the image. With nothing highlighted
    /// the buffers stay empty and the composite gives back the image as it was (2.7 ms per frame on the Mac at 2560x1600).
    /// Here, on a frame where no highlighter is highlighted (stock draws only those, FillBuffer), the command buffer is
    /// emptied (it stays attached: the camera's buffers keep their order) and OnRenderImage copies the image as it is.
    /// The frame a highlighter turns on, stock sees the change and rebuilds the buffer before the camera renders (its
    /// UpdateHighlighting reports it), and the composite is used again. Same image, same highlights.
    /// verify = true keeps stock rendering and compares the two results on a few idle frames, pixel by pixel.
    /// </summary>
    internal sealed class HighlightIdle : Subsystem
    {
        private sealed class CameraState
        {
            public HighlightingSystem System;
            public int IdleFrame = -1;
        }

        private static AccessTools.FieldRef<HighlightingSystem, CommandBuffer> s_buffer;
        private static FieldInfo s_highlightersField, s_compField, s_dirty;
        private static HashSet<Highlighter> s_highlighters;
        private static readonly List<CameraState> s_states = new List<CameraState>();
        private static bool s_verify;
        private static int s_verified;
        private static float s_nextVerify;
        private static long s_idleFrames, s_frames;

        public override string Name => "HighlightIdle";
        public override bool YieldOnSharedTargets => true;

        private static bool s_bypass;

        /// <summary>Dev A/B (AutoBench): stock rendering while true; the emptied buffers are rebuilt by stock.</summary>
        internal static bool Bypass
        {
            get => s_bypass;
            set
            {
                if (value && !s_bypass)
                    MarkDirty();
                s_bypass = value;
            }
        }

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_verify = settings.GetBool("verify", false);
        }

        protected internal override void OnEnable()
        {
            MethodInfo preRender = AccessTools.Method(typeof(HighlightingSystem), "OnPreRender");
            MethodInfo renderImage = AccessTools.Method(typeof(HighlightingSystem), "OnRenderImage");
            s_highlightersField = AccessTools.Field(typeof(Highlighter), "highlighters");
            s_compField = AccessTools.Field(typeof(HighlightingSystem), "compMaterial");
            s_dirty = AccessTools.Field(typeof(HighlightingSystem), "isDirty");
            if (preRender == null || renderImage == null || s_highlightersField == null || !s_highlightersField.IsStatic
                || s_highlightersField.FieldType != typeof(HashSet<Highlighter>) || s_compField == null || !s_compField.IsStatic
                || s_dirty == null || s_dirty.FieldType != typeof(bool))
                throw new SubsystemUnavailableException("HighlightingSystem members not found (game version mismatch?)");
            s_buffer = AccessTools.FieldRefAccess<HighlightingSystem, CommandBuffer>("renderBuffer");
            s_verified = 0;
            Patch(preRender, postfix: nameof(PreRenderPostfix));
            Patch(renderImage, prefix: nameof(RenderImagePrefix));
            Log.Info("HighlightIdle: part highlighting skipped on frames with nothing highlighted" + (s_verify ? " (verify mode: stock rendering, results compared)" : ""));
        }

        // Stock is asked to rebuild what was emptied (its isDirty flag), so its buffers are filled and cleared again.
        protected internal override void OnDisable()
        {
            MarkDirty();
            s_states.Clear();
        }

        private static void MarkDirty()
        {
            for (int i = 0; i < s_states.Count; i++)
            {
                s_states[i].IdleFrame = -1;
                if (s_states[i].System != null && s_dirty != null)
                    s_dirty.SetValue(s_states[i].System, true);
            }
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (s_frames > 0)
                Log.Info("HighlightIdle: " + s_idleFrames + " of " + s_frames + " frames rendered without the highlighting passes");
            s_idleFrames = s_frames = 0;
            for (int i = s_states.Count - 1; i >= 0; i--)
                if (s_states[i].System == null)
                    s_states.RemoveAt(i);
        }

        private static bool AnyHighlighted()
        {
            if (s_highlighters == null)
                s_highlighters = s_highlightersField.GetValue(null) as HashSet<Highlighter>;
            if (s_highlighters == null)
                return true;   // unknown: stock rendering
            HashSet<Highlighter>.Enumerator e = s_highlighters.GetEnumerator();
            while (e.MoveNext())
            {
                Highlighter h = e.Current;
                if ((object)h != null && h.highlighted)
                    return true;
            }
            return false;
        }

        private static CameraState Find(HighlightingSystem system)
        {
            for (int i = 0; i < s_states.Count; i++)
                if (ReferenceEquals(s_states[i].System, system))
                    return s_states[i];
            return null;
        }

        // After stock has updated its highlighters (and rebuilt its command buffer when they changed).
        private static void PreRenderPostfix(HighlightingSystem __instance)
        {
            if (s_bypass || __instance.GetType() != typeof(HighlightingSystem))
                return;   // a subclass may render its own way
            CommandBuffer buffer = s_buffer(__instance);
            if (buffer == null)
                return;
            s_frames++;
            if (AnyHighlighted())
                return;
            CameraState state = Find(__instance);
            if (state == null)
            {
                state = new CameraState { System = __instance };
                s_states.Add(state);
            }
            s_idleFrames++;
            state.IdleFrame = Time.frameCount;
            if (!s_verify && buffer.sizeInBytes > 0)
                buffer.Clear();
        }

        private static bool RenderImagePrefix(HighlightingSystem __instance, RenderTexture src, RenderTexture dst)
        {
            CameraState state = Find(__instance);
            if (state == null || state.IdleFrame != Time.frameCount)
                return true;
            if (s_verify)
            {
                if (s_verified < 3 && Time.realtimeSinceStartup >= s_nextVerify)
                    Verify(src);
                return true;
            }
            Graphics.Blit(src, dst);
            return false;
        }

        // Dev check: the stock composite of the (empty) highlighting buffers against a plain copy, read back and compared.
        private static void Verify(RenderTexture src)
        {
            s_nextVerify = Time.realtimeSinceStartup + 2f;
            s_verified++;
            Material comp = s_compField.GetValue(null) as Material;
            if (src == null || comp == null)
                return;
            RenderTexture a = RenderTexture.GetTemporary(src.width, src.height, 0, src.format);
            RenderTexture b = RenderTexture.GetTemporary(src.width, src.height, 0, src.format);
            RenderTexture previous = RenderTexture.active;
            Texture2D read = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(src, a, comp);
                Graphics.Blit(src, b);
                RenderTexture.active = a;
                read.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0, false);
                Color32[] stock = read.GetPixels32();
                RenderTexture.active = b;
                read.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0, false);
                Color32[] copy = read.GetPixels32();
                int differing = 0, worst = 0;
                for (int i = 0; i < stock.Length; i++)
                {
                    int d = Math.Max(Math.Max(Math.Abs(stock[i].r - copy[i].r), Math.Abs(stock[i].g - copy[i].g)),
                                     Math.Max(Math.Abs(stock[i].b - copy[i].b), Math.Abs(stock[i].a - copy[i].a)));
                    if (d > 0)
                    {
                        differing++;
                        if (d > worst)
                            worst = d;
                    }
                }
                Log.Info("HighlightIdle verify: " + src.width + "x" + src.height + " " + src.format + ", " + differing + " pixels differ (max " + worst + "/255) between the stock composite and a plain copy");
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(a);
                RenderTexture.ReleaseTemporary(b);
                UnityEngine.Object.Destroy(read);
            }
        }
    }
}
