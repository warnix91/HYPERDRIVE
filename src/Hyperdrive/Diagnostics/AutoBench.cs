using System;
using System.IO;
using Hyperdrive.Core;
using Hyperdrive.Memory;
using Hyperdrive.Patching;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.Profiling;

namespace Hyperdrive.Diagnostics
{
    /// <summary>
    /// Developer benchmark, inert unless GameData/Hyperdrive/PluginData/autobench.cfg exists.
    /// From the main menu it creates (or reuses) a dedicated sandbox save "HyperdriveBench" (never touches other saves),
    /// launches a craft from Ships/VAB with default crew, throttles up, stages at a fixed interval, pauses once,
    /// logs PerfHUD-style lines and a final report, then optionally quits. Same scenario on every machine.
    /// With cycles = N, the measured flight comes after N short flights, alternately ended by a revert to launch and by a
    /// return to the space centre followed by a new launch: the scene changes of a play session.
    /// </summary>
    internal sealed class AutoBench : Subsystem
    {
        public const string SaveName = "HyperdriveBench";
        private const string FlagUrl = "Squad/Flags/default";

        private enum Phase { WaitMainMenu, StartingGame, WaitSpaceCenter, Launching, Flying, Editing, Done }

        private string _craft = "Kerbal X";
        private float _flightSeconds = 120f;
        private float _stageIntervalSeconds = 10f;
        private float _firstStageDelaySeconds = 5f;
        private float _pauseAtSeconds = 60f;
        private float _pauseDurationSeconds = 4f;
        private bool _quitWhenDone = true;
        // Editor mode (editor = VAB or SPH): the part list searches are typed one after the other, each one's worst frame logged.
        private string _editor = "";
        private string[] _editorSearches = new string[0];
        private float _editorStep = 3f, _editorWarm = 4f;
        private int _editorIndex = -1;
        private float _editorNext, _editorWorstMs;
        private int _editorSlowFrames, _editorGcStart;
        private readonly System.Text.StringBuilder _editorReport = new System.Text.StringBuilder();
        private int _cycles;
        private float _cycleSeconds = 8f;
        private int _cyclesDone;
        // Explosions: from explodeAtSeconds, explodeCount parts of the active vessel blow up, explodeBurst at a time,
        // every explodeIntervalSeconds; the worst frame of the second after each is logged.
        private float _explodeAt;
        private float _highlightAt;
        // Timed windows in flight: map view (e.g. after a crash, many debris orbits) and on-rails time warp; each window's
        // frames (after a 1 s settle) are reported against the same length of frames just before it.
        private float _mapAt, _mapSeconds = 6f, _warpAt, _warpSeconds = 8f;
        private int _warpIndex = 4, _mapStep, _warpStep;
        private float _winStart, _winEnd, _refStart;
        private string _winName;
        private double _winSum, _refSum;
        private int _winFrames, _refFrames;
        private float _winWorst;
        // In-flight A/B: a subsystem's effect switched on and off every few seconds, frame times summed per state (same
        // flight, same heat: a fanless Mac slows down over a run, so separate runs compare badly).
        private string _abToggle;
        private float _abSeconds = 3f, _abNext;
        private bool _abOn = true;
        private readonly double[] _abSum = new double[2];
        private readonly int[] _abFrames = new int[2];
        private int _highlightStep;
        private int _explodeCount, _explodeBurst = 1, _exploded;
        private float _explodeInterval = 1f, _nextExplosion, _watchUntil, _watchWorstMs;
        private int _watchLongFrames, _watchIndex;
        // Crash: at crashAtSeconds the active vessel is thrown at the ground at crashSpeed m/s; the frames of the next
        // crashWatchSeconds are summed up (worst, over 50 / 100 ms, time spent in frames over 50 ms).
        private float _crashHeight = 200f;
        private float _crashAt, _crashSpeed = 0f, _crashWatchSeconds = 6f, _crashUntil = -1f, _crashWorstMs, _crashFrozenMs;
        private int _crashOver50, _crashOver100, _crashFrames, _partsBeforeCrash;
        private bool _crashed;

        private Phase _phase = Phase.WaitMainMenu;
        private bool _menuOnly;
        private float _phaseTime;
        private float _sceneRequested = -1f;
        private float _flightStart;
        private double _flightStartUT;
        private float _nextStage;
        private bool _pausedOnce, _pauseActive, _earlyThermalLogged;
        private float _pauseEnd;
        private int _stagesFired;
        private long _gcAtFlightStart;
        private float _worstFrameMs;
        private int _frames;
        private float _frameTimeSum;

        public override string Name => "AutoBench";

        private static string TriggerPath => Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), "autobench.cfg");

        protected internal override void OnEnable()
        {
            if (!File.Exists(TriggerPath))
                throw new SubsystemUnavailableException("idle, no PluginData/autobench.cfg");

            ConfigNode root = ConfigNode.Load(TriggerPath)?.GetNode("AUTOBENCH");
            if (root != null)
            {
                var s = new SettingsSection(root, null);
                _craft = s.GetString("craft", _craft);
                _flightSeconds = Mathf.Clamp(s.GetFloat("flightSeconds", _flightSeconds), 10f, 3600f);
                _stageIntervalSeconds = Mathf.Clamp(s.GetFloat("stageIntervalSeconds", _stageIntervalSeconds), 1f, 600f);
                _firstStageDelaySeconds = Mathf.Clamp(s.GetFloat("firstStageDelaySeconds", _firstStageDelaySeconds), 0f, 60f);
                _pauseAtSeconds = s.GetFloat("pauseAtSeconds", _pauseAtSeconds);
                _pauseDurationSeconds = Mathf.Clamp(s.GetFloat("pauseDurationSeconds", _pauseDurationSeconds), 0.5f, 60f);
                _quitWhenDone = s.GetBool("quitWhenDone", _quitWhenDone);
                // Load time only: stop at the main menu (no game, no save, no craft), e.g. on a player's install.
                _menuOnly = s.GetBool("menuOnly", false);
                _cycles = Mathf.Clamp((int)s.GetFloat("cycles", 0f), 0, 100);
                _cycleSeconds = Mathf.Clamp(s.GetFloat("cycleSeconds", _cycleSeconds), 3f, 600f);
                _crashAt = s.GetFloat("crashAtSeconds", 0f);
                _crashSpeed = Mathf.Clamp(s.GetFloat("crashSpeed", _crashSpeed), 0f, 2000f);
                _crashHeight = Mathf.Clamp(s.GetFloat("crashHeight", _crashHeight), 0f, 20000f);
                _crashWatchSeconds = Mathf.Clamp(s.GetFloat("crashWatchSeconds", _crashWatchSeconds), 1f, 60f);
                _explodeAt = s.GetFloat("explodeAtSeconds", 0f);
                _highlightAt = s.GetFloat("highlightAtSeconds", 0f);
                _mapAt = s.GetFloat("mapViewAtSeconds", 0f);
                _mapSeconds = Mathf.Clamp(s.GetFloat("mapViewSeconds", _mapSeconds), 2f, 120f);
                _warpAt = s.GetFloat("warpAtSeconds", 0f);
                _warpSeconds = Mathf.Clamp(s.GetFloat("warpSeconds", _warpSeconds), 2f, 120f);
                _warpIndex = Mathf.Clamp((int)s.GetFloat("warpRateIndex", _warpIndex), 1, 7);
                _abToggle = s.GetString("abToggle", null);
                _abSeconds = Mathf.Clamp(s.GetFloat("abSeconds", _abSeconds), 1f, 60f);
                _explodeCount = Mathf.Clamp((int)s.GetFloat("explodeCount", 0f), 0, 1000);
                _explodeBurst = Mathf.Clamp((int)s.GetFloat("explodeBurst", 1f), 1, 100);
                _explodeInterval = Mathf.Clamp(s.GetFloat("explodeIntervalSeconds", _explodeInterval), 0.1f, 60f);
                _editor = s.GetString("editor", "").Trim().ToUpperInvariant();
                _editorSearches = s.GetString("editorSearches", "a;fuel;engine;tank;;struct;").Split(';');
                _editorStep = Mathf.Clamp(s.GetFloat("editorStepSeconds", _editorStep), 0.5f, 60f);
                _editorWarm = Mathf.Clamp(s.GetFloat("editorWarmSeconds", _editorWarm), 0f, 600f);
            }

            if (!File.Exists(CraftPath))
                throw new InvalidOperationException("craft not found: " + CraftPath);

            Log.Warn("AutoBench ARMED: craft '" + _craft + "', " + _flightSeconds + " s of flight, save '" + SaveName
                     + "'. Delete PluginData/autobench.cfg to disable.");
        }

        private string CraftPath => Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "Ships"), "VAB"), _craft + ".craft");

        protected internal override void OnSceneReady(GameScenes scene)
        {
            _phaseTime = Time.unscaledTime;
            if (_sceneRequested >= 0f)
            {
                Log.Info("AutoBench: scene " + scene + " ready " + (Time.realtimeSinceStartup - _sceneRequested).ToString("F2") + " s after the request");
                _sceneRequested = -1f;
            }
            switch (scene)
            {
                case GameScenes.MAINMENU when _phase == Phase.WaitMainMenu:
                    _phase = Phase.StartingGame;
                    break;
                case GameScenes.SPACECENTER when _phase == Phase.WaitSpaceCenter:
                    _phase = Phase.Launching;
                    break;
                case GameScenes.EDITOR when _phase == Phase.Launching && _editor.Length > 0:
                    _phase = Phase.Editing;
                    _editorIndex = -1;
                    _editorNext = Time.unscaledTime + _editorWarm;
                    break;
                case GameScenes.FLIGHT when _phase == Phase.Launching:
                    _phase = Phase.Flying;
                    _flightStart = Time.unscaledTime;
                    _nextStage = _flightStart + _firstStageDelaySeconds;
                    _gcAtFlightStart = GC.CollectionCount(0);
                    _flightStartUT = Planetarium.GetUniversalTime();
                    _frames = 0;
                    _frameTimeSum = 0f;
                    _worstFrameMs = 0f;
                    _stagesFired = 0;
                    _pausedOnce = _pauseActive = _earlyThermalLogged = false;
                    _exploded = 0;
                    _highlightStep = 0;
                    _mapStep = _warpStep = 0;
                    _winName = null;
                    _refStart = 0f;
                    _abNext = _flightStart + 3f + _abSeconds;
                    _abOn = true;
                    _abSum[0] = _abSum[1] = 0;
                    _abFrames[0] = _abFrames[1] = 0;
                    SetAb(true);
                    _nextExplosion = _explodeAt > 0f ? _flightStart + _explodeAt : float.MaxValue;
                    _watchUntil = -1f;
                    _crashed = false;
                    _crashUntil = -1f;
                    Log.Info("AutoBench: in flight with '" + FlightGlobals.ActiveVessel?.vesselName + "'"
                             + (_cyclesDone < _cycles ? " (cycle " + (_cyclesDone + 1) + " of " + _cycles + ")" : ""));
                    break;
            }
        }

        protected internal override void OnUpdate()
        {
            float now = Time.unscaledTime;
            switch (_phase)
            {
                case Phase.StartingGame:
                    if (_menuOnly)
                    {
                        // A few seconds at the menu for the load reports, then done: nothing is started or saved.
                        if (now - _phaseTime >= 5f)
                        {
                            _phase = Phase.Done;
                            Log.Info("AutoBench REPORT: menu only, main menu reached (no game started, nothing saved)");
                            Log.Info("AutoBench DONE");
                            if (_quitWhenDone)
                                Application.Quit();
                        }
                        break;
                    }
                    if (now - _phaseTime >= 3f)
                        StartGame();
                    break;
                case Phase.Launching:
                    if (now - _phaseTime >= 3f && HighLogic.LoadedScene == GameScenes.SPACECENTER)
                    {
                        if (_editor.Length > 0)
                            OpenEditor();
                        else
                            Launch();
                    }
                    break;
                case Phase.Editing:
                    Edit(now);
                    break;
                case Phase.Flying:
                    Fly(now);
                    break;
            }
        }

        private void StartGame()
        {
            _phase = Phase.WaitSpaceCenter;
            Game game = GamePersistence.CreateNewGame(SaveName, Game.Modes.SANDBOX, new GameParameters(), FlagUrl, GameScenes.SPACECENTER, EditorFacility.VAB);
            HighLogic.CurrentGame = game;
            HighLogic.SaveFolder = SaveName;
            GamePersistence.SaveGame(game, "persistent", SaveName, SaveMode.OVERWRITE);
            Log.Info("AutoBench: starting sandbox save '" + SaveName + "'");
            _sceneRequested = Time.realtimeSinceStartup;
            game.startScene = GameScenes.SPACECENTER;
            game.Start();
        }

        private void OpenEditor()
        {
            _phaseTime = float.MaxValue;   // wait for the editor scene
            _sceneRequested = Time.realtimeSinceStartup;
            Log.Info("AutoBench: opening the " + _editor);
            EditorDriver.StartEditor(_editor == "SPH" ? EditorFacility.SPH : EditorFacility.VAB);
        }

        // One search every editorStepSeconds, typed into the part list's search field as a player would (the field's own
        // change event runs the stock search); the worst frame and the frames over 50 ms of each step are logged.
        private void Edit(float now)
        {
            if (_editorIndex >= 0)
            {
                float ms = Time.unscaledDeltaTime * 1000f;
                if (ms > _editorWorstMs)
                    _editorWorstMs = ms;
                if (ms > 50f)
                    _editorSlowFrames++;
            }
            if (now < _editorNext)
                return;
            if (_editorIndex >= 0)
            {
                string term = _editorSearches[_editorIndex];
                _editorReport.Append(" | '").Append(term).Append("' worst ").Append(_editorWorstMs.ToString("F0")).Append(" ms, ")
                    .Append(_editorSlowFrames).Append(" > 50 ms, GC ").Append(GC.CollectionCount(0) - _editorGcStart);
            }
            _editorIndex++;
            _editorWorstMs = 0f;
            _editorSlowFrames = 0;
            _editorGcStart = GC.CollectionCount(0);
            _editorNext = now + _editorStep;
            if (_editorIndex >= _editorSearches.Length)
            {
                _phase = Phase.Done;
                Log.Info("AutoBench EDITOR " + _editor + ":" + _editorReport + " | heap " + (Profiler.GetMonoHeapSizeLong() / (1024 * 1024)) + " MB");
                Log.Info("AutoBench DONE");
                if (_quitWhenDone)
                    Application.Quit();
                return;
            }
            var field = HarmonyLib.AccessTools.Field(typeof(KSP.UI.Screens.BasePartCategorizer), "searchField");
            object input = KSP.UI.Screens.PartCategorizer.Instance != null && field != null ? field.GetValue(KSP.UI.Screens.PartCategorizer.Instance) : null;
            if (_editorIndex == 0)
                Diagnostics.PSystemTrace.BeginFlightWindow("editor");
            System.Reflection.PropertyInfo text = input?.GetType().GetProperty("text");
            if (text != null)
                text.SetValue(input, _editorSearches[_editorIndex], null);   // fires the field's change event, as typing does
            else
                Log.Warn("AutoBench: part list search field not found");
        }

        private void Launch()
        {
            _phase = Phase.WaitSpaceCenter; // guard against double launch until FLIGHT is ready
            ConfigNode craftNode = ConfigNode.Load(CraftPath);
            VesselCrewManifest crew = HighLogic.CurrentGame.CrewRoster.DefaultCrewForVessel(craftNode, null, true, false);
            Log.Info("AutoBench: launching '" + _craft + "'");
            _phase = Phase.Launching;
            _sceneRequested = Time.realtimeSinceStartup;
            Diagnostics.PSystemTrace.BeginFlightWindow("launch");
            FlightDriver.StartWithNewLaunch(CraftPath, FlagUrl, "LaunchPad", crew);
            _phaseTime = float.MaxValue; // wait for FLIGHT scene
        }

        private void TimedWindows(float t, float now, float dt)
        {
            if (_winName != null && dt > 0f && dt < 5f)
            {
                if (now >= _winStart && now < _winEnd)
                {
                    _winSum += dt;
                    _winFrames++;
                    if (dt > _winWorst) _winWorst = dt;
                }
            }
            else if (_refStart > 0f && dt > 0f && dt < 5f && now >= _refStart)
            {
                _refSum += dt;   // frames before the next window: its reference
                _refFrames++;
            }
            if (_winName != null && now >= _winEnd)
            {
                double ms = _winFrames > 0 ? _winSum * 1000.0 / _winFrames : 0, refMs = _refFrames > 0 ? _refSum * 1000.0 / _refFrames : 0;
                Log.Info("AutoBench WINDOW " + _winName + (_warpStep == 1 ? " (rate now x" + TimeWarp.CurrentRate.ToString("F0") + ")" : "") + ": " + ms.ToString("F1") + " ms per frame (" + _winFrames + " frames, worst " + (_winWorst * 1000f).ToString("F0")
                         + " ms) vs " + refMs.ToString("F1") + " ms just before (" + _refFrames + " frames) | vessels loaded " + FlightGlobals.VesselsLoaded.Count
                         + ", total " + FlightGlobals.Vessels.Count);
                if (_mapStep == 1) { MapView.ExitMapView(); _mapStep = 2; }
                if (_warpStep == 1) { TimeWarp.SetRate(0, true); _warpStep = 2; }
                _winName = null;
                _refStart = 0f;
            }
            if (_winName != null)
                return;
            float length = _mapStep == 0 && _mapAt > 0f ? _mapSeconds : _warpSeconds;
            float next = _mapStep == 0 && _mapAt > 0f ? _mapAt : (_warpStep == 0 && _warpAt > 0f ? _warpAt : -1f);
            if (next > 0f && t < next && t >= next - length - 1f && _refStart <= 0f)
            {
                _refStart = now;
                _refSum = 0;
                _refFrames = 0;
            }
            if (_mapStep == 0 && _mapAt > 0f && t >= _mapAt)
            {
                MapView.EnterMapView();
                _mapStep = 1;
                OpenWindow("map view", now, _mapSeconds);
            }
            else if (_warpStep == 0 && _warpAt > 0f && t >= _warpAt && (_mapStep == 0 ? _mapAt <= 0f : _mapStep == 2))
            {
                TimeWarp.SetRate(_warpIndex, true);
                _warpStep = 1;
                OpenWindow("time warp x" + (TimeWarp.fetch != null && _warpIndex < TimeWarp.fetch.warpRates.Length ? TimeWarp.fetch.warpRates[_warpIndex].ToString("F0") : "?"), now, _warpSeconds);
            }
        }

        private void OpenWindow(string name, float now, float seconds)
        {
            _winName = name;
            _winStart = now + 1f;
            _winEnd = _winStart + seconds;
            _winSum = 0;
            _winFrames = 0;
            _winWorst = 0f;
        }

        // Visual check of the part highlighting: the whole vessel highlighted, a screenshot, back to default, a screenshot.
        private void HighlightCheck(float t)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.rootPart == null)
                return;
            string dir = Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData");
            if (_highlightStep == 0 && t >= _highlightAt)
            {
                vessel.rootPart.SetHighlight(true, true);
                Log.Info("AutoBench: vessel highlighted");
                _highlightStep = 1;
            }
            else if (_highlightStep == 1 && t >= _highlightAt + 1.5f)
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "autobench_highlight_on.png"));
                _highlightStep = 2;
            }
            else if (_highlightStep == 2 && t >= _highlightAt + 3f)
            {
                for (int i = 0; i < vessel.parts.Count; i++)
                    vessel.parts[i].SetHighlightDefault();
                Log.Info("AutoBench: vessel highlight back to default");
                _highlightStep = 3;
            }
            else if (_highlightStep == 3 && t >= _highlightAt + 4.5f)
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "autobench_highlight_off.png"));
                _highlightStep = 4;
            }
        }

        private void Fly(float now)
        {
            float t = now - _flightStart;
            if (_cyclesDone < _cycles)
            {
                if (t >= _cycleSeconds)
                    EndCycle();
                return;
            }
            float dt = Time.unscaledDeltaTime;
            if (t > 2f && dt > 0f && dt < 1f && !_pauseActive)
            {
                _frames++;
                _frameTimeSum += dt;
                if (dt * 1000f > _worstFrameMs) _worstFrameMs = dt * 1000f;
            }

            // 10 s of game time (not real time): on a slowed-down machine fewer physics steps run per real second, and the
            // temperatures compared by the regression check would move with the machine's load, not with the code.
            if (!_earlyThermalLogged && Planetarium.GetUniversalTime() - _flightStartUT >= 10.0)
            {
                _earlyThermalLogged = true;
                Log.Info("AutoBench at 10 s: " + ThermalSummary());
                // Visual check between runs (e.g. texture loading changes): written at the end of this frame.
                string shot = Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), "autobench_10s.png");
                ScreenCapture.CaptureScreenshot(shot);
            }

            if (_highlightAt > 0f)
                HighlightCheck(t);
            if (_mapAt > 0f || _warpAt > 0f)
                TimedWindows(t, now, dt);
            if (_abToggle != null && t > 3f && dt > 0f && dt < 1f)
            {
                if (now >= _abNext)
                {
                    _abOn = !_abOn;
                    SetAb(_abOn);
                    _abNext = now + _abSeconds;
                }
                else if (now >= _abNext - _abSeconds + 0.5f)   // the first half second after a switch is left out
                {
                    int k = _abOn ? 1 : 0;
                    _abSum[k] += dt;
                    _abFrames[k]++;
                }
            }

            if (!_pauseActive)
                FlightInputHandler.state.mainThrottle = 1f;

            if (_watchUntil > 0f)
            {
                float ms = dt * 1000f;
                if (ms > _watchWorstMs) _watchWorstMs = ms;
                if (ms > 50f) _watchLongFrames++;
                if (now >= _watchUntil)
                {
                    Log.Info("AutoBench: explosion " + _watchIndex + " worst frame " + _watchWorstMs.ToString("F0") + " ms, "
                             + _watchLongFrames + " frames over 50 ms in the next second");
                    _watchUntil = -1f;
                }
            }
            if (_exploded < _explodeCount && now >= _nextExplosion && !_pauseActive)
                Explode(now);
            // Game time too, and never before the 10 s temperatures: the same flight before the crash on any machine load.
            if (_crashAt > 0f && !_crashed && _earlyThermalLogged && Planetarium.GetUniversalTime() - _flightStartUT >= _crashAt && !_pauseActive)
                Crash(now);
            if (_crashUntil > 0f)
            {
                float ms = dt * 1000f;
                _crashFrames++;
                if (ms > _crashWorstMs) _crashWorstMs = ms;
                if (ms > 50f) { _crashOver50++; _crashFrozenMs += ms; }
                if (ms > 100f) _crashOver100++;
                if (now >= _crashUntil)
                {
                    _crashUntil = -1f;
                    int left = 0;
                    foreach (Vessel vessel in FlightGlobals.VesselsLoaded)
                        if (vessel != null)
                            left += vessel.parts.Count;
                    Log.Info("AutoBench CRASH: " + _crashFrames + " frames in " + _crashWatchSeconds.ToString("F0") + " s, worst " + _crashWorstMs.ToString("F0")
                             + " ms, " + _crashOver50 + " over 50 ms, " + _crashOver100 + " over 100 ms, " + (_crashFrozenMs / 1000f).ToString("F2")
                             + " s in frames over 50 ms | parts " + _partsBeforeCrash + " -> " + left + " in " + FlightGlobals.VesselsLoaded.Count + " loaded vessels");
                }
            }

            if (!_pauseActive && now >= _nextStage && StageManager.CurrentStage > 0)
            {
                StageManager.ActivateNextStage();
                _stagesFired++;
                _nextStage = now + _stageIntervalSeconds;
            }

            if (!_pausedOnce && _pauseAtSeconds > 0f && t >= _pauseAtSeconds)
            {
                _pausedOnce = true;
                _pauseActive = true;
                _pauseEnd = now + _pauseDurationSeconds;
                FlightDriver.SetPause(true, false);
            }
            else if (_pauseActive && now >= _pauseEnd)
            {
                _pauseActive = false;
                FlightDriver.SetPause(false, false);
            }

            if (t >= _flightSeconds && !_pauseActive)
                Finish(t);
        }

        // Every part of the active vessel gets the same extra velocity towards the ground: the impact does the rest.
        private void Crash(float now)
        {
            _crashed = true;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null)
                return;
            _partsBeforeCrash = v.parts.Count;
            Vector3d down = (v.mainBody.position - v.CoMD).normalized;
            // Lifted crashHeight metres and dropped (plus crashSpeed towards the ground): the impact breaks it as in play.
            if (_crashHeight > 0f)
                v.SetPosition(v.transform.position - (Vector3)(down * _crashHeight));
            if (_crashSpeed > 0f)
                v.ChangeWorldVelocity(down * _crashSpeed);
            Diagnostics.PSystemTrace.BeginFlightWindow();
            _crashUntil = now + _crashWatchSeconds;
            _crashWorstMs = _crashFrozenMs = 0f;
            _crashOver50 = _crashOver100 = _crashFrames = 0;
            Log.Info("AutoBench: crash, " + _partsBeforeCrash + " parts dropped from " + _crashHeight.ToString("F0") + " m"
                     + (_crashSpeed > 0f ? " at " + _crashSpeed.ToString("F0") + " m/s" : ""));
        }

        // Parts picked evenly through the vessel (never the root), in a fixed order: same scenario on every run.
        private void Explode(float now)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (_exploded == 0)
                Diagnostics.PSystemTrace.BeginFlightWindow();
            int fired = 0;
            for (int b = 0; b < _explodeBurst && _exploded < _explodeCount && v != null; b++)
            {
                int n = v.parts.Count;
                if (n < 2)
                    break;
                int index = 1 + (int)((long)(_exploded * 7919L + 13) % (n - 1));
                Part p = v.parts[index];
                _exploded++;
                if (p != null && p != v.rootPart)
                {
                    p.explode();
                    fired++;
                }
            }
            _watchIndex = _exploded;
            _watchWorstMs = 0f;
            _watchLongFrames = 0;
            _watchUntil = now + 1f;
            _nextExplosion = now + _explodeInterval;
            Log.Info("AutoBench: " + fired + " part(s) exploded (" + _exploded + " of " + _explodeCount + ")");
        }

        // Odd cycles revert to launch, even ones go back to the space centre and launch again from there.
        private void EndCycle()
        {
            _cyclesDone++;
            _sceneRequested = Time.realtimeSinceStartup;
            _phaseTime = float.MaxValue;
            if ((_cyclesDone & 1) == 1 && FlightDriver.CanRevertToPostInit && FlightDriver.PostInitState != null)
            {
                Log.Info("AutoBench: cycle " + _cyclesDone + " ends, revert to launch");
                _phase = Phase.Launching;
                Diagnostics.PSystemTrace.BeginFlightWindow("revert");
                FlightDriver.RevertToLaunch();
            }
            else
            {
                Log.Info("AutoBench: cycle " + _cyclesDone + " ends, back to the space centre");
                _phase = Phase.WaitSpaceCenter;
                Diagnostics.PSystemTrace.BeginFlightWindow("spacecentre");
                HighLogic.LoadScene(GameScenes.SPACECENTER);
            }
        }

        private void SetAb(bool on)
        {
            if (_abToggle == null)
                return;
            if (string.Equals(_abToggle, "HighlightIdle", StringComparison.OrdinalIgnoreCase))
                Rendering.HighlightIdle.Bypass = !on;
            else if (string.Equals(_abToggle, "HudText", StringComparison.OrdinalIgnoreCase))
                Rendering.HudText.Bypass = !on;
        }

        private void Finish(float flightTime)
        {
            _phase = Phase.Done;
            if (_abToggle != null)
            {
                SetAb(true);
                double on = _abFrames[1] > 0 ? _abSum[1] * 1000.0 / _abFrames[1] : 0, off = _abFrames[0] > 0 ? _abSum[0] * 1000.0 / _abFrames[0] : 0;
                Log.Info("AutoBench A/B " + _abToggle + ": on " + on.ToString("F2") + " ms (" + _abFrames[1] + " frames) | off " + off.ToString("F2")
                         + " ms (" + _abFrames[0] + " frames) | difference " + (off - on).ToString("F2") + " ms per frame");
            }
            GCWindows windows = GCWindows.Active;
            float avgMs = _frames > 0 ? _frameTimeSum * 1000f / _frames : 0f;
            Log.Info("AutoBench REPORT: craft '" + _craft + "' flight " + flightTime.ToString("F0") + " s"
                     + " | frames " + _frames + " avg " + avgMs.ToString("F1") + " ms (" + (avgMs > 0 ? 1000f / avgMs : 0f).ToString("F1") + " fps)"
                     + " worst " + _worstFrameMs.ToString("F0") + " ms"
                     + " | stages fired " + _stagesFired
                     + " | GC cycles " + (GC.CollectionCount(0) - _gcAtFlightStart)
                     + " | holds " + (windows?.HoldCount ?? -1) + " paused collections " + (windows?.PausedCollections ?? -1)
                     + " | heap " + (Profiler.GetMonoHeapSizeLong() / (1024 * 1024)) + " MB, divisor " + BoehmGC.FreeSpaceDivisor
                     + " | " + ThermalSummary());
            Log.Info("AutoBench DONE");
            if (_quitWhenDone)
                Application.Quit();
        }

        // Sanity check for physics optimizations: hottest part of the active vessel relative to its limits.
        private static string ThermalSummary()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
                return "thermal n/a";
            double worst = 0, maxTemp = 0, maxSkin = 0, sumTemp = 0, sumSkin = 0;
            int parts = 0;
            foreach (Part p in v.parts)
            {
                if (p == null)
                    continue;
                parts++;
                double ratio = Math.Max(p.temperature / Math.Max(1.0, p.maxTemp), p.skinTemperature / Math.Max(1.0, p.skinMaxTemp));
                if (ratio > worst) worst = ratio;
                if (p.temperature > maxTemp) maxTemp = p.temperature;
                if (p.skinTemperature > maxSkin) maxSkin = p.skinTemperature;
                sumTemp += p.temperature;
                sumSkin += p.skinTemperature;
            }
            if (parts == 0)
                return "thermal n/a";
            return "thermal: " + parts + " parts, hottest " + (worst * 100).ToString("F2") + "% of limit, max core " + maxTemp.ToString("F2")
                   + " K, max skin " + maxSkin.ToString("F2") + " K, mean core " + (sumTemp / parts).ToString("F3")
                   + " K, mean skin " + (sumSkin / parts).ToString("F3") + " K";
        }
    }
}
