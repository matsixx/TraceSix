using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using TraceSix.Source;

namespace TraceSix.Patches
{
    // The moment the game says the raid has started (Fika calls it too): the start-up report's zero (GiStartup), and with
    // Debug Log what is ready then, to compare with when the wait loop (GiRaid.WaitForRaid) reads the map.
    internal class GiRaidStartTestPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EFT.GameWorld), nameof(EFT.GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix()
        {
            GiRaid.LogRaidStartTest("OnGameStarted hook");
        }
    }
}
