using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Batch build of Hyperdrive's shader bundles, one per platform (KSP: OpenGL Core on macOS and Linux, Direct3D 11 or OpenGL
// on Windows). Run by tools/build_shaders.sh with HYPERDRIVE_BUNDLE_TARGET = mac | windows and HYPERDRIVE_BUNDLE_OUTPUT.
public static class BuildShaders
{
    private const string ShaderPath = "Assets/Shaders/Upscale.shader";

    public static void Build()
    {
        try
        {
            if (Application.unityVersion != "2019.4.18f1")
                throw new Exception("Unity 2019.4.18f1 (KSP 1.12) required, this is " + Application.unityVersion);
            string platform = Environment.GetEnvironmentVariable("HYPERDRIVE_BUNDLE_TARGET") ?? "mac";
            string output = Environment.GetEnvironmentVariable("HYPERDRIVE_BUNDLE_OUTPUT");
            if (string.IsNullOrEmpty(output))
                throw new Exception("HYPERDRIVE_BUNDLE_OUTPUT not set");
            BuildTarget target = platform == "windows" ? BuildTarget.StandaloneWindows64
                : platform == "linux" ? BuildTarget.StandaloneLinux64 : BuildTarget.StandaloneOSX;
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                throw new Exception("Unity has no build support installed for " + target);
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, platform == "windows"
                ? new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore }
                : platform == "linux" ? new[] { GraphicsDeviceType.OpenGLCore, GraphicsDeviceType.Vulkan }
                : new[] { GraphicsDeviceType.OpenGLCore, GraphicsDeviceType.Metal });
            AssetDatabase.Refresh();

            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null || ShaderUtil.ShaderHasError(shader))
                throw new Exception("shader import failed: " + ShaderPath);

            Directory.CreateDirectory(output);
            var bundle = new AssetBundleBuild { assetBundleName = "hyperdrive-" + platform + ".bundle", assetNames = new[] { ShaderPath } };
            if (BuildPipeline.BuildAssetBundles(output, new[] { bundle }, BuildAssetBundleOptions.ForceRebuildAssetBundle
                    | BuildAssetBundleOptions.ChunkBasedCompression, target) == null)
                throw new Exception("bundle build failed");
            foreach (ShaderMessage message in ShaderUtil.GetShaderMessages(shader))
                if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                    throw new Exception(shader.name + ": " + message.message + " (" + message.platform + ")");

            if (platform == "mac" && Application.platform == RuntimePlatform.OSXEditor)
            {
                AssetBundle loaded = AssetBundle.LoadFromFile(Path.Combine(output, bundle.assetBundleName));
                if (loaded == null)
                    throw new Exception("built bundle could not be loaded back");
                Shader back = loaded.LoadAsset<Shader>(ShaderPath);
                bool ok = back != null && back.isSupported;
                loaded.Unload(true);
                if (!ok)
                    throw new Exception("built shader not supported on this editor's graphics API");
            }
            Debug.Log("HYPERDRIVE_BUNDLE_OK " + platform);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("HYPERDRIVE_BUNDLE_FAILED " + e);
            EditorApplication.Exit(1);
        }
    }
}
