using BepInEx;
using BepInEx.Logging;
using TraceSix.Patches;
using TraceSix.Source;

namespace TraceSix
{
    // Global illumination for SPT: a voxel copy of the world (VoxelScene), light probes that trace through it
    // (GiProbes), and the pass that lights the picture from them in place of the game's flat ambient
    // (GiCamera, GameAmbient); hardware ray tracing beside it (GiRt). The raid's lifecycle is GiRaid.
    [BepInPlugin("com.matsix.tracesix", "TraceSix", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;

        private void Awake()
        {
            MyLog = Logger;
            GiConfig.Bind(Config);
            new GiWeaponSpawnPatch().Enable();
            try { new GiLampSwitchPatch().Enable(); }
            catch (System.Exception e) { MyLog.LogWarning("[TraceSix] Lamps switched by the game are not seen at once (" + e.Message + "): their light fades at the usual pace."); }
            gameObject.AddComponent<GiRaid>();
            MyLog.LogInfo("TraceSix loaded");
        }
    }
}
