using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Physics
{
    /// <summary>
    /// FlightIntegrator.Integrate runs for every part on every physics step and hands the part's accumulated force and
    /// torque to PhysX with Rigidbody.AddForce(Vector3) / AddTorque(Vector3), even when both are zero, which is the case
    /// for almost every part (tanks, structure). A zero force has no effect and, as documented by Unity, does not wake the
    /// rigidbody, so skipping the native call when the vector is exactly zero is strictly equivalent.
    /// IL: "callvirt Rigidbody::AddForce(Vector3)" consumes [rigidbody, vector] and returns nothing; the replacement
    /// "call ZeroForceGuard::AddForceIfNonZero(Rigidbody, Vector3)" consumes the same stack. Same for AddTorque.
    /// Gravity (AddForce with ForceMode.Acceleration, another overload) and per-position forces are not touched.
    /// With ModularFlightIntegrator (FAR, Kopernicus) this stays on: MFI runs the stock Integrate unless a mod took that step
    /// (see MfiHooks; logged on the first flight).
    /// </summary>
    internal sealed class ZeroForceGuard : Subsystem
    {
        private static readonly MethodInfo AddForce = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForce), new[] { typeof(Vector3) });
        private static readonly MethodInfo AddTorque = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddTorque), new[] { typeof(Vector3) });
        private static readonly MethodInfo AddForceHelper = AccessTools.Method(typeof(ZeroForceGuard), nameof(AddForceIfNonZero));
        private static readonly MethodInfo AddTorqueHelper = AccessTools.Method(typeof(ZeroForceGuard), nameof(AddTorqueIfNonZero));

        private static int s_replaced;

        public override string Name => "ZeroForceGuard";

        public override string[] IncompatibleAssemblies => new[] { "ksp_plugin_adapter" };
        private bool _mfiReported;

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene != GameScenes.FLIGHT || _mfiReported || !MfiHooks.Installed)
                return;
            _mfiReported = true;
            MfiHooks.Report(Name, "integrateOverride", "Integrate");
        }
        public override string[] KspcfEquivalents => new[] { "FlightIntegratorPerf" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void OnEnable()
        {
            if (AddForce == null || AddTorque == null || AddForceHelper == null || AddTorqueHelper == null)
                throw new SubsystemUnavailableException("Rigidbody.AddForce/AddTorque(Vector3) not found");

            s_replaced = 0;
            Patch(AccessTools.Method(typeof(FlightIntegrator), "Integrate"), transpiler: nameof(Transpiler));
            if (s_replaced == 0)
                throw new System.InvalidOperationException("no AddForce/AddTorque(Vector3) call found in FlightIntegrator.Integrate (game version mismatch?)");
            Log.Info("ZeroForceGuard: " + s_replaced + " force/torque calls in FlightIntegrator.Integrate now skip zero vectors");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo method)
                {
                    if (method == AddForce)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AddForceHelper;
                        s_replaced++;
                    }
                    else if (method == AddTorque)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AddTorqueHelper;
                        s_replaced++;
                    }
                }
                yield return instruction;
            }
        }

        private static void AddForceIfNonZero(Rigidbody rb, Vector3 force)
        {
            if (force.x != 0f || force.y != 0f || force.z != 0f)
                rb.AddForce(force);
        }

        private static void AddTorqueIfNonZero(Rigidbody rb, Vector3 torque)
        {
            if (torque.x != 0f || torque.y != 0f || torque.z != 0f)
                rb.AddTorque(torque);
        }
    }
}
