using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Hyperdrive.Throttle
{
    /// <summary>Enumerates the per-frame Unity callbacks of third-party mods (never stock KSP, Unity or Hyperdrive).</summary>
    internal static class ModAssemblies
    {
        // Matched on the real assembly name: KSP's AssemblyLoader can list the game's own assembly under another name.
        private static readonly HashSet<string> Stock = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Assembly-CSharp", "Assembly-CSharp-firstpass", "KSPSteamCtrlr"
        };

        private static readonly HashSet<string> NeverTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "0Harmony", "HarmonyInstallChecker", "Hyperdrive", "Hyperdrive.API"
        };

        public static bool IsStock(Assembly assembly) => Stock.Contains(assembly.GetName().Name);

        /// <summary>
        /// Declared, non-generic, parameterless instance methods with the given names on MonoBehaviour subclasses of
        /// loaded plugins (each assembly once), optionally including the stock game's assemblies.
        /// </summary>
        public static List<MethodInfo> FindCallbacks(string[] methodNames, bool includeStock)
        {
            var result = new List<MethodInfo>();
            var seen = new HashSet<Assembly>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Assembly assembly = loaded?.assembly;
                if (assembly == null || !seen.Add(assembly))
                    continue;
                string assemblyName = assembly.GetName().Name;
                if (NeverTouched.Contains(assemblyName) || (!includeStock && Stock.Contains(assemblyName)))
                    continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }

                foreach (Type type in types)
                {
                    if (type == null || type.IsAbstract || type.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(type))
                        continue;

                    foreach (string name in methodNames)
                    {
                        MethodInfo method;
                        try { method = type.GetMethod(name, flags, null, Type.EmptyTypes, null); }
                        catch (AmbiguousMatchException) { continue; }

                        if (method != null && !method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null)
                            result.Add(method);
                    }
                }
            }
            return result;
        }

        // Unity messages outside Update/LateUpdate/FixedUpdate/OnGUI: physics contacts and camera/render callbacks.
        private static readonly HashSet<string> MoreMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit",
            "OnWillRenderObject", "OnRenderObject", "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage", "OnBecameVisible", "OnBecameInvisible"
        };

        /// <summary>
        /// Physics and render messages of MonoBehaviour subclasses (any parameters), and with coroutines the MoveNext of the
        /// compiled iterators nested in them (what StartCoroutine runs every frame), in the same assemblies as FindCallbacks.
        /// </summary>
        public static List<MethodInfo> FindMoreCallbacks(bool includeStock, bool coroutines)
        {
            var result = new List<MethodInfo>();
            var seen = new HashSet<Assembly>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Assembly assembly = loaded?.assembly;
                if (assembly == null || !seen.Add(assembly))
                    continue;
                string assemblyName = assembly.GetName().Name;
                if (NeverTouched.Contains(assemblyName) || (!includeStock && Stock.Contains(assemblyName)))
                    continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (Type type in types)
                {
                    if (type == null || type.ContainsGenericParameters)
                        continue;
                    bool behaviour = !type.IsAbstract && typeof(MonoBehaviour).IsAssignableFrom(type);
                    bool iterator = coroutines && type.IsNested && typeof(System.Collections.IEnumerator).IsAssignableFrom(type)
                                    && type.DeclaringType != null && typeof(MonoBehaviour).IsAssignableFrom(type.DeclaringType);
                    if (!behaviour && !iterator)
                        continue;
                    MethodInfo[] methods;
                    try { methods = type.GetMethods(flags); }
                    catch (Exception) { continue; }
                    foreach (MethodInfo method in methods)
                    {
                        bool wanted = behaviour ? MoreMessages.Contains(method.Name) : method.Name == "MoveNext" && method.GetParameters().Length == 0;
                        if (wanted && !method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null)
                            result.Add(method);
                    }
                }
            }
            return result;
        }

        public static string ModName(MethodBase method) => method.DeclaringType?.Assembly.GetName().Name ?? "?";

        public static string ShortName(MethodBase method) => method.DeclaringType?.Name + "." + method.Name;
    }
}
