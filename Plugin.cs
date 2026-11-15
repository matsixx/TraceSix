using BepInEx;
using BepInEx.Logging;
using TraceSix.Patches;
using TraceSix.Source;

namespace TraceSix
{
    // Global illumination for SPT: a voxel copy of the world (VoxelScene), light probes that trace through it
    // (GiProbes), and the pass that lights the picture from them in place of the game's flat ambient
    // (GiCamera, GameAmbient); hardware ray tracing beside it (GiRt). The raid's lifecycle is GiRaid.
    [BepInPlugin("com.matsix.tracesix", "TraceSix", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;

        // Set-up and per-raid detail (counts, contents, what is attached where): only with Debug Log on. Warnings, errors
        // and notices that something is off go out always.
        internal static void LogDetail(string message)
        {
            if (GiConfig.DebugLog != null && GiConfig.DebugLog.Value) MyLog.LogInfo(message);
        }

        private void Awake()
        {
            MyLog = Logger;
            GiConfig.Bind(Config);
            new GiWeaponSpawnPatch().Enable();
            try { new GiRaidStartTestPatch().Enable(); }
            catch (System.Exception e) { MyLog.LogWarning("[TraceSix] Raid start test: no hook (" + e.Message + ")."); }
            try { new GiLampSwitchPatch().Enable(); }
            catch (System.Exception e) { MyLog.LogWarning("[TraceSix] Lamps switched by the game are not seen at once (" + e.Message + "): their light fades at the usual pace."); }
            try { new GiNearInteriorPatch().Enable(); }
            catch (System.Exception e) { MyLog.LogWarning("[TraceSix] Interior mask: no hook (" + e.Message + "): the game's interior edge can still cut across the gun."); }
            try { new GiTimeOfDayPatch().Enable(); new GiAmbientShPatch().Enable(); }
            catch (System.Exception e) { MyLog.LogWarning("[TraceSix] Sky light: no hook (" + e.Message + "): the game's fake sun bounce stays in it."); }
            gameObject.AddComponent<GiRaid>();
            MyLog.LogInfo("TraceSix loaded");
        }
    }
}
