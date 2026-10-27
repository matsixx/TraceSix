using System.IO;
using UnityEngine;

namespace TraceSix.Source
{
    // Shaders come from the `tracesix` bundle next to the DLL (Assets/tracesix), picked by canary property so
    // a renamed shader still resolves. Kept loaded: live materials use them.
    internal static class GiAssets
    {
        private static bool _tried;
        public static bool LightVis;   // (0.24.115) the copy of the light pass can lay the ray-traced lamp shadows in (its V2)
        public static bool LampRestir; // (0.24.154) the probe shader has the ReSTIR DI lamp choice (RTLAMPRIS / RTLAMPSEL / RTLAMPRESFIN; marker _GISixLampReSTIR)
        public static bool LampTop3;   // (0.24.153) the probe shader reads two exact lamps + one of the rest per lamp shadow ray (RTLAMPTEMPORAL; marker _GISixLampSplit)
        public static bool Glow;       // (0.24.155) the probe shader adds glowing surfaces' light (the voxels' and the ray-traced hits'; marker _GISixGlow)
        public static Shader Glass;    // (0.24.117) TraceSix's glass (GiGlass)
        public static Shader Voxelize, Debug, Probes, Light;   // Light: (0.24.114) the copy of the game's light pass (GiLightPass)

        public static bool Load()
        {
            if (_tried) return Voxelize != null;
            _tried = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(GiAssets).Assembly.Location), "Assets", "tracesix");
            if (!File.Exists(path))
            {
                Plugin.MyLog.LogError("[TraceSix] Shader bundle not found at " + path + " - nothing will run.");
                return false;
            }
            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Plugin.MyLog.LogError("[TraceSix] Failed to load " + path);
                return false;
            }
            foreach (Shader s in bundle.LoadAllAssets<Shader>())
            {
                if (s.FindPropertyIndex("_GISixVoxelCanary") >= 0) Voxelize = s;
                else if (s.FindPropertyIndex("_GISixDebugCanary") >= 0) Debug = s;
                else if (s.FindPropertyIndex("_GISixProbesCanary") >= 0) Probes = s;
                else if (s.FindPropertyIndex("_GISixLightCanary") >= 0) Light = s;
                else if (s.FindPropertyIndex("_GISixGlassCanary") >= 0) Glass = s;
            }
            // (0.24.114) optional: an older bundle has none, and only Own Light Pass waits for it
            if (Light != null && (Light.FindPropertyIndex("_GISixLightV1") < 0 || !Light.isSupported))
            {
                Plugin.MyLog.LogWarning("[TraceSix] The tracesix bundle's copy of the light pass is " + (Light.isSupported ? "older than this DLL" : "not supported on this GPU") + ": the game's own light pass stays in use (no ray-traced direct lamp shadows).");
                Light = null;
            }
            LightVis = Light != null && Light.FindPropertyIndex("_GISixLightV2") >= 0;
            if (Glass != null && (Glass.FindPropertyIndex("_GISixGlassV3") < 0 || !Glass.isSupported)) Glass = null;
            bundle.Unload(false);
            if (Voxelize != null && Voxelize.FindPropertyIndex("_GISixVoxelV16") < 0)   // (V16, 0.24.155: the glow, its own scratch)
            {
                Plugin.MyLog.LogError("[TraceSix] The tracesix bundle is older than this DLL (its voxel shader writes the older voxel format). Rebuild the bundle.");
                Voxelize = null;
            }
            if (Voxelize != null && !Voxelize.isSupported)
            {
                Plugin.MyLog.LogError("[TraceSix] The voxelize shader is not supported on this GPU.");
                Voxelize = null;
            }
            if (Probes != null && Probes.FindPropertyIndex("_GISixProbesV116") < 0)
            {
                Plugin.MyLog.LogError("[TraceSix] The tracesix bundle's probe shader is older than this DLL. Rebuild the bundle.");
                Probes = null;
                Voxelize = null;
            }
            LampTop3 = Probes != null && Probes.FindPropertyIndex("_GISixLampSplit") >= 0;
            LampRestir = Probes != null && Probes.FindPropertyIndex("_GISixLampReSTIR") >= 0;
            Glow = Probes != null && Probes.FindPropertyIndex("_GISixGlow") >= 0;
            if (Probes != null && !Probes.isSupported)
            {
                Plugin.MyLog.LogError("[TraceSix] The probe shader is not supported on this GPU.");
                Probes = null;
            }
            if (Voxelize != null && Probes == null)
                Plugin.MyLog.LogError("[TraceSix] The tracesix bundle has no probe shader (it is older than this DLL): the voxel world builds, the lighting does not change. Rebuild the bundle.");
            Plugin.MyLog.LogInfo("[TraceSix] Shaders: voxelize=" + (Voxelize != null) + " debug=" + (Debug != null && Debug.isSupported) + " probes=" + (Probes != null) + " light pass=" + (Light != null) + " glass=" + (Glass != null));
            return Voxelize != null;
        }

        // Every pass of the three shaders drawn once into a 1 x 1 target, so the graphics driver turns each into
        // GPU code now (in the menu, where nobody sees the hitch) and not the first time the raid uses it. The
        // picture's and the light update's passes are thousands of instructions each, and their first use was
        // the hitch as the light came on at the start of a raid. Unbound inputs read as zero and unbound UAVs
        // take no writes, so nothing is changed anywhere; one pixel is shaded per pass.
        private static bool _warm;
        private static readonly System.Collections.Generic.List<Object> _warmKeep = new System.Collections.Generic.List<Object>();
        public static void WarmUp()
        {
            if (_warm || !Load()) return;
            _warm = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rt = new RenderTexture(1, 1, 24, RenderTextureFormat.ARGBHalf) { name = "TraceSix Warm-Up", hideFlags = HideFlags.DontSave };
            rt.Create();
            // the voxelise pass draws meshes (position, uv, colour) through a geometry shader: one triangle
            var tri = new Mesh { hideFlags = HideFlags.DontSave };
            tri.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            tri.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            tri.colors = new[] { Color.white, Color.white, Color.white };
            tri.triangles = new[] { 0, 1, 2 };
            var cb = new UnityEngine.Rendering.CommandBuffer { name = "TraceSix Warm-Up" };
            cb.SetRenderTarget(rt);
            cb.ClearRenderTarget(true, true, Color.clear);
            int passes = 0;
            foreach (Shader s in new[] { Probes, Voxelize, Debug })
            {
                if (s == null || !s.isSupported) continue;
                var m = new Material(s) { hideFlags = HideFlags.DontSave };
                _warmKeep.Add(m);
                for (int p = 0; p < m.passCount; p++)
                {
                    if (s == Voxelize && p == 0) cb.DrawMesh(tri, Matrix4x4.identity, m, 0, 0);
                    else cb.DrawProcedural(Matrix4x4.identity, m, p, MeshTopology.Triangles, 3);
                    passes++;
                }
            }
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
            _warmKeep.Add(rt);
            _warmKeep.Add(tri);
            Plugin.MyLog.LogInfo("[TraceSix] Shaders made ready ahead of the raid: " + passes + " passes in " + sw.ElapsedMilliseconds + " ms (main thread; the driver may take a little longer).");
        }

        // The warm-up's own objects, a few frames later (the draws hold them until then).
        public static void DropWarmUp()
        {
            foreach (Object o in _warmKeep)
            {
                if (o is RenderTexture r) r.Release();
                if (o != null) Object.Destroy(o);
            }
            _warmKeep.Clear();
        }
    }
}
