using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Throttle
{
    /// <summary>
    /// Opt-in throttling of other mods' Update/LateUpdate when the frame rate drops. Each listed method is skipped on
    /// most frames and runs at about <c>throttledHz</c>; instances are spread across frames (instance id modulo N) so the
    /// remaining cost is smoothed rather than bunched. FixedUpdate (physics) and OnGUI are refused.
    /// Only for visual or UI work: a method that integrates Time.deltaTime will lose the skipped time.
    /// </summary>
    internal sealed class ThrottleGuard : Subsystem
    {
        private static readonly string[] AllowedMethods = { "Update", "LateUpdate" };

        // Read by the prefix on every call of a throttled method: plain statics, no allocation.
        private static bool s_engaged;
        private static int s_divisor = 1;
        private static int s_frame;
        private static bool s_alwaysOn;

        private float _engageBelowFps = 40f;
        private float _disengageAboveFps = 50f;
        private float _throttledHz = 15f;
        private ConfigNode[] _targetNodes = new ConfigNode[0];

        private float _smoothedFrameTime = 1f / 60f;
        private int _targetCount;

        public static bool Engaged => s_engaged;
        public static int Divisor => s_divisor;
        public int TargetCount => _targetCount;

        public override string Name => "ThrottleGuard";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _engageBelowFps = Mathf.Clamp(settings.GetFloat("engageBelowFps", _engageBelowFps), 5f, 240f);
            _disengageAboveFps = Mathf.Clamp(settings.GetFloat("disengageAboveFps", _disengageAboveFps), _engageBelowFps + 1f, 300f);
            _throttledHz = Mathf.Clamp(settings.GetFloat("throttledHz", _throttledHz), 1f, 120f);
            s_alwaysOn = string.Equals(settings.GetString("mode", "auto"), "always", StringComparison.OrdinalIgnoreCase);
            _targetNodes = settings.GetNodes("TARGET");
        }

        protected internal override void OnEnable()
        {
            s_engaged = false;
            s_divisor = 1;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(ThrottleGuard), nameof(SkipPrefix)));
            var seen = new HashSet<MethodBase>();

            foreach (ConfigNode node in _targetNodes)
            {
                string typeName = node.GetValue("type");
                string methodName = node.GetValue("method") ?? "Update";
                if (string.IsNullOrEmpty(typeName))
                    continue;

                if (Array.IndexOf(AllowedMethods, methodName) < 0)
                {
                    Log.Warn("ThrottleGuard: refusing to throttle " + typeName + "." + methodName + " (only Update/LateUpdate are allowed)");
                    continue;
                }

                Type type = AccessTools.TypeByName(typeName);
                MethodInfo method = type != null ? AccessTools.DeclaredMethod(type, methodName, Type.EmptyTypes) : null;
                if (method == null || method.IsAbstract || method.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    Log.Warn("ThrottleGuard: target not found or unsupported, skipped: " + typeName + "." + methodName);
                    continue;
                }
                if (!seen.Add(method))
                    continue;

                // A bad target must not take the others down with it.
                try
                {
                    Harmony.Patch(method, prefix: prefix);
                    _targetCount++;
                    Log.Info("ThrottleGuard: target " + ModAssemblies.ModName(method) + " " + type.FullName + "." + methodName);
                }
                catch (Exception e)
                {
                    Log.Warn("ThrottleGuard: cannot patch " + typeName + "." + methodName + ": " + e.Message);
                }
            }

            Log.Info("ThrottleGuard: " + _targetCount + " target(s), " + (s_alwaysOn ? "always on" : "engages below " + _engageBelowFps + " fps, releases above " + _disengageAboveFps)
                     + ", throttled rate " + _throttledHz + " Hz");
        }

        protected internal override void OnDisable()
        {
            s_engaged = false;
            s_divisor = 1;
        }

        protected internal override void OnUpdate()
        {
            s_frame = Time.frameCount;
            if (_targetCount == 0)
                return;

            float dt = Time.unscaledDeltaTime;
            if (dt > 0f && dt < 1f)
                _smoothedFrameTime += (dt - _smoothedFrameTime) * 0.05f;
            float fps = 1f / Mathf.Max(_smoothedFrameTime, 0.0001f);

            bool engaged = s_alwaysOn
                || (s_engaged ? fps < _disengageAboveFps : fps < _engageBelowFps);

            // In flight only for the automatic mode: menus and editor are not worth the risk.
            if (!s_alwaysOn && HighLogic.LoadedScene != GameScenes.FLIGHT)
                engaged = false;

            s_divisor = engaged ? Mathf.Max(1, Mathf.RoundToInt(fps / _throttledHz)) : 1;
            s_engaged = engaged && s_divisor > 1;
        }

        private static bool SkipPrefix(object __instance)
        {
            if (!s_engaged)
                return true;
            // UnityEngine.Object.GetHashCode() is the instance id: stable per object, no allocation.
            int phase = __instance.GetHashCode() & 0x7fffffff;
            return (s_frame + phase) % s_divisor == 0;
        }
    }
}
