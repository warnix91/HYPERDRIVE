using System.Collections.Generic;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// Caps how many dynamic lights registered by mods (explosion flashes, fires...) are enabled at once. Every few frames
    /// the registered lights are scored (priority x intensity x range / distance to the camera); the best ones stay on,
    /// the rest are switched off until they score high enough again. Stock lights and unregistered lights are never touched.
    /// Allocation-free after registration.
    /// </summary>
    internal sealed class LightBudget : Subsystem
    {
        private const int Capacity = 512;
        private const float RefreshSeconds = 0.2f;

        public static LightBudget Active { get; private set; }

        private int _maxLights = 8;

        private readonly Light[] _lights = new Light[Capacity];
        private readonly float[] _priority = new float[Capacity];
        private readonly float[] _score = new float[Capacity];
        private readonly bool[] _disabledByUs = new bool[Capacity];
        private readonly Dictionary<int, int> _indexById = new Dictionary<int, int>(Capacity);
        private int _count;
        private float _nextRefresh;

        public int RegisteredCount => _count;
        public int MaxLights => _maxLights;

        public override string Name => "LightBudget";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _maxLights = Mathf.Clamp(settings.GetInt("maxLights", _maxLights), 1, 64);
        }

        protected internal override void OnEnable()
        {
            Active = this;
            Log.Info("LightBudget: at most " + _maxLights + " mod-registered dynamic lights enabled at once");
        }

        protected internal override void OnDisable()
        {
            if (Active == this)
                Active = null;
            for (int i = 0; i < _count; i++)
            {
                if (_disabledByUs[i] && _lights[i] != null)
                    _lights[i].enabled = true;
                _lights[i] = null;
            }
            _count = 0;
            _indexById.Clear();
        }

        public bool Register(Light light, float priority)
        {
            if (light == null)
                return false;
            int id = light.GetInstanceID();
            if (_indexById.TryGetValue(id, out int existing))
            {
                _priority[existing] = Mathf.Max(0.01f, priority);
                return true;
            }
            if (_count >= Capacity)
                return false;

            _lights[_count] = light;
            _priority[_count] = Mathf.Max(0.01f, priority);
            _disabledByUs[_count] = false;
            _indexById[id] = _count;
            _count++;
            _nextRefresh = 0f; // apply the budget on the next frame
            return true;
        }

        public bool Unregister(Light light)
        {
            if (ReferenceEquals(light, null))
                return false;
            if (!_indexById.TryGetValue(light.GetInstanceID(), out int index))
                return false;
            if (_disabledByUs[index] && light != null)
                light.enabled = true;
            RemoveAt(index);
            return true;
        }

        protected internal override void OnUpdate()
        {
            float now = Time.unscaledTime;
            if (now < _nextRefresh || _count == 0)
                return;
            _nextRefresh = now + RefreshSeconds;

            Camera camera = FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null ? FlightCamera.fetch.mainCamera : Camera.main;
            Vector3 eye = camera != null ? camera.transform.position : Vector3.zero;

            // Drop lights destroyed by their owner, then score the rest.
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_lights[i] == null)
                    RemoveAt(i);
            }

            int candidates = 0;
            for (int i = 0; i < _count; i++)
            {
                Light light = _lights[i];
                bool wanted = light.gameObject.activeInHierarchy && (light.enabled || _disabledByUs[i]);
                if (!wanted)
                {
                    _score[i] = -1f;
                    continue;
                }
                float distance = (light.transform.position - eye).magnitude;
                _score[i] = _priority[i] * light.intensity * light.range / (1f + distance);
                candidates++;
            }

            if (candidates <= _maxLights)
            {
                for (int i = 0; i < _count; i++)
                    if (_score[i] >= 0f) SetEnabled(i, true);
                return;
            }

            // Threshold = score of the max-th best light (partial selection, O(n * max), no sorting allocation).
            float threshold = float.MaxValue;
            for (int k = 0; k < _maxLights; k++)
            {
                float best = -1f;
                for (int i = 0; i < _count; i++)
                {
                    float s = _score[i];
                    if (s < threshold && s > best)
                        best = s;
                }
                threshold = best;
            }

            int enabled = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_score[i] < 0f)
                    continue;
                bool on = _score[i] >= threshold && enabled < _maxLights;
                if (on) enabled++;
                SetEnabled(i, on);
            }
        }

        private void SetEnabled(int index, bool on)
        {
            Light light = _lights[index];
            if (on)
            {
                if (_disabledByUs[index])
                {
                    light.enabled = true;
                    _disabledByUs[index] = false;
                }
            }
            else if (light.enabled)
            {
                light.enabled = false;
                _disabledByUs[index] = true;
            }
        }

        private void RemoveAt(int index)
        {
            Light removed = _lights[index];
            if (!ReferenceEquals(removed, null))
                _indexById.Remove(removed.GetInstanceID());

            int last = --_count;
            if (index != last)
            {
                _lights[index] = _lights[last];
                _priority[index] = _priority[last];
                _score[index] = _score[last];
                _disabledByUs[index] = _disabledByUs[last];
                _indexById[_lights[index].GetInstanceID()] = index;
            }
            _lights[last] = null;
            _disabledByUs[last] = false;
        }
    }
}
