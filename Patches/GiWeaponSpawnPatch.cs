using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using TraceSix.Source;

namespace TraceSix.Patches
{
    // A weapon coming into someone's hands. Its lights are the lights that move, and a bot's did not exist when the
    // map's lights were collected - nothing else says so short of searching the whole scene again and again.
    // FirearmController.Spawn runs for the player and for every bot, at the raid's start, on a weapon switch and when
    // a weapon is taken up again (FogSix registers its weapon lights at the same place).
    internal class GiWeaponSpawnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.Spawn), new[] { typeof(float), typeof(System.Action) });

        [PatchPostfix]
        private static void Postfix(EFT.Player.FirearmController __instance)
        {
            try { GiLamps.RegisterWeapon(__instance.WeaponRoot); }
            catch (System.Exception) { }
        }
    }
}
