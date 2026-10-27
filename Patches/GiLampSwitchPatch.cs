using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using TraceSix.Source;
using UnityEngine;

namespace TraceSix.Patches
{
    // A lamp switched by the game: shot out (GameWorld.ChangeLampState -> Destroyed), a light switch, the power, the
    // time of day. The light cells around it learn it the frame it happens (GiLamps.Announced). Its Lights are taken
    // before the call: a destroyed lamp's list is emptied inside it (method_8).
    internal class GiLampSwitchPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.DeclaredMethod(typeof(LampController), nameof(LampController.Switch), new[] { typeof(Turnable.EState) });

        [PatchPrefix]
        private static void Prefix(LampController __instance, out Light[] __state)
        {
            __state = __instance.Lights;
        }

        [PatchPostfix]
        private static void Postfix(LampController __instance, Light[] __state)
        {
            try
            {
                Turnable.EState s = __instance.LampState;
                bool on = s == Turnable.EState.On || s == Turnable.EState.TurningOn || s == Turnable.EState.ConstantFlickering;
                GiLamps.Announced(__state, on, s == Turnable.EState.Destroyed ? "was shot out" : on ? "was switched on" : "was switched off");
            }
            catch (System.Exception) { }
        }
    }
}
