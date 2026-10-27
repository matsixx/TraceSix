using System.Collections.Generic;
using UnityEngine;

namespace TraceSix.Source
{
    // The game's own indirect light is one flat term: AmbientLight's "Custom Ambient" pass adds sky SH x surface
    // colour to every pixel. The probe light replaces that term, so it has to be off while the probes light
    // the picture. The pass reads the SH as shader globals (_SHAr.. _SHC); a value set on its MATERIAL takes
    // precedence over a global, so zeros on the material switch the term off and leave the rest of the pass
    // (reflections, the fog fill) alone, whichever shader the material runs (the game's or AOSix's port).
    //
    // The material is shared by every camera the game lights (scopes too), so GiCamera zeroes it only around the
    // main camera's render. A value once set on a material cannot be unset: from then on its copy has to
    // follow the globals, which Sync() does every frame.
    internal static class GameAmbient
    {
        private static readonly int[] Ids =
        {
            Shader.PropertyToID("_SHAr"), Shader.PropertyToID("_SHAg"), Shader.PropertyToID("_SHAb"),
            Shader.PropertyToID("_SHBr"), Shader.PropertyToID("_SHBg"), Shader.PropertyToID("_SHBb"), Shader.PropertyToID("_SHC")
        };

        private static bool _zeroed, _logged;
        private static AmbientLight[] _owners = new AmbientLight[0];
        private static float _nextFind;
        private static readonly List<Material> _touched = new List<Material>();

        // Before the main camera renders.
        public static void Zero()
        {
            Collect();
            foreach (Material m in _touched)
            {
                if (m == null) continue;
                foreach (int id in Ids) m.SetVector(id, Vector4.zero);
            }
            _zeroed = true;
        }

        // After the main camera has rendered.
        public static void Restore()
        {
            if (_zeroed) Sync();
        }

        // Every frame: materials this has ever written to carry the game's current values.
        public static void Sync()
        {
            _zeroed = false;
            for (int i = _touched.Count - 1; i >= 0; i--)
            {
                Material m = _touched[i];
                if (m == null) { _touched.RemoveAt(i); continue; }
                foreach (int id in Ids) m.SetVector(id, Shader.GetGlobalVector(id));
            }
        }

        // Finds the AmbientLight objects: ONE whole-scene search, run by GiRaid while the raid is still loading.
        // Never on a timer: FindObjectsOfType walks every object in the scene.
        public static void Locate()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _owners = UnityEngine.Object.FindObjectsOfType<AmbientLight>();
            _nextFind = Time.unscaledTime + 20f;
            Plugin.MyLog.LogInfo("[TraceSix] Game ambient: " + _owners.Length + " AmbientLight found (the scene search took "
                + sw.Elapsed.TotalMilliseconds.ToString("F1") + " ms).");
        }

        // The materials of the known AmbientLights. The game recreates a material whenever AmbientLight
        // initialises, so the field is read every frame (no search).
        private static void Collect()
        {
            bool alive = false;
            foreach (AmbientLight o in _owners)
            {
                if (o == null) continue;
                alive = true;
                Material m = o._screenAmbientMaterial;
                if (m != null && !_touched.Contains(m))
                {
                    _touched.Add(m);
                    if (!_logged)
                    {
                        _logged = true;
                        Plugin.MyLog.LogInfo("[TraceSix] Game ambient: sky term of '" + m.shader.name + "' is replaced by the probe light on the main camera.");
                    }
                }
            }
            // No AmbientLight known (GI switched on mid-raid, or the game replaced it): search again, rarely.
            if (!alive && Time.unscaledTime >= _nextFind) Locate();
        }
    }
}
