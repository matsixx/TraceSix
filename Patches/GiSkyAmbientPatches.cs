using System.Reflection;
using EFT.Weather;
using HarmonyLib;
using SPT.Reflection.Patching;
using TraceSix.Source;
using UnityEngine.Rendering;

namespace TraceSix.Patches
{
    // ToDController.Update: SetSH inside it lands last frame's SH job; after it, this frame's job is scheduled
    // (GiSkyAmbient rebuilds that job's fake sun bounce from the same inputs).
    internal class GiTimeOfDayPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.DeclaredMethod(typeof(ToDController), nameof(ToDController.Update));

        [PatchPrefix]
        private static void Prefix() => GiSkyAmbient.TimeOfDayUpdateStarting();

        [PatchPostfix]
        private static void Postfix(ToDController __instance)
        {
            GiSkyAmbient.TimeOfDayUpdateEnded();
            try { GiSkyAmbient.RebuildScheduledBounce(__instance); }
            catch (System.Exception) { }
        }
    }

    // AmbientLight.SetSH: the game's sky SH, as it goes into the globals.
    internal class GiAmbientShPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.DeclaredMethod(typeof(AmbientLight), nameof(AmbientLight.SetSH), new[] { typeof(SphericalHarmonicsL2) });

        [PatchPostfix]
        private static void Postfix(SphericalHarmonicsL2 sh)
        {
            try { GiSkyAmbient.ShSet(sh); }
            catch (System.Exception) { }
        }
    }
}
