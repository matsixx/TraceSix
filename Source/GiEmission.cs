using System.Collections.Generic;
using UnityEngine;

namespace TraceSix.Source
{
    // (0.24.155) "Glowing Surfaces Give Light": the game's glowing materials as light sources - in the voxel world
    // (TraceSixVoxelize: a glow per voxel, read where a light cell's ray ends) and in the ray tracing (GiRt: a glow tile per
    // material in the atlas, read where a traced ray hits).
    //   The glow, decoded from the game's compiled deferred passes (memory: gisix-emissive-survey):
    //  - p0/Reflective/Bumped Emissive Specular SMap: _EmissionMap x _EmissiveColor x _EmissionPower, faded in over the
    //    reflection by _EmissionVisibility, x 0.5 (_SpecVals.x + _SpecVals.y f) with f = 0.5 (1 - N.V)^2 - over the
    //    hemisphere f averages 1/12, and the light a surface sends every way is what lights the world round it;
    //  - ...Emissive Specular and Custom/TextureGlitch: the same without the colour;
    //  - ...Animated Emissive Specular SMap: the same, + two scrolling layers (_EmissionMapAnim1 / 2 x _EmAnim1 / 2Color x
    //    its alpha), taken where they stand unscrolled;
    //  - p0/Cutout/Bumped Diffuse Emissive: _EmissionMap x _EmissionColor x the same specular scale (its own defaults);
    //  - Standard: _EmissionMap x _EmissionColor, where _EMISSION is on.
    // Decals have an _EmissionColor too: it darkens their ambient (their light-buffer output is exp(-x)), not a glow.
    // The game draws in gamma space: these are its light buffer's own numbers, added to albedo x light as it adds them.
    //   Which meshes. Not a lamp's own fixture: its LampController switches that glow with the lamp, and the lamp's light
    // is the light it gives. Nor a mesh with one of the game's lights within "Glow Lamp Distance" of it: the lights that
    // belong to a glowing surface sit on it (Streets' ceiling stripes, Labs' panels, Ground Zero's office lights: 0.0 -
    // 0.5 m from the glowing mesh), where Factory's window slabs have the hall's lamps 1 - 3 m off (the survey).
    internal static class GiEmission
    {
        // A material's glow: up to three textures at its main texture's coordinates (the 2nd and 3rd through their own
        // tiling / offset), each x a colour that carries every factor of the game's formula. peak = the most a channel of
        // it reaches with the textures at 1 (the ray tracing's glow tile is drawn over it).
        internal sealed class Glow
        {
            public Texture tex0, tex1, tex2;   // null = white, as the game's shaders default them
            public Vector4 col0, col1, col2;
            public Vector4 st1 = new Vector4(1f, 1f, 0f, 0f), st2 = new Vector4(1f, 1f, 0f, 0f);
            public float peak;
        }

        private static readonly Dictionary<int, Glow> _byMat = new Dictionary<int, Glow>();
        // every light of the map (GiLamps.Places), in cells of 2 m
        private const float Cell = 2f;
        private static readonly Dictionary<long, List<Vector3>> _grid = new Dictionary<long, List<Vector3>>();
        private static int _lights;
        private static float _gap;
        private static bool _ready;
        // for the raid's log: the largest glowing meshes taken and left out, and how many
        private struct Seen { public float score; public string text; }
        private static readonly List<Seen> _taken = new List<Seen>(), _left = new List<Seen>();
        private static int _nTaken, _nNear, _nFixture;
        private static readonly int IdOn = Shader.PropertyToID("_GiGlowOn");

        // The bundle can draw the glow into the voxels and read it (the glow is built whenever it can be)
        public static bool Built => GiAssets.Glow;
        // ... and it lights the world (a live switch: the shaders read _GiGlowOn)
        public static bool On => GiConfig.Glow.Value && Built && !GiTest.NoGlow;

        public static void SetSwitch() => Shader.SetGlobalFloat(IdOn, On ? 1f : 0f);

        public static void Reset()
        {
            _byMat.Clear();   // (holds the map's textures: let go with the map)
            _grid.Clear();
            _taken.Clear();
            _left.Clear();
            _nTaken = _nNear = _nFixture = _lights = 0;
            _ready = false;
        }

        // At the raid's set-up, after GiLamps.Collect and before the meshes are read.
        public static void Begin()
        {
            Reset();
            _gap = Mathf.Max(0f, GiConfig.GlowGap.Value);
            var places = new List<Vector3>();
            GiLamps.Places(places);
            foreach (Vector3 p in places)
            {
                long key = Key(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));
                if (!_grid.TryGetValue(key, out List<Vector3> list)) _grid[key] = list = new List<Vector3>();
                list.Add(p);
            }
            _lights = places.Count;
            _ready = true;
        }

        private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        // One of the map's lights within the gap of the box.
        private static bool LightNear(Bounds b)
        {
            if (_gap <= 0f || _lights == 0) return false;
            Vector3 lo = b.min - Vector3.one * _gap, hi = b.max + Vector3.one * _gap;
            int x0 = Mathf.FloorToInt(lo.x / Cell), y0 = Mathf.FloorToInt(lo.y / Cell), z0 = Mathf.FloorToInt(lo.z / Cell);
            int x1 = Mathf.FloorToInt(hi.x / Cell), y1 = Mathf.FloorToInt(hi.y / Cell), z1 = Mathf.FloorToInt(hi.z / Cell);
            long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (cells > 4096)
            {
                // a box of a map's size: every light instead
                foreach (List<Vector3> list in _grid.Values)
                    foreach (Vector3 p in list) if (Within(b, p)) return true;
                return false;
            }
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        if (_grid.TryGetValue(Key(x, y, z), out List<Vector3> list))
                            foreach (Vector3 p in list) if (Within(b, p)) return true;
            return false;
        }

        private static bool Within(Bounds b, Vector3 p)
        {
            Vector3 lo = b.min, hi = b.max;
            float dx = Mathf.Max(0f, Mathf.Max(lo.x - p.x, p.x - hi.x));
            float dy = Mathf.Max(0f, Mathf.Max(lo.y - p.y, p.y - hi.y));
            float dz = Mathf.Max(0f, Mathf.Max(lo.z - p.z, p.z - hi.z));
            return dx * dx + dy * dy + dz * dz <= _gap * _gap;
        }

        // Whether a mesh's glowing materials give light (its first count materials; b = its box in the world). note: count
        // it for the raid's log (its full-detail copy only - the simpler levels of its LOD group are the same thing).
        public static bool Allowed(Renderer r, List<Material> mats, int count, Bounds b, bool fixture, bool note)
        {
            if (!Built || !_ready) return false;
            Glow best = null;
            Material bestMat = null;
            for (int i = 0; i < count && i < mats.Count; i++)
            {
                Glow g = Of(mats[i]);
                if (g != null && (best == null || g.peak > best.peak)) { best = g; bestMat = mats[i]; }
            }
            if (best == null) return false;
            bool near = !fixture && LightNear(b);
            bool ok = !fixture && !near;
            if (note)
            {
                Vector3 s = b.size;
                float a = Mathf.Max(s.x * s.y, Mathf.Max(s.y * s.z, s.x * s.z));   // its largest face, about
                var seen = new Seen
                {
                    score = best.peak * a,
                    text = "'" + r.name + "' (" + bestMat.name + ", up to " + best.peak.ToString("F2") + ", " + s.x.ToString("F1") + " x " + s.y.ToString("F1")
                           + " x " + s.z.ToString("F1") + " m at " + b.center.ToString("F0") + ")"
                };
                if (ok) { _nTaken++; Keep(_taken, seen); }
                else { if (fixture) _nFixture++; else _nNear++; if (!fixture) Keep(_left, seen); }
            }
            return ok;
        }

        private static void Keep(List<Seen> list, Seen s)
        {
            const int Most = 10;
            if (list.Count >= Most && s.score <= list[list.Count - 1].score) return;
            int at = list.Count;
            while (at > 0 && list[at - 1].score < s.score) at--;
            list.Insert(at, s);
            if (list.Count > Most) list.RemoveAt(list.Count - 1);
        }

        // After the meshes are read.
        public static void Log()
        {
            if (!Built) { Plugin.MyLog.LogInfo("[TraceSix] Glowing surfaces: the tracesix bundle has no glow yet (rebuild it): none give light."); return; }
            var sb = new System.Text.StringBuilder();
            sb.Append("[TraceSix] Glowing surfaces: ").Append(_nTaken).Append(" meshes give light")
              .Append(GiConfig.Glow.Value ? "" : " once Glowing Surfaces Give Light is switched on").Append("; ")
              .Append(_nNear).Append(" are left out, one of the game's ").Append(_lights).Append(" lights within ").Append(_gap.ToString("F2"))
              .Append(" m of them (taken to be lit by it); ").Append(_nFixture).Append(" are lamp fixtures (their lamps are their light).");
            if (_taken.Count > 0)
            {
                sb.Append("\n    The largest that give light:");
                foreach (Seen s in _taken) sb.Append("\n      ").Append(s.text);
            }
            if (_left.Count > 0)
            {
                sb.Append("\n    The largest left out beside a light:");
                foreach (Seen s in _left) sb.Append("\n      ").Append(s.text);
            }
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        //--- the formula ----------------------------------------------------------------------------------------

        // A material's glow, or null (it does not glow, or not visibly).
        public static Glow Of(Material m)
        {
            if (m == null || m.shader == null) return null;
            int id = m.GetInstanceID();
            if (_byMat.TryGetValue(id, out Glow g)) return g;
            try { g = Make(m); }
            catch (System.Exception e)
            {
                Plugin.MyLog.LogWarning("[TraceSix] Glowing surfaces: material '" + m.name + "' could not be read (" + e.GetType().Name + ": " + e.Message + "): taken as not glowing.");
                g = null;
            }
            _byMat[id] = g;
            return g;
        }

        private static Glow Make(Material m)
        {
            string n = m.shader.name;
            var g = new Glow();
            if (n == "Custom/TextureGlitch" || (n.StartsWith("p0/Reflective/Bumped") && n.Contains("Emissive")))
            {
                float k = F(m, "_EmissionPower", 1f) * F(m, "_EmissionVisibility", 1f) * Spec(m, 1.1f, 2f);
                Color c = n == "p0/Reflective/Bumped Emissive Specular SMap" ? C(m, "_EmissiveColor", Color.white) : Color.white;
                g.col0 = Rgb(c) * k;
                if (n.Contains("Animated"))
                {
                    Color a1 = C(m, "_EmAnim1Color", Color.white), a2 = C(m, "_EmAnim2Color", Color.white);
                    g.tex1 = T(m, "_EmissionMapAnim1");
                    g.tex2 = T(m, "_EmissionMapAnim2");
                    g.col1 = Rgb(a1) * (a1.a * k);
                    g.col2 = Rgb(a2) * (a2.a * k);
                    g.st1 = St(m, "_EmissionMapAnim1");
                    g.st2 = St(m, "_EmissionMapAnim2");
                }
            }
            else if (n == "p0/Cutout/Bumped Diffuse Emissive")
                g.col0 = Rgb(C(m, "_EmissionColor", Color.black)) * Spec(m, 0.35f, 2f);
            else if (n == "Standard" || n == "Standard (Specular setup)")
            {
                if (!m.IsKeywordEnabled("_EMISSION")) return null;
                g.col0 = Rgb(C(m, "_EmissionColor", Color.black));
            }
            else return null;
            g.tex0 = T(m, "_EmissionMap");
            Vector4 sum = g.col0 + g.col1 + g.col2;
            g.peak = Mathf.Max(sum.x, Mathf.Max(sum.y, sum.z));
            return g.peak >= 0.01f ? g : null;
        }

        private static float F(Material m, string p, float def) => m.HasProperty(p) ? m.GetFloat(p) : def;
        private static Color C(Material m, string p, Color def) => m.HasProperty(p) ? m.GetColor(p) : def;
        private static Texture T(Material m, string p) => m.HasProperty(p) ? m.GetTexture(p) : null;
        private static Vector4 Rgb(Color c) => new Vector4(Mathf.Max(c.r, 0f), Mathf.Max(c.g, 0f), Mathf.Max(c.b, 0f), 0f);
        private static Vector4 St(Material m, string p)
        {
            if (!m.HasProperty(p)) return new Vector4(1f, 1f, 0f, 0f);
            Vector2 s = m.GetTextureScale(p), o = m.GetTextureOffset(p);
            return new Vector4(s.x, s.y, o.x, o.y);
        }
        // the specular-vals scale over the hemisphere: 0.5 (x + y / 12)
        private static float Spec(Material m, float x, float y)
        {
            Vector4 v = m.HasProperty("_SpecVals") ? m.GetVector("_SpecVals") : new Vector4(x, y, 0f, 0f);
            return Mathf.Max(0f, 0.5f * (v.x + v.y / 12f));
        }
    }
}
