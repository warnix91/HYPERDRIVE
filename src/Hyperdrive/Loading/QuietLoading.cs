using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// No window over the loading screen: message boxes that mods open while the game loads are held back and shown at
    /// the main menu instead, unchanged (same text, same buttons, same actions). Covers KSP's own PopupDialog and the
    /// KSPe message boxes (KSPe.UI.*, one copy in every KSPe-based mod: TweakScale, KAX, AirplanePlus, KSP-Recall...).
    /// Nothing is dropped: a held window is only hidden (component disabled or object inactive) until the menu.
    /// </summary>
    internal sealed class QuietLoading : Subsystem
    {
        private static readonly List<MonoBehaviour> s_heldBoxes = new List<MonoBehaviour>();
        private static readonly List<GameObject> s_heldDialogs = new List<GameObject>();
        private static readonly List<GameObject> s_kept = new List<GameObject>();     // kept across the scene changes until the menu
        private static bool s_holding;

        public override string Name => "QuietLoading";

        protected internal override void OnEnable()
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING)
                throw new SubsystemUnavailableException("only during the game's first load");
            int patched = 0;
            foreach (MethodInfo spawn in typeof(PopupDialog).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (spawn.Name == nameof(PopupDialog.SpawnPopupDialog) && spawn.ReturnType == typeof(PopupDialog))
                {
                    Patch(spawn, postfix: nameof(SpawnPostfix));
                    patched++;
                }
            int boxes = PatchKspeBoxes();
            s_holding = true;
            Log.Info("QuietLoading: " + patched + " PopupDialog entry points and " + boxes + " KSPe message box types held back until the main menu");
        }

        protected internal override void OnDisable() => Release("subsystem disabled");

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU)
                Release("main menu reached");
        }

        // Every MonoBehaviour of a KSPe.UI namespace that draws with OnGUI and has Show methods, in any loaded assembly.
        private int PatchKspeBoxes()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            int count = 0;
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try { types = loaded.assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (Type type in types)
                {
                    if (type == null || type.Namespace == null || !type.Namespace.StartsWith("KSPe.UI", StringComparison.Ordinal)
                        || !type.IsSubclassOf(typeof(MonoBehaviour)) || type.GetMethod("OnGUI", flags) == null)
                        continue;
                    bool any = false;
                    foreach (MethodInfo show in type.GetMethods(flags))
                    {
                        if (show.Name != "Show" || show.IsAbstract || show.ContainsGenericParameters)
                            continue;
                        try
                        {
                            Patch(show, postfix: nameof(ShowPostfix));
                            any = true;
                        }
                        catch (Exception e)
                        {
                            Log.Warn("QuietLoading: could not hold " + type.FullName + "." + show.Name + " (" + e.Message + ")");
                        }
                    }
                    if (any)
                        count++;
                }
            }
            return count;
        }

        private static void ShowPostfix(MonoBehaviour __instance)
        {
            if (!s_holding || __instance == null || !__instance.enabled)
                return;
            __instance.enabled = false;     // no OnGUI, so no window, until the menu
            s_heldBoxes.Add(__instance);
            Keep(__instance.gameObject);
            Log.Info("QuietLoading: message box from " + __instance.GetType().Assembly.GetName().Name + " held until the main menu");
        }

        private static void SpawnPostfix(PopupDialog __result)
        {
            if (!s_holding || __result == null || !__result.gameObject.activeSelf)
                return;
            __result.gameObject.SetActive(false);
            s_heldDialogs.Add(__result.gameObject);
            Keep(__result.gameObject);
            Log.Info("QuietLoading: dialog '" + __result.gameObject.name + "' held until the main menu");
        }

        // A window in the loading scene would be destroyed by the scene changes that end the load (in stock it is seen
        // before that): its root is kept alive until the menu, then put back in the active scene, as stock would have it.
        private static void Keep(GameObject window)
        {
            GameObject root = window.transform.root.gameObject;
            if (root.scene.name == "DontDestroyOnLoad")
                return;
            UnityEngine.Object.DontDestroyOnLoad(root);
            s_kept.Add(root);
        }

        private static void Release(string why)
        {
            if (!s_holding)
                return;
            s_holding = false;
            int shown = 0;
            foreach (MonoBehaviour box in s_heldBoxes)
                if (box != null)
                {
                    box.enabled = true;
                    shown++;
                }
            foreach (GameObject dialog in s_heldDialogs)
                if (dialog != null)
                {
                    dialog.SetActive(true);
                    shown++;
                }
            foreach (GameObject root in s_kept)
                if (root != null)
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            s_heldBoxes.Clear();
            s_heldDialogs.Clear();
            s_kept.Clear();
            if (shown > 0)
                Log.Info("QuietLoading: " + shown + " held windows shown (" + why + ")");
        }
    }
}
