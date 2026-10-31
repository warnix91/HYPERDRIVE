using System;
using System.Collections.Generic;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// Scene changes after the first load (main menu, space centre, editors, flight, revert...): KSP covers the screen with
    /// LoadingBufferMask (a black camera with its logo) from the scene request until the new scene has settled. While it is
    /// shown, one of the loading screen animations is drawn over it, drawn from the same saved bag as the start screen (the
    /// main menu window's exclusions and choice; every screen once before any comes back, across starts and scene changes,
    /// never the same twice in a row), and fades out when KSP hides its mask. Drawn in OnGUI with
    /// the same effects and GL helpers as the start screen; nothing allocated per frame. Frames frozen by the scene load
    /// itself stay frozen: the animation runs in the frames KSP gives.
    /// </summary>
    internal sealed class SceneLoadScreen : Subsystem
    {
        private static SceneLoadView s_view;
        private static bool s_afterFirstLoad;
        private GameObject _host;
        private bool _capture;

        public override string Name => "SceneLoadScreen";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            // Developer: a screenshot 1 s into each scene change animation, and one of the main menu window, in PluginData.
            _capture = settings.GetBool("captureFrames", false);
        }

        public override string[] IncompatibleAssemblies => new[] { "LoadingScreenManager" };

        protected internal override void OnEnable()
        {
            Patch(AccessTools.Method(typeof(LoadingBufferMask), "Show"), postfix: nameof(ShowPostfix));
            Patch(AccessTools.Method(typeof(LoadingBufferMask), "Hide"), postfix: nameof(HidePostfix));
            _host = new GameObject("Hyperdrive.SceneLoadScreen");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            s_view = _host.AddComponent<SceneLoadView>();
            s_view.Capture = _capture;
            Log.Info("SceneLoadScreen: loading animations for KSP's scene changes after the first load");
        }

        protected internal override void OnDisable()
        {
            s_view = null;
            if (_host != null)
                UnityEngine.Object.Destroy(_host);
            _host = null;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU)
                s_afterFirstLoad = true;
        }

        private static void ShowPostfix()
        {
            if (s_view != null && s_afterFirstLoad && HighLogic.LoadedScene != GameScenes.LOADING)
                s_view.Begin();
        }

        private static void HidePostfix()
        {
            if (s_view != null)
                s_view.End();
        }
    }

    internal sealed class SceneLoadView : MonoBehaviour
    {
        private const float FadeIn = 0.25f, FadeOut = 0.6f;

        private readonly EffectContext _ctx = new EffectContext();
        private LoadingEffect[] _effects;
        private LoadingEffect _effect;
        private Material _blend, _additive;
        private GUIStyle _text;
        private string _caption;
        private int _lastIndex = -1;
        private bool _active, _ending;
        private float _start, _endStart, _overlay, _energy, _lastTime;
        private readonly System.Random _rng = new System.Random(Environment.TickCount ^ 0x51ce);
        internal bool Capture;
        private int _captures;
        private bool _captured;

        private void Awake()
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader != null)
            {
                _blend = MakeMaterial(shader, UnityEngine.Rendering.BlendMode.SrcAlpha, UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _additive = MakeMaterial(shader, UnityEngine.Rendering.BlendMode.One, UnityEngine.Rendering.BlendMode.One);
            }
            _effects = LoadingEffectCatalog.Create();
            _caption = Lang.T("#HD_Loading");
        }

        private static Material MakeMaterial(Shader shader, UnityEngine.Rendering.BlendMode src, UnityEngine.Rendering.BlendMode dst)
        {
            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            return m;
        }

        internal void Begin()
        {
            if (_blend == null || _effects == null || _effects.Length == 0)
                return;
            float now = Time.realtimeSinceStartup;
            if (_active && _ending)
            {
                _ending = false;   // a new request while fading out: same animation, back to full
                return;
            }
            if (_active)
                return;
            int index = Choose(out string how);
            _effect = _effects[index];
            Log.Info("SceneLoadScreen: '" + _effect.Name + "' for the scene change from " + HighLogic.LoadedScene + " (" + how + ")");
            _lastIndex = index;
            _active = true;
            _ending = false;
            _start = now;
            _anim = 0f;
            _lastTime = now;
            _overlay = 0f;
            _energy = 0f;
            _captured = false;
            try
            {
                FillContext(0f);
                _effect.Init(_ctx, new System.Random(_rng.Next()));
            }
            catch (Exception e)
            {
                Log.Warn("SceneLoadScreen: animation '" + _effect.Name + "' failed to start (" + e.Message + ")");
                _active = false;
            }
        }

        internal void End()
        {
            if (!_active || _ending)
                return;
            _ending = true;
            _endStart = Time.realtimeSinceStartup;
        }

        // The start screen's bag (LoadingScreenChoice, saved in PluginData): scene changes and starts share one draw, so
        // every screen plays once before any comes back, whatever the mix of starts and scene changes.
        private int Choose(out string how)
        {
            if (_names == null)
            {
                _names = new string[_effects.Length];
                for (int i = 0; i < _effects.Length; i++)
                    _names[i] = _effects[i].Name;
            }
            string pick = LoadingScreenChoice.Pick(_names, "random", out how, sceneChange: true);
            int index = Array.IndexOf(_names, pick);
            return index >= 0 ? index : (_lastIndex + 1) % _effects.Length;
        }

        private string[] _names;

        // Animation clock advanced by the clamped frame time: a frozen frame pauses the animation, no jump afterwards.
        private float _anim;

        private void FillContext(float dt)
        {
            _anim += dt;
            float t = _anim;
            _ctx.W = Screen.width;
            _ctx.H = Screen.height;
            _ctx.T = t;
            _ctx.Dt = dt;
            _ctx.Energy = _energy;
            _ctx.Progress = 1f - Mathf.Exp(-t / 3f);   // no real progress for a scene load: an easing towards the end
            _ctx.Overlay = _overlay;
            _ctx.Flash = 0f;
            _ctx.Exiting = _ending;
            _ctx.BlendMaterial = _blend;
            _ctx.AdditiveMaterial = _additive;
        }

        private void Update()
        {
            if (!_active)
                return;
            float now = Time.realtimeSinceStartup;
            float dt = Mathf.Min(now - _lastTime, 0.1f);
            _lastTime = now;
            if (_ending)
            {
                _overlay = Mathf.Clamp01(1f - (now - _endStart) / FadeOut);
                _energy = Mathf.MoveTowards(_energy, 0f, dt * 2f);
                if (_overlay <= 0f)
                {
                    _active = false;
                    return;
                }
            }
            else
            {
                _overlay = Mathf.Clamp01((now - _start) / FadeIn);
                _energy = Mathf.MoveTowards(_energy, 1f, dt);
            }
            FillContext(dt);
            if (_ctx.W < 16f || _ctx.H < 16f)
                return;
            _effect.Step(_ctx);
            if (Capture && !_captured && !_ending && now - _start > 1f)
            {
                _captured = true;
                string path = System.IO.Path.Combine(System.IO.Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"),
                    "scene_" + (++_captures) + "_" + _effect.Name + ".png");
                ScreenCapture.CaptureScreenshot(path);
            }
        }

        private void OnGUI()
        {
            if (!_active || _effect == null)
                return;
            GUI.depth = -10000;
            if (Event.current.type != EventType.Repaint)
                return;
            float w = Screen.width, h = Screen.height;
            if (w < 16f || h < 16f)
                return;   // minimized or zero-sized window: nothing to draw
            FillContext(0f);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, 0, h);
            if (_blend.SetPass(0))
            {
                Color bg = _effect.Background;
                GL.Begin(GL.QUADS);
                Gfx.Rect(0f, 0f, w, h, new Color(bg.r, bg.g, bg.b, _overlay));
                GL.End();
            }
            Gfx.Boil = _effect.Boil * h / 1080f;
            Gfx.BoilStep = (int)(_ctx.T * 8f);
            _effect.Draw(_ctx);
            Gfx.Boil = 0f;
            Finishes.Apply(_ctx, _effect.Finish);
            GL.PopMatrix();

            if (_text == null)
            {
                _text = new GUIStyle(HighLogic.Skin != null ? HighLogic.Skin.label : GUI.skin.label) { fontSize = 22 };
                _text.normal.textColor = Color.white;
            }
            _ctx.Text = _text;
            Color old = GUI.color;
            _effect.DrawGui(_ctx);
            _ctx.Label(w * 0.04f, h * 0.08f, _ctx.Exiting ? _effect.ExitText : _caption,
                       new Color(1f, 1f, 1f, 0.85f), h * 0.026f);
            GUI.color = old;
        }

        private void OnDestroy()
        {
            if (_blend != null) Destroy(_blend);
            if (_additive != null) Destroy(_additive);
        }
    }
}
