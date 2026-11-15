using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using TraceSix.Source;
using UnityEngine.Rendering;

namespace TraceSix.Patches
{
    // AmbientLight.InitStencilBuffer: the interior mask's depth copied and its target set - before its volumes are drawn
    // (GiNearInterior).
    internal class GiNearInteriorPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(AmbientLight), nameof(AmbientLight.InitStencilBuffer));

        [PatchPostfix]
        private static void Postfix(CommandBuffer commandBuffer)
        {
            GiNearInterior.Append(commandBuffer);
        }
    }
}
