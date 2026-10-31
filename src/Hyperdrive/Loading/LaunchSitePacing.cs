using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// First load: to place the Making History launch sites that flatten the terrain, PSystemSetup.SetupLaunchSites forces
    /// a full terrain build of the home planet (PQS.ForceStart: ~1100 quad meshes, 2.1 s in one frozen frame on a Mac),
    /// then waits 2 s and orients the site. Here the quads are built in the same order by the same stock calls, a few
    /// milliseconds per frame, during that wait; the rest of the sphere start (OnPostSetup, OnSphereStarted, child spheres,
    /// OnReady, the update coroutine) runs right after the last quad, still before the site is oriented. The wait lasts
    /// until then if the build needs longer than 2 s. A terrain and launch site fingerprint is logged for comparison with
    /// spread = false (stock build).
    /// </summary>
    internal sealed class LaunchSitePacing : Subsystem
    {
        private sealed class Job
        {
            public PQS Sphere;
            public bool Force;
            public int Root = 5;
            public readonly PQ[] Nodes = new PQ[64];
            public readonly int[] Next = new int[64];
            public int Depth;
            public bool Building = true;
            public int Frames, Leaves, SliceVisits;
            public double MaxSliceMs, SlowestMs;
            public PQ Slowest;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
        }

        // Waits like the stock WaitForSeconds and, past it, until the deferred build and sphere start are done.
        private sealed class BuildWait : CustomYieldInstruction
        {
            private readonly float _until;
            public BuildWait(float seconds) { _until = Time.time + seconds; }
            public override bool keepWaiting => s_job != null || (s_keepStockWait && Time.time < _until) || Time.frameCount < s_tailFrame + 2;
        }

        private static LaunchSitePacing s_instance;
        private static bool s_spread = true, s_fingerprint = true, s_keepStockWait = true;
        private static float s_buildBudget = -1f;   // ms per frame for the build; <= 0: the loading screen's budget
        private static float s_fallbackBudget = 0.008f;
        private static PQS s_forcing;          // inside PSystemSetup.ForceInitPQS
        private static PQS s_deferred;         // UpdateQuadsInit was deferred: StartSphere returns right after it
        private static PQS s_watch;            // sphere forced for a launch site, until the site is oriented
        private static Job s_job;
        private static int s_tailFrame = -10;

        private static MethodInfo s_updateQuadsInit, s_tailCheck;
        private static Action<PQS> s_createQuads, s_modOnPostSetup, s_modOnSphereStarted;
        private static Action<PQS, bool> s_startSphere;
        private static Func<PQS, IEnumerator> s_updateSphere;
        private static AccessTools.FieldRef<PQ, bool> s_outOfTime;
        private static AccessTools.FieldRef<object, object> s_launchSitesCurrent;
        private static AccessTools.FieldRef<WaitForSeconds, float> s_waitSeconds;
        private static int s_tailChecks;

        public override string Name => "LaunchSitePacing";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_spread = settings.GetBool("spread", s_spread);
            s_fingerprint = settings.GetBool("fingerprint", s_fingerprint);
            s_fallbackBudget = Mathf.Clamp(settings.GetFloat("frameBudgetMs", 8f), 2f, 50f) / 1000f;
            s_keepStockWait = settings.GetBool("keepStockWait", s_keepStockWait);
            float build = settings.GetFloat("buildBudgetMs", -1f);
            s_buildBudget = build > 0f ? Mathf.Clamp(build, 2f, 100f) / 1000f : -1f;
        }

        protected internal override void OnEnable()
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING)
                throw new SubsystemUnavailableException("only acts on the first load");

            Type pqs = typeof(PQS);
            s_updateQuadsInit = AccessTools.Method(pqs, "UpdateQuadsInit");
            s_tailCheck = AccessTools.Method(typeof(LaunchSitePacing), nameof(ReturnAfterInit));
            MethodInfo startSphere = AccessTools.Method(pqs, "StartSphere", new[] { typeof(bool) });
            MethodInfo createQuads = AccessTools.Method(pqs, "CreateQuads");
            MethodInfo postSetup = AccessTools.Method(pqs, "Mod_OnPostSetup");
            MethodInfo sphereStarted = AccessTools.Method(pqs, "Mod_OnSphereStarted");
            MethodInfo updateSphere = AccessTools.Method(pqs, "UpdateSphere");
            FieldInfo outOfTime = AccessTools.Field(typeof(PQ), "outOfTime");
            MethodInfo forceInit = AccessTools.Method(typeof(PSystemSetup), "ForceInitPQS", new[] { typeof(PQS), typeof(Transform) });
            MethodInfo launchSites = AccessTools.Method(typeof(PSystemSetup), "SetupLaunchSites");
            MethodInfo moveNext = launchSites != null ? AccessTools.EnumeratorMoveNext(launchSites) : null;
            FieldInfo current = moveNext?.DeclaringType.GetField("<>2__current", BindingFlags.Instance | BindingFlags.NonPublic);
            if (s_updateQuadsInit == null || startSphere == null || createQuads == null || postSetup == null || sphereStarted == null
                || updateSphere == null || outOfTime == null || forceInit == null || moveNext == null || current == null)
                throw new SubsystemUnavailableException("PQS / PSystemSetup members not found (game version mismatch?)");

            s_createQuads = AccessTools.MethodDelegate<Action<PQS>>(createQuads);
            s_modOnPostSetup = AccessTools.MethodDelegate<Action<PQS>>(postSetup);
            s_modOnSphereStarted = AccessTools.MethodDelegate<Action<PQS>>(sphereStarted);
            s_startSphere = AccessTools.MethodDelegate<Action<PQS, bool>>(startSphere);
            s_updateSphere = AccessTools.MethodDelegate<Func<PQS, IEnumerator>>(updateSphere);
            s_outOfTime = AccessTools.FieldRefAccess<PQ, bool>(outOfTime);
            s_launchSitesCurrent = AccessTools.FieldRefAccess<object, object>(current);
            FieldInfo seconds = AccessTools.Field(typeof(WaitForSeconds), "m_Seconds");
            s_waitSeconds = seconds != null ? AccessTools.FieldRefAccess<WaitForSeconds, float>(seconds) : null;

            s_tailChecks = 0;
            Patch(startSphere, prefix: nameof(StartSpherePrefix), transpiler: nameof(StartSphereTranspiler));
            if (s_tailChecks != 1)
                throw new InvalidOperationException("expected one UpdateQuadsInit call in PQS.StartSphere, found " + s_tailChecks);
            Patch(s_updateQuadsInit, prefix: nameof(UpdateQuadsInitPrefix));
            Patch(forceInit, prefix: nameof(ForceInitPrefix), postfix: nameof(ForceInitPostfix));
            Patch(moveNext, postfix: nameof(LaunchSitesMoveNextPostfix));
            Patch(AccessTools.Method(pqs, "ResetSphere"), prefix: nameof(ResetSpherePrefix));
            Patch(AccessTools.Method(typeof(PQSCity), "Orientate", new[] { typeof(bool) }),
                  prefix: nameof(CityOrientatePrefix), postfix: nameof(CityOrientatePostfix));
            Patch(AccessTools.Method(typeof(PQSCity2), "Orientate", Type.EmptyTypes),
                  prefix: nameof(City2OrientatePrefix), postfix: nameof(City2OrientatePostfix));
            s_instance = this;
            Log.Info("LaunchSitePacing: forced launch site terrain builds " + (s_spread ? "spread over the stock 2 s wait" : "left to stock (spread = false)")
                     + (s_fingerprint ? ", fingerprint logged" : ""));
        }

        protected internal override void OnDisable()
        {
            if (s_job != null)
                Finish("subsystem disabled");
            s_instance = null;
            s_forcing = s_deferred = s_watch = null;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene != GameScenes.MAINMENU)
                return;
            if (s_job != null)
                Finish("main menu reached");
            s_watch = null;
            // Nothing left to do after the first load. The patches stay, inert (removing them costs a 50 ms frame here).
            s_instance = null;
            s_spread = false;
        }

        // ------------------------------------------------------------------ deferral

        private static void ForceInitPrefix(PQS pqsIn)
        {
            if (s_instance == null || pqsIn == null)
                return;
            s_forcing = pqsIn;
            s_watch = s_fingerprint ? pqsIn : null;
        }

        private static void ForceInitPostfix()
        {
            s_forcing = null;
        }

        // Another mod hooking the quads may count on the whole forced build happening in the one stock frame: Parallax's
        // scatters (PQ.SetVisible prefix) start on quads built over several frames before its shaders are ready, throw, and
        // the build stops short (377 quads built instead of 1304). So any foreign patch on PQ, and on PQS.UpdateQuadsInit
        // anything but a void prefix (Kopernicus fires its pre-init event there, before the build either way): a postfix
        // would run before the deferred build is done. Owner and method of the first such patch, or null.
        private static string ForeignQuadHook()
        {
            foreach (MethodBase patched in Harmony.GetAllPatchedMethods())
            {
                bool quad = patched.DeclaringType == typeof(PQ);
                if (!quad && patched != (MethodBase)s_updateQuadsInit)
                    continue;
                Patches info = Harmony.GetPatchInfo(patched);
                if (info == null)
                    continue;
                string owner = quad ? FirstForeign(info.Prefixes, true) ?? FirstForeign(info.Postfixes, true)
                                      ?? FirstForeign(info.Transpilers, true) ?? FirstForeign(info.Finalizers, true)
                                    : FirstForeign(info.Prefixes, false) ?? FirstForeign(info.Postfixes, true)
                                      ?? FirstForeign(info.Transpilers, true) ?? FirstForeign(info.Finalizers, true);
                if (owner != null)
                    return owner + " (" + patched.DeclaringType?.Name + "." + patched.Name + ")";
            }
            return null;
        }

        private static string FirstForeign(IEnumerable<Patch> patches, bool any)
        {
            foreach (Patch p in patches)
                if (!p.owner.StartsWith("Hyperdrive.", StringComparison.Ordinal) && (any || p.PatchMethod.ReturnType != typeof(void)))
                    return p.owner;
            return null;
        }

        // Stock UpdateQuadsInit: create the 6 root quads, subdivide until the quad count is stable for 10 passes, then one
        // UpdateSubdivision pass over the roots with building allowed (the 2 s). The first part runs here as stock; the
        // build pass is left to the job.
        private static bool UpdateQuadsInitPrefix(PQS __instance)
        {
            if (!s_spread || __instance != s_forcing || s_job != null)
                return true;
            string hook = ForeignQuadHook();
            if (hook != null)
            {
                s_spread = false;
                Log.Info("LaunchSitePacing: the terrain quads are also patched by '" + hook + "': forced builds left to stock (one frame, as the game does)");
                return true;
            }
            // The build pass only builds (no further subdivision) when the sphere's frame deadline is past, as on a fresh
            // sphere; anything else is left to stock.
            if (Time.realtimeSinceStartup <= __instance.maxFrameEnd)
                return true;
            try
            {
                s_createQuads(__instance);
                PQ[] quads = __instance.quads;
                if (quads == null || quads.Length != 6)
                    throw new InvalidOperationException("CreateQuads made " + (quads?.Length ?? 0) + " root quads");
                __instance.isThinking = true;
                __instance.quadAllowBuild = false;
                int stable = 0;
                while (stable < 10)
                {
                    int before = __instance.quadCount;
                    for (int i = 5; i >= 0; i--)
                        quads[i].UpdateSubdivisionInit();
                    stable = before >= __instance.quadCount ? stable + 1 : 0;
                }
                __instance.quadAllowBuild = true;
                __instance.isThinking = false;
            }
            catch (Exception e)
            {
                // The subdivision itself failed: stock would have thrown here too. Let the original run from scratch.
                Log.Exception("LaunchSitePacing: deferral of " + __instance.name + " failed, stock build", e);
                s_spread = false;
                return true;
            }
            s_job = new Job { Sphere = __instance };
            s_deferred = __instance;
            return false;
        }

        // Inserted in PQS.StartSphere right after the UpdateQuadsInit call: returns true (StartSphere returns) when that call
        // was deferred; the job finishes the start later.
        private static bool ReturnAfterInit(PQS sphere, bool force)
        {
            if (s_deferred == null || sphere != s_deferred)
                return false;
            s_deferred = null;
            if (s_job != null)
                s_job.Force = force;
            return true;
        }

        private static IEnumerable<CodeInstruction> StartSphereTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count - 1; i++)
            {
                if ((list[i].opcode != OpCodes.Call && list[i].opcode != OpCodes.Callvirt) || !(list[i].operand is MethodInfo m) || m != s_updateQuadsInit)
                    continue;
                s_tailChecks++;
                Label resume = generator.DefineLabel();
                list[i + 1].labels.Add(resume);
                list.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, s_tailCheck),
                    new CodeInstruction(OpCodes.Brfalse, resume),
                    new CodeInstruction(OpCodes.Ret),
                });
                i += 5;
            }
            return list;
        }

        // The forced launch site wait (stock: WaitForSeconds(2)) also waits for the deferred build.
        private static void LaunchSitesMoveNextPostfix(object __instance, bool __result)
        {
            if (!__result || s_job == null)
                return;
            if (s_launchSitesCurrent(__instance) is WaitForSeconds wait)
                s_launchSitesCurrent(__instance) = new BuildWait(s_waitSeconds != null ? s_waitSeconds(wait) : 2f);
        }

        // ------------------------------------------------------------------ the build, a slice per frame

        protected internal override void OnUpdate()
        {
            Job job = s_job;
            if (job == null)
                return;
            float budget = s_buildBudget > 0f ? s_buildBudget : FastLoader.FrameBudgetOverride > 0f ? FastLoader.FrameBudgetOverride : s_fallbackBudget;
            long start = Stopwatch.GetTimestamp();
            long limit = start + (long)(budget * Stopwatch.Frequency);
            try
            {
                // The rest of the sphere start (cities, the ocean sphere: ~80 ms) gets a frame of its own.
                if (!job.Building)
                {
                    Complete(job, null);
                    return;
                }
                job.Sphere.isThinking = true;
                job.Building = Step(job, limit);
                job.Sphere.isThinking = false;
                job.Frames++;
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (ms > job.MaxSliceMs)
                    job.MaxSliceMs = ms;
            }
            catch (Exception e)
            {
                Log.Exception("LaunchSitePacing: spread build of " + job.Sphere.name + " failed, finishing it at once", e);
                s_spread = false;
                Finish("error");
            }
        }

        // Same visiting order and the same per-quad work as the stock recursive pass (roots 5..0, children 0..3, the
        // quad's onUpdate after its children). Leaves run the stock PQ.UpdateSubdivision (visibility, build, edges);
        // subdivided quads that stock would collapse do too. Returns false when the pass is over. limit 0: no limit.
        private static bool Step(Job job, long limit)
        {
            PQS sphere = job.Sphere;
            while (true)
            {
                if (job.Depth == 0)
                {
                    if (job.Root < 0)
                        return false;
                    if (limit > 0 && IsHeavy(sphere.quads[job.Root]) && job.SliceVisits > 0)
                    {
                        job.SliceVisits = 0;
                        return true;
                    }
                    Visit(job, sphere.quads[job.Root--]);
                    job.SliceVisits++;
                }
                else
                {
                    int top = job.Depth - 1;
                    PQ quad = job.Nodes[top];
                    int child = job.Next[top];
                    if (child < 4)
                    {
                        PQ sub = quad.subNodes[child];
                        // A quad that subdivides builds its four children at once (~45 ms): it starts a slice of its own.
                        if (limit > 0 && sub && IsHeavy(sub) && job.SliceVisits > 0)
                        {
                            job.SliceVisits = 0;
                            return true;
                        }
                        job.Next[top] = child + 1;
                        if (sub)
                        {
                            Visit(job, sub);
                            job.SliceVisits++;
                        }
                    }
                    else
                    {
                        job.Nodes[top] = null;
                        job.Depth = top;
                        if (quad.onUpdate != null)
                            quad.onUpdate(quad);
                    }
                }
                if (limit > 0 && Stopwatch.GetTimestamp() >= limit)
                {
                    job.SliceVisits = 0;
                    return true;
                }
            }
        }

        // A leaf below the minimum level: stock UpdateSubdivision subdivides it and builds its children.
        private static bool IsHeavy(PQ quad) => !quad.isSubdivided && quad.subdivision < quad.sphereRoot.minLevel;

        private static void Visit(Job job, PQ quad)
        {
            if (!quad.isSubdivided)
            {
                long t0 = Stopwatch.GetTimestamp();
                quad.UpdateSubdivision();
                job.Leaves++;
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (ms > job.SlowestMs)
                {
                    job.SlowestMs = ms;
                    job.Slowest = quad;   // named in the final log line: no string built per frame
                }
                return;
            }
            PQS root = quad.sphereRoot;
            quad.UpdateTargetRelativity();
            bool outOfTime = Time.realtimeSinceStartup > root.maxFrameEnd;
            bool far = quad.gcDist > root.collapseThresholds[quad.subdivision] * quad.subdivideThresholdFactor;
            if (quad.subdivision > root.maxLevel || (far && !outOfTime) || job.Depth >= job.Nodes.Length)
            {
                quad.UpdateSubdivision();
                return;
            }
            s_outOfTime(quad) = outOfTime;
            quad.isPendingCollapse = far;
            quad.meshRenderer.enabled = false;
            job.Nodes[job.Depth] = quad;
            job.Next[job.Depth] = 0;
            job.Depth++;
        }

        // Runs whatever is left at once (stock behaviour), then the rest of the sphere start.
        private static void Finish(string why)
        {
            Job job = s_job;
            if (job == null)
                return;
            try
            {
                if (job.Building)
                {
                    job.Sphere.isThinking = true;
                    Step(job, 0);
                    job.Sphere.isThinking = false;
                }
                Complete(job, why);
            }
            catch (Exception e)
            {
                Log.Exception("LaunchSitePacing: could not finish the build of " + job.Sphere.name, e);
                job.Sphere.isThinking = false;
                s_job = null;
            }
        }

        // The end of stock StartSphere after UpdateQuadsInit.
        private static void Complete(Job job, string finishedEarly)
        {
            job.Building = false;
            s_job = null;
            s_tailFrame = Time.frameCount;
            PQS sphere = job.Sphere;
            double buildMs = job.Clock.Elapsed.TotalMilliseconds;
            // Stock runs this inside PSystemSetup.ForceInitPQS: the cities oriented by OnPostSetup are not the forced site.
            PQS forcing = s_forcing;
            s_forcing = sphere;
            try
            {
                Tail(sphere, job.Force);
            }
            finally
            {
                s_forcing = forcing;
            }
            Log.Info("LaunchSitePacing: " + sphere.name + " terrain built over " + job.Frames + " frames in "
                     + (buildMs / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + " s (" + job.Leaves + " quads visited, longest slice "
                     + job.MaxSliceMs.ToString("F1", CultureInfo.InvariantCulture) + " ms, longest quad "
                     + job.SlowestMs.ToString("F1", CultureInfo.InvariantCulture) + " ms"
                     + (job.Slowest ? ": " + job.Slowest.name + " level " + job.Slowest.subdivision + (job.Slowest.isSubdivided ? ", subdivided" : "") : "") + ")"
                     + (finishedEarly != null ? ", rest finished at once (" + finishedEarly + ")" : ""));
        }

        private static void Tail(PQS sphere, bool force)
        {
            s_modOnPostSetup(sphere);
            s_modOnSphereStarted(sphere);
            PQS[] children = sphere.ChildSpheres;
            int n = children.Length;
            while (n-- > 0)
            {
                if (!children[n].isStarted || force)
                    s_startSphere(children[n], force);
            }
            sphere.OnReady.Fire(sphere);
            sphere.StartCoroutine(s_updateSphere(sphere));
        }

        private static void StartSpherePrefix(PQS __instance)
        {
            if (s_job != null && s_job.Sphere == __instance)
                Finish("sphere restarted");
        }

        private static void ResetSpherePrefix(PQS __instance)
        {
            if (s_job != null && s_job.Sphere == __instance)
                Finish("sphere reset");
        }

        // ------------------------------------------------------------------ the site is oriented: finish, fingerprint

        private static void CityOrientatePrefix(PQSCity __instance) => BeforeOrientate(__instance.sphere);
        private static void City2OrientatePrefix(PQSCity2 __instance) => BeforeOrientate(__instance.sphere);
        private static void CityOrientatePostfix(PQSCity __instance) => AfterOrientate(__instance.sphere, __instance.transform, __instance.name);
        private static void City2OrientatePostfix(PQSCity2 __instance) => AfterOrientate(__instance.sphere, __instance.transform, __instance.name);

        private static void BeforeOrientate(PQS sphere)
        {
            if (sphere == null)
                return;
            if (s_job != null && s_job.Sphere == sphere)
                Finish("launch site oriented before the end of the build");
            if (s_watch != null && sphere == s_watch && s_forcing == null)
                Log.Info("LaunchSitePacing: fingerprint " + sphere.name + " before orientation: " + Fingerprint(sphere));
        }

        private static void AfterOrientate(PQS sphere, Transform site, string name)
        {
            if (s_watch == null || sphere != s_watch || s_forcing != null || site == null)
                return;
            s_watch = null;
            Vector3 p = site.localPosition;
            Quaternion r = site.localRotation;
            Log.Info("LaunchSitePacing: launch site " + name + " oriented at " + R(p.x) + " " + R(p.y) + " " + R(p.z)
                     + ", rotation " + R(r.x) + " " + R(r.y) + " " + R(r.z) + " " + R(r.w));
        }

        private static string R(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        // Counts and sums over the quad tree: equal fingerprints mean the same quads built with the same vertices.
        internal static string Fingerprint(PQS sphere)
        {
            int nodes = 0, leaves = 0, built = 0, visible = 0;
            double heights = 0.0, verts = 0.0;
            PQ[] quads = sphere.quads;
            if (quads != null)
            {
                for (int i = 0; i < quads.Length; i++)
                    Walk(quads[i], ref nodes, ref leaves, ref built, ref visible, ref heights, ref verts);
            }
            return "quads " + sphere.quadCount + ", nodes " + nodes + ", leaves " + leaves + ", built " + built + ", visible " + visible
                   + ", height sum " + heights.ToString("R", CultureInfo.InvariantCulture) + ", vertex sum " + verts.ToString("R", CultureInfo.InvariantCulture)
                   + ", sphere min/max " + sphere.meshVertMin.ToString("R", CultureInfo.InvariantCulture) + "/" + sphere.meshVertMax.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void Walk(PQ quad, ref int nodes, ref int leaves, ref int built, ref int visible, ref double heights, ref double verts)
        {
            if (!quad)
                return;
            nodes++;
            if (quad.isSubdivided)
            {
                for (int i = 0; i < 4; i++)
                    Walk(quad.subNodes[i], ref nodes, ref leaves, ref built, ref visible, ref heights, ref verts);
                return;
            }
            leaves++;
            if (quad.isVisible)
                visible++;
            if (!quad.isBuilt)
                return;
            built++;
            heights += quad.meshVertMin + quad.meshVertMax;
            Vector3[] v = quad.verts;
            if (v == null)
                return;
            for (int i = 0; i < v.Length; i++)
                verts += (double)v[i].x + v[i].y + v[i].z;
        }
    }
}
