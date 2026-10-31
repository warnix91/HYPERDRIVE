using System.Diagnostics;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// Once, shortly after the main menu first appears, asks Unity to create the GPU programs of every loaded shader
    /// (Shader.WarmupAllShaders), so that the first explosion, plume or effect does not hitch while the driver compiles.
    /// Stands down on Metal/Vulkan/DX12 where Unity only partially supports it, and on macOS: measured on a Mac M4,
    /// KSP froze for 18+ minutes in WarmupAllShaders ("AGX: exceeded compiled variants footprint limit").
    /// Off by default; experimental on Windows/DX11.
    /// </summary>
    internal sealed class ShaderWarmup : Subsystem
    {
        private float _delaySeconds = 2f;
        private bool _done;
        private float _runAt = -1f;

        public override string Name => "ShaderWarmup";

        public override bool EnabledByDefault => false;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _delaySeconds = Mathf.Clamp(settings.GetFloat("delaySeconds", _delaySeconds), 0f, 60f);
        }

        protected internal override void OnEnable()
        {
            if (Application.platform == RuntimePlatform.OSXPlayer)
                throw new SubsystemUnavailableException("freezes the game on macOS (Apple GPU driver)");
            GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
            if (api == GraphicsDeviceType.Metal || api == GraphicsDeviceType.Vulkan || api == GraphicsDeviceType.Direct3D12)
                throw new SubsystemUnavailableException("not reliable on " + api);
            Log.Warn("ShaderWarmup is EXPERIMENTAL: the main menu may freeze for a while once. Disable it if it takes too long.");
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (!_done && scene == GameScenes.MAINMENU)
                _runAt = Time.unscaledTime + _delaySeconds;
        }

        protected internal override void OnUpdate()
        {
            if (_done || _runAt < 0f || Time.unscaledTime < _runAt)
                return;
            _done = true;

            var watch = Stopwatch.StartNew();
            Shader.WarmupAllShaders();
            Log.Info("ShaderWarmup: warmed up all loaded shaders in " + watch.ElapsedMilliseconds + " ms");
        }
    }
}
