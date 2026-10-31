using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;

namespace Hyperdrive.Fixes
{
    /// <summary>
    /// Stock leaks that slow the game down, fixed without changing what live objects see.
    ///  - PartSet: every resource set subscribes to GameEvents.onPartResourceFlowStateChange / FlowModeChange and only
    ///    unsubscribes in its finalizer, which never runs because GameEvents keeps it alive. Each rebuild of a vessel's
    ///    resource sets (part count change, docking, staging, every resource list change of a loaded vessel) creates one
    ///    or two sets per part and none ever goes away: every flow event is then handled by all the sets ever built.
    ///    Measured in a modded install: a 310-part vessel whose tanks are reconfigured by B9PartSwitch on load piled up
    ///    63,000 sets and ~100 million "[PartSet]: Failed to add Resource" calls, freezing the flight scene load for a
    ///    minute. Here, right after PartSet.BuildPartSets, the sets it replaced are unsubscribed, but only those no longer
    ///    referenced by the vessel or by any part of any loaded vessel: nothing can reach them any more, so nothing can
    ///    observe the event handling they would have done. The part they would have refreshed is refreshed by its live
    ///    set, which handles the same event.
    /// Stands down with KSPCommunityFixes (its MemoryLeaks patch covers this).
    /// </summary>
    internal sealed class LeakFixes : Subsystem
    {
        private static MethodInfo s_onFlowState, s_onFlowMode;
        private static readonly List<PartSet> s_old = new List<PartSet>(1024);
        private static readonly HashSet<PartSet> s_oldSeen = new HashSet<PartSet>();
        private static readonly HashSet<PartSet> s_live = new HashSet<PartSet>();
        private static bool s_inside;
        private static long s_unhooked;
        private static bool s_failed;

        public override string Name => "LeakFixes";

        public override bool YieldOnSharedTargets => true;

        public static long UnhookedPartSets => s_unhooked;

        protected internal override void OnEnable()
        {
            s_onFlowState = AccessTools.Method(typeof(PartSet), "OnFlowStateChange");
            s_onFlowMode = AccessTools.Method(typeof(PartSet), "OnFlowModeChange");
            if (s_onFlowState == null || s_onFlowMode == null)
                throw new SubsystemUnavailableException("PartSet flow event handlers not found (game version mismatch?)");
            MethodInfo build = AccessTools.FirstMethod(typeof(PartSet), m => m.Name == "BuildPartSets" && m.GetParameters().Length == 4);
            Patch(build, prefix: nameof(BuildPartSetsPrefix), postfix: nameof(BuildPartSetsPostfix), finalizer: nameof(BuildPartSetsFinalizer));
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (s_unhooked > 0)
                Log.Info("LeakFixes: " + s_unhooked + " replaced resource sets unsubscribed from flow events so far");
        }

        // __state: this call is the outermost one (a nested rebuild is left to its caller's pass).
        private static void BuildPartSetsPrefix(List<Part> parts, Vessel v, out bool __state)
        {
            __state = !s_inside && !s_failed;
            if (!__state)
                return;
            s_inside = true;
            s_old.Clear();
            s_oldSeen.Clear();
            if (v != null && v.crossfeedSets != null)
                for (int i = 0; i < v.crossfeedSets.Count; i++)
                    AddOld(v.crossfeedSets[i]);
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                {
                    Part p = parts[i];
                    if (p == null)
                        continue;
                    AddOld(p.crossfeedPartSet);
                    AddOld(p.simulationCrossfeedPartSet);
                }
        }

        private static void AddOld(PartSet set)
        {
            if (set != null && s_oldSeen.Add(set))
                s_old.Add(set);
        }

        // Runs even when the stock method throws: the flag must never stay set.
        private static Exception BuildPartSetsFinalizer(Exception __exception, bool __state)
        {
            if (__state)
            {
                s_inside = false;
                s_old.Clear();
                s_oldSeen.Clear();
            }
            return __exception;
        }

        private static void BuildPartSetsPostfix(List<Part> parts, Vessel v, bool __state)
        {
            if (!__state || s_old.Count == 0)
                return;
            try
            {
                // Everything still reachable through the game's own references stays subscribed, exactly as in stock.
                s_live.Clear();
                if (v != null)
                {
                    AddLive(v.resourcePartSet);
                    AddLive(v.simulationResourcePartSet);
                    if (v.crossfeedSets != null)
                        for (int i = 0; i < v.crossfeedSets.Count; i++)
                            AddLive(v.crossfeedSets[i]);
                }
                AddLiveParts(parts);
                List<Vessel> loaded = FlightGlobals.fetch != null ? FlightGlobals.VesselsLoaded : null;
                if (loaded != null)
                    for (int i = 0; i < loaded.Count; i++)
                    {
                        Vessel other = loaded[i];
                        if (other == null)
                            continue;
                        AddLive(other.resourcePartSet);
                        AddLive(other.simulationResourcePartSet);
                        if (other.crossfeedSets != null)
                            for (int k = 0; k < other.crossfeedSets.Count; k++)
                                AddLive(other.crossfeedSets[k]);
                        AddLiveParts(other.parts);
                    }

                for (int i = 0; i < s_old.Count; i++)
                {
                    PartSet set = s_old[i];
                    if (s_live.Contains(set))
                        continue;
                    GameEvents.onPartResourceFlowStateChange.Remove((EventData<GameEvents.HostedFromToAction<PartResource, bool>>.OnEvent)
                        Delegate.CreateDelegate(typeof(EventData<GameEvents.HostedFromToAction<PartResource, bool>>.OnEvent), set, s_onFlowState));
                    GameEvents.onPartResourceFlowModeChange.Remove((EventData<GameEvents.HostedFromToAction<PartResource, PartResource.FlowMode>>.OnEvent)
                        Delegate.CreateDelegate(typeof(EventData<GameEvents.HostedFromToAction<PartResource, PartResource.FlowMode>>.OnEvent), set, s_onFlowMode));
                    s_unhooked++;
                }
            }
            catch (Exception e)
            {
                // Never break the stock rebuild: stop fixing for the session, sets stay subscribed as in stock.
                s_failed = true;
                Log.Exception("LeakFixes: could not release replaced resource sets, leaving them as stock does", e);
            }
            finally
            {
                s_old.Clear();
                s_oldSeen.Clear();
                s_live.Clear();
            }
        }

        private static void AddLive(PartSet set)
        {
            if (set != null)
                s_live.Add(set);
        }

        private static void AddLiveParts(List<Part> parts)
        {
            if (parts == null)
                return;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p == null)
                    continue;
                AddLive(p.crossfeedPartSet);
                AddLive(p.simulationCrossfeedPartSet);
            }
        }
    }
}
