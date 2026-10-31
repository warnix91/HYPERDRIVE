using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using Hyperdrive.Rendering;
using Hyperdrive.Throttle;
using Hyperdrive.UI.Panel;
using KSP.UI.Screens;
using UnityEngine;

namespace Hyperdrive.UI
{
    /// <summary>
    /// Hyperdrive's interface, in the player's language and in Hyperdrive's look (UI.HdSkin):
    /// - a Hyperdrive section in KSP's settings (pause menu, Settings), as KSPCommunityFixes does: the main switches, applied
    ///   at the next start, and a button for the panel (KspSettingsSection);
    /// - the panel: Features (every subsystem, its state and why, a switch), Performance (a 10 s measurement of every mod's
    ///   per-frame callbacks and the game's) and Loading screens (random or always the same, which to leave out);
    /// - opened from the logo in KSP's toolbar (space centre, flight, editors, tracking station), the logo at the top right
    ///   of the main menu (where the toolbar sits in the other scenes), the settings button, or Alt+F10.
    /// </summary>
    internal sealed class HyperdriveWindow : Subsystem
    {
        private HyperdriveWindowView _view;
        private ApplicationLauncherButton _button;
        private EventVoid.OnEvent _onLauncherReady, _onLauncherDestroyed;
        private bool _capture, _settingsSection = true;

        public override string Name => "HyperdriveWindow";
        public override bool ReadOnly => true;

        internal static HyperdriveWindow Instance { get; private set; }

        private static Texture2D s_icon;

        /// <summary>The mod's icon, for KSP's toolbar and the main menu.</summary>
        internal static Texture2D Icon => s_icon != null ? s_icon : (s_icon = UiArt.Icon(64));

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _capture = settings.GetBool("captureAtMenu", false);   // developer: screenshots of the panel at the main menu
            _settingsSection = settings.GetBool("settingsSection", true);
        }

        protected internal override void OnEnable()
        {
            var host = new GameObject("Hyperdrive.Window");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _view = host.AddComponent<HyperdriveWindowView>();
            _view.CaptureAtMenu = _capture;
            _onLauncherReady = AddButton;
            _onLauncherDestroyed = RemoveButton;
            GameEvents.onGUIApplicationLauncherReady.Add(_onLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(_onLauncherDestroyed);
            Instance = this;
            if (_settingsSection)
            {
                var draw = AccessTools.Method(typeof(GameplaySettingsScreen), nameof(GameplaySettingsScreen.DrawMiniSettings));
                var apply = AccessTools.Method(typeof(GameplaySettingsScreen), nameof(GameplaySettingsScreen.ApplySettings));
                if (draw != null && apply != null && draw.ReturnType == typeof(DialogGUIBase[]))
                {
                    Patch(draw, postfix: nameof(DrawMiniSettingsPostfix));
                    Patch(apply, postfix: nameof(ApplySettingsPostfix));
                }
                else
                    Log.Info("HyperdriveWindow: KSP's settings screen not found, no Hyperdrive section there (the panel stays)");
            }
        }

        protected internal override void OnDisable()
        {
            if (_onLauncherReady != null)
                GameEvents.onGUIApplicationLauncherReady.Remove(_onLauncherReady);
            if (_onLauncherDestroyed != null)
                GameEvents.onGUIApplicationLauncherDestroyed.Remove(_onLauncherDestroyed);
            RemoveButton();
            if (_view != null)
                UnityEngine.Object.Destroy(_view.gameObject);
            _view = null;
            Instance = null;
        }

        // KSP's in-game settings (MiniSettings): the Hyperdrive section after the gameplay settings; kept on Apply/Accept.
        private static void DrawMiniSettingsPostfix(ref DialogGUIBase[] __result) => KspSettingsSection.Append(ref __result);

        private static void ApplySettingsPostfix() => KspSettingsSection.Apply();

        /// <summary>Opens the panel (from KSP's settings), on a given tab.</summary>
        internal void OpenPanel(int tab = 0) => _view?.Open(true, tab);

        private void AddButton()
        {
            if (_button != null || ApplicationLauncher.Instance == null || _view == null)
                return;
            const ApplicationLauncher.AppScenes scenes = ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT
                | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH
                | ApplicationLauncher.AppScenes.TRACKSTATION;
            _button = ApplicationLauncher.Instance.AddModApplication(() => _view.Open(true), () => _view.Open(false), null, null, null, null, scenes, Icon);
            _view.Button = _button;
        }

        private void RemoveButton()
        {
            if (_button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(_button);
            _button = null;
            if (_view != null)
                _view.Button = null;
        }
    }

    internal sealed class HyperdriveWindowView : MonoBehaviour
    {
        private const string LockId = "Hyperdrive_panel";
        internal ApplicationLauncherButton Button;
        internal bool CaptureAtMenu;
        private float _menuSince = -1f;
        private int _captureStep;
        private SettingsPanel _panel;
        private int _page, _perfVersion = -1;
        private bool _locked, _rebuild;
        private string _menuTip;
        private readonly GUIContent _logoContent = new GUIContent();

        private void Start()
        {
            _menuTip = Lang.T("#HD_Win_Button");
            _logoContent.tooltip = _menuTip;
        }

        internal bool IsOpen => _panel != null && _panel.Visible;

        internal void Open(bool open, int page = -1)
        {
            if (page >= 0)
                _page = page;
            if (open)
            {
                if (_panel == null)
                    Build();
                _panel.Page = _page;
                _panel.Visible = true;
            }
            else if (_panel != null)
            {
                _panel.Visible = false;
                Unlock();
            }
            if (Button != null)
            {
                if (open) Button.SetTrue(false);
                else Button.SetFalse(false);
            }
        }

        private void Build()
        {
            var hooks = new PanelModel.Hooks
            {
                Close = () => Open(false),
                Changed = () => { },
                Rebuild = () => _rebuild = true,
            };
            Font font = HighLogic.Skin != null ? HighLogic.Skin.font : null;
            float scale = Mathf.Clamp(GameSettings.UI_SCALE, 0.5f, 2.5f);
            _panel = new SettingsPanel(PanelModel.Build(hooks), font, null, scale);
            _perfVersion = ModProfiler.LastVersion;
        }

        // A new look, or a new measurement on the performance page: the window is built again on the same page.
        private void RebuildNow()
        {
            bool visible = _panel != null && _panel.Visible;
            if (_panel != null)
            {
                _page = _panel.Page;
                _panel.Dispose();
                _panel = null;
            }
            Unlock();
            Build();
            _panel.Page = _page;
            _panel.Visible = visible;
        }

        private void Update()
        {
            CaptureSteps();
            if (Input.GetKeyDown(KeyCode.F10) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
                Open(!IsOpen);
            if (_panel == null)
                return;
            try
            {
                if (_rebuild || (_panel.Visible && ModProfiler.LastVersion != _perfVersion))
                {
                    _rebuild = false;
                    RebuildNow();
                }
                if (_panel.Visible && Input.GetKeyDown(KeyCode.Escape))
                {
                    Open(false);
                    return;
                }
                _panel.SetScale(Mathf.Clamp(GameSettings.UI_SCALE, 0.5f, 2.5f));
                _panel.Tick(Time.unscaledDeltaTime);
                _page = _panel.Page;
                bool over = _panel.Visible && _panel.Contains(Input.mousePosition);
                if (!over && Input.GetMouseButtonDown(0))
                    _panel.ReleaseFocus();
                bool interacting = over || _panel.HasKeyboardFocus;
                if (interacting && !_locked)
                {
                    InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.CAMERACONTROLS | ControlTypes.KSC_FACILITIES | ControlTypes.EDITOR_UI, LockId);
                    _locked = true;
                }
                else if (!interacting && _locked)
                    Unlock();
            }
            catch (Exception e)
            {
                Log.Warn("HyperdriveWindow: panel closed after an error (" + e.Message + ")");
                if (_panel != null) { _panel.ReleaseFocus(); _panel.Visible = false; }
                Unlock();
            }
        }

        private void Unlock()
        {
            if (_locked)
            {
                InputLockManager.RemoveControlLock(LockId);
                _locked = false;
            }
        }

        private void OnDestroy()
        {
            Unlock();
            if (_panel != null)
            {
                _panel.Dispose();
                _panel = null;
            }
        }

        private void CaptureSteps()
        {
            if (!CaptureAtMenu)
                return;
            string dir = Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData");
            GameScenes scene = HighLogic.LoadedScene;
            if (scene == GameScenes.EDITOR && _captureStep < 10)
            {
                if (_captureStep < 6) { Open(false); _captureStep = 6; }
                if (_captureStep == 6) { _menuSince = Time.realtimeSinceStartup; _captureStep = 7; }
                float t = Time.realtimeSinceStartup - _menuSince;
                if (_captureStep == 7 && t > 2.5f) { Open(true, 0); _captureStep = 8; }
                else if (_captureStep == 8 && t > 3.5f) { ScreenCapture.CaptureScreenshot(Path.Combine(dir, "window_general.png")); _captureStep = 9; }
                else if (_captureStep == 9 && t > 4.0f) { _panel.Page = 1; _captureStep = 10; }
            }
            else if (scene == GameScenes.EDITOR && _captureStep >= 10 && _captureStep < 16)
            {
                float t = Time.realtimeSinceStartup - _menuSince;
                if (_captureStep == 10 && t > 5.0f) { ScreenCapture.CaptureScreenshot(Path.Combine(dir, "window_optimizations.png")); _captureStep = 11; }
                else if (_captureStep == 11 && t > 5.5f) { _panel.Page = 3; _captureStep = 12; }
                else if (_captureStep == 12 && t > 6.5f) { ScreenCapture.CaptureScreenshot(Path.Combine(dir, "window_screens.png")); _captureStep = 13; }
                else if (_captureStep == 13 && t > 7.0f) { Open(false); _captureStep = 16; }
            }
        }

        private void OnGUI()
        {
            if (HighLogic.LoadedScene != GameScenes.MAINMENU)
                return;
            // Main menu: the mod's icon at the top right, where KSP's toolbar sits in the other scenes.
            float size = Mathf.Clamp(Screen.height * 0.034f, 32f, 52f);
            var r = new Rect(Screen.width - size - 14f, 14f, size, size);
            bool hover = r.Contains(Event.current.mousePosition);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, hover || IsOpen ? 1f : 0.82f);
            GUI.DrawTexture(r, HyperdriveWindow.Icon);
            GUI.color = old;
            if (GUI.Button(r, _logoContent, GUIStyle.none))
                Open(!IsOpen);
        }
    }
}
