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
    /// FlightIntegrator.UpdateMassStats assigns Rigidbody.mass and Rigidbody.centerOfMass for every part on every physics
    /// step, even when nothing changed. Each assignment is a native call into PhysX. This transpiler swaps those two
    /// setter calls for helpers that only assign when the value differs from the current one.
    /// IL: "callvirt Rigidbody::set_mass(float32)" consumes [rigidbody, value] and returns nothing; the replacement
    /// "call MassUpdateGuard::SetMassIfChanged(Rigidbody, float32)" consumes the same stack and returns nothing, so the
    /// rest of the method is unaffected. Same for set_centerOfMass(Vector3).
    /// </summary>
    internal sealed class MassUpdateGuard : Subsystem
    {
        private static readonly MethodInfo SetMass = AccessTools.PropertySetter(typeof(Rigidbody), nameof(Rigidbody.mass));
        private static readonly MethodInfo SetCenterOfMass = AccessTools.PropertySetter(typeof(Rigidbody), nameof(Rigidbody.centerOfMass));
        private static readonly MethodInfo SetMassHelper = AccessTools.Method(typeof(MassUpdateGuard), nameof(SetMassIfChanged));
        private static readonly MethodInfo SetCenterOfMassHelper = AccessTools.Method(typeof(MassUpdateGuard), nameof(SetCenterOfMassIfChanged));

        private static int s_replaced;

        public override string Name => "MassUpdateGuard";

        // Off by default: it moves the temperatures slightly (+0.16 K measured in a modded install).
        public override bool EnabledByDefault => false;

        public override string[] IncompatibleAssemblies => new[] { "ksp_plugin_adapter" };
        public override string[] KspcfEquivalents => new[] { "FlightIntegratorPerf" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void OnEnable()
        {
            if (SetMass == null || SetCenterOfMass == null || SetMassHelper == null || SetCenterOfMassHelper == null)
                throw new SubsystemUnavailableException("Rigidbody setters not found");

            s_replaced = 0;
            Patch(AccessTools.Method(typeof(FlightIntegrator), "UpdateMassStats"), transpiler: nameof(Transpiler));
            if (s_replaced == 0)
                throw new System.InvalidOperationException("no Rigidbody.mass/centerOfMass assignment found in UpdateMassStats (game version mismatch?)");
            Log.Info("MassUpdateGuard: " + s_replaced + " rigidbody assignments in FlightIntegrator.UpdateMassStats now skip unchanged values");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo method)
                {
                    if (method == SetMass)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = SetMassHelper;
                        s_replaced++;
                    }
                    else if (method == SetCenterOfMass)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = SetCenterOfMassHelper;
                        s_replaced++;
                    }
                }
                yield return instruction;
            }
        }

        private static void SetMassIfChanged(Rigidbody rb, float mass)
        {
            if (rb.mass != mass)
                rb.mass = mass;
        }

        private static void SetCenterOfMassIfChanged(Rigidbody rb, Vector3 centerOfMass)
        {
            Vector3 current = rb.centerOfMass;
            if (current.x != centerOfMass.x || current.y != centerOfMass.y || current.z != centerOfMass.z)
                rb.centerOfMass = centerOfMass;
        }
    }
}
