using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // (0.24.98) "Sun Shadow Mode" = RayTracedWorld: the map's meshes the ray tracing sees stop drawing into the game's sun
    // shadow (shadowCastingMode Off, each put back as it was), so the map's sun shadows are the rays' alone, and the
    // game's shadow pass draws what the rays cannot see: you, your gun, bots, grass, and alpha-tested meshes (leaves
    // sway there and stand still in the rays: the sun's rays leave those out within the game's shadow distance,
    // instance mask 4). Switched off besides the meshes in the ray-traced scene: their own lower levels of detail (a far
    // object casts through those), the game's shadow-only stand-ins on the world layers, and the far-view stand-ins
    // (left out of the scene; the full-detail meshes they stand in for are in). While a scope renders the game's shadows
    // come back - the scope's picture has none of the rays'. The switch runs 2 ms of a frame at a time.
    // (0.24.99) A shadow-only renderer is DISABLED instead: ShadowCastingMode.Off means "drawn, without a shadow", so Off on
    // the game's shadow-only stand-ins drew them - untextured, white, poking through the real meshes (his screenshots:
    // white strips on concrete blocks' bevels, white rock triangles, a white door). They are nearly all of the map's sun
    // shadow: on Customs 45,901 of them against 400 visible meshes that cast their own.
    // (0.24.116) ... and NOT by Renderer.enabled any more: the game's culling zones (CullingObject._componentsToTurnOff) switch
    // those very components on and off as the player moves from area to area, and a zone turning a stand-in back "on" brought
    // its game shadow back over the rays' one (his doubled shadows). Renderer.forceRenderingOff stops it drawing at all and is
    // a flag the game's culling never touches. Besides, while the switch is on, a sweep re-checks the list a slice a frame and
    // takes out again anything the game switched back on (counted in the log); and the game's shadows come back only when the
    // sun's rays have been missing for half a second (or a scope renders) - not on a gap of a frame or two, and why is logged.
    internal static class GiSunWorld
    {
        private static readonly List<Renderer> _rs = new List<Renderer>();
        private static readonly List<ShadowCastingMode> _was = new List<ShadowCastingMode>();
        private static readonly List<byte> _how = new List<byte>();   // what the switch did: 0 nothing, 1 mode Off, 2 not drawn (forceRenderingOff)
        private static readonly Dictionary<int, int> _index = new Dictionary<int, int>();   // renderer id -> its place in _rs (Log Surface Ahead)
        private static readonly HashSet<int> _keptIds = new HashSet<int>();                // alpha-tested: left with the game's shadow
        private static int _at;                    // _rs[0.._at) are switched off now
        private static bool _listed, _lastWant, _saidList;
        private static int _gen, _wantFrame = -100, _scopeFrame = -100, _scopeTraced = -100;
        private static float _since, _raysSeen = -100f;
        // (0.24.116) the sweep: where it is in the list, what it took out again since the last report, and why the sun's rays
        // were last missing (GiRtCamera.OnPreCull)
        private const int SweepPerFrame = 1000;
        private static int _sweep, _retaken;
        private static float _retakenSaid;
        private static string _missed = "no frame of the main camera";
        private static Coroutine _listing;
        private static readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private static readonly List<Material> _mats = new List<Material>();

        // GiRtCamera.OnPreCull, each frame the sun's buffers go on the light: the mode is RayTracedWorld
        internal static void Want(bool want) { if (want) _wantFrame = Time.frameCount; }
        // (0.24.116) GiRtCamera.OnPreCull, a frame the sun's rays are not traced: why (a constant string: no garbage)
        internal static void Missed(string why) => _missed = why;
        // GiScopeStamp, each frame the scope's camera renders
        internal static void ScopeRendered() => _scopeFrame = Time.frameCount;
        // (0.24.120) GiRtScope, each frame the scope traces its own sun rays ("Ray-Traced Sun Shadows In Scopes"): its picture
        // has the rays' shadows then, so the game's are not handed back for it
        internal static void ScopeTraced() => _scopeTraced = Time.frameCount;

        // An alpha-tested mesh keeps the game's sun shadow - if the game draws it into the shadow at all.
        internal static bool KeepsGameShadow(Renderer r, Material[] src)
        {
            if (r == null || r.shadowCastingMode == ShadowCastingMode.Off) return false;
            if (src != null)
            {
                foreach (Material m in src) if (m != null && VoxelScene.Cutout(m)) return true;
                return false;
            }
            r.GetSharedMaterials(_mats);
            foreach (Material m in _mats) if (m != null && VoxelScene.Cutout(m)) return true;
            return false;
        }

        // The raid is over (restore = false: the renderers go with it), or the voxel world is read anew mid-raid.
        internal static void Forget(bool restore)
        {
            _gen++;
            _listing = null;
            if (restore)
                while (_at > 0)
                {
                    _at--;
                    Undo(_at);
                }
            _rs.Clear();
            _was.Clear();
            _how.Clear();
            _index.Clear();
            _keptIds.Clear();
            _sweep = _retaken = 0;
            _at = 0;
            _listed = false;
            _saidList = false;
        }

        internal static void Tick(MonoBehaviour host)
        {
            int f = Time.frameCount;
            float now = Time.unscaledTime;
            // (0.24.116) a scope rendering gives the game's shadows back at once (its picture has none of the rays'); the sun's
            // rays missing only after half a second of it - a gap of a frame or two kept handing all ~60,000 meshes' shadows
            // back and taking them away again (his log: many times a minute), the shadows doubling while it ran
            // (0.24.120) ... unless the scope traces sun rays of its own
            bool scope = f - _scopeFrame <= 2 && f - _scopeTraced > 2;
            if (f - _wantFrame <= 2) _raysSeen = now;
            bool want = !scope && now - _raysSeen < 0.5f;
            if (want != _lastWant)
            {
                if (!want && _at > 0)
                    Plugin.MyLog.LogInfo("[TraceSix] Sun Shadow Mode RayTracedWorld: giving the game its sun shadow back - "
                        + (scope ? "a scope is rendering." : "the sun's rays have been missing for half a second (" + _missed + ")."));
                _lastWant = want;
                _since = now;
            }
            // back to the game's shadows at once; over to the rays' after a second of wanting it (a scope raised and
            // lowered, a frame the rays miss, the sun at the horizon)
            bool off = want && Time.unscaledTime - _since >= 1f;
            if (off && !_listed)
            {
                if (_listing == null && host != null) _listing = host.StartCoroutine(List(_gen));
                return;
            }
            if (!_listed) return;
            int target = off ? _rs.Count : 0;
            if (_at == target)
            {
                if (off) Sweep(now);
                return;
            }
            bool wasAll = _at == _rs.Count, wasNone = _at == 0;
            _sw.Restart();
            int k = 0;
            while (_at != target && ((++k & 255) != 0 || _sw.Elapsed.TotalMilliseconds < 2.0))
            {
                if (_at < target)
                {
                    _how[_at] = 0;
                    TakeOut(_at);
                    _at++;
                }
                else
                {
                    _at--;
                    Undo(_at);
                }
            }
            if (_at == target && (target == 0 ? !wasNone : !wasAll))
                Plugin.MyLog.LogInfo("[TraceSix] Sun Shadow Mode RayTracedWorld: " + (target == 0
                    ? "the game's sun shadow draws the map again" + (f - _scopeFrame <= 2 && f - _scopeTraced > 2 ? " (a scope is in use)." : ".")
                    : "the map's meshes are out of the game's sun shadow (" + _rs.Count + " of them)."));
        }

        // Takes entry i out of the game's sun shadow: a shadow-only stand-in is not drawn at all (forceRenderingOff - Off would
        // draw it, white), anything else draws without a shadow. True when it changed something.
        private static bool TakeOut(int i)
        {
            Renderer r = _rs[i];
            if (r == null) return false;
            ShadowCastingMode m = r.shadowCastingMode;
            if (m == ShadowCastingMode.ShadowsOnly)
            {
                if (r.forceRenderingOff) return false;
                r.forceRenderingOff = true;
                _how[i] = 2;
                return true;
            }
            if (m == ShadowCastingMode.Off) return false;
            _was[i] = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            _how[i] = 1;
            return true;
        }

        // (0.24.116) While the map is out of the game's sun shadow: a slice of the list a frame, anything back in it taken out
        // again (the game's culling, or anything else, switching it on); reported every half minute when there was any.
        private static void Sweep(float now)
        {
            int n = _rs.Count;
            if (n == 0) return;
            for (int k = 0; k < SweepPerFrame && k < n; k++)
            {
                if (_sweep >= n) _sweep = 0;
                if (TakeOut(_sweep)) _retaken++;
                _sweep++;
            }
            if (_retaken > 0 && now - _retakenSaid > 30f)
            {
                _retakenSaid = now;
                Plugin.MyLog.LogInfo("[TraceSix] Sun Shadow Mode RayTracedWorld: " + _retaken + " meshes had their game shadow back (switched on again by the game) and were taken out again.");
                _retaken = 0;
            }
        }

        // Puts entry i back the way the switch found it - if nothing else has changed it since.
        private static void Undo(int i)
        {
            Renderer r = _rs[i];
            if (r == null) return;
            if (_how[i] == 2 && r.forceRenderingOff) r.forceRenderingOff = false;
            else if (_how[i] == 1 && r.shadowCastingMode == ShadowCastingMode.Off) r.shadowCastingMode = _was[i];
            _how[i] = 0;
        }

        // (0.24.116, Log Surface Ahead) what the switch did with a renderer
        internal static string Status(Renderer r)
        {
            if (r == null) return "";
            int id = r.GetInstanceID();
            if (_keptIds.Contains(id)) return "alpha-tested: keeps the game's sun shadow";
            if (!_index.TryGetValue(id, out int i)) return _listed ? "not in RayTracedWorld's list (its game shadow is left alone)" : "RayTracedWorld's list not made yet";
            string what = _how[i] == 2 ? "not drawn (a shadow-only stand-in)" : _how[i] == 1 ? "drawn without a shadow" : "nothing done (it cast none when switched)";
            return "in RayTracedWorld's list, " + (i < _at ? "taken out: " + what : "not taken out now (the game's shadows are back)");
        }

        private static IEnumerator List(int gen)
        {
            float t0 = Time.realtimeSinceStartup;
            var placed = new HashSet<int>();
            var taken = new HashSet<int>();
            int inScene = 0, lower = 0, proxies = 0, standIns = 0, kept = 0;
            // the meshes in the ray-traced scene
            VoxelScene.EachNear((mesh, l2w, src, glass, fixture, door, r, glow) =>
            {
                if (r == null) return;
                int id = r.GetInstanceID();
                placed.Add(id);
                if (r.shadowCastingMode == ShadowCastingMode.Off || taken.Contains(id)) return;
                if (KeepsGameShadow(r, src)) { kept++; _keptIds.Add(id); return; }
                taken.Add(id);
                _rs.Add(r);
                inScene++;
            });
            yield return null;
            if (gen != _gen) yield break;
            // their lower levels of detail
            int n = 0;
            foreach (LODGroup g in Resources.FindObjectsOfTypeAll<LODGroup>())
            {
                if (++n % 1000 == 0) { yield return null; if (gen != _gen) yield break; }
                if (g == null || !g.gameObject.scene.IsValid()) continue;
                LOD[] lods = g.GetLODs();
                if (lods.Length < 2 || lods[0].renderers == null) continue;
                bool seen = false;
                foreach (Renderer r in lods[0].renderers)
                    if (r != null && placed.Contains(r.GetInstanceID())) { seen = true; break; }
                if (!seen) continue;
                for (int l = 1; l < lods.Length; l++)
                {
                    if (lods[l].renderers == null) continue;
                    foreach (Renderer r in lods[l].renderers)
                    {
                        if (r == null || r.shadowCastingMode == ShadowCastingMode.Off || !taken.Add(r.GetInstanceID())) continue;
                        if (KeepsGameShadow(r, null)) { kept++; _keptIds.Add(r.GetInstanceID()); continue; }
                        _rs.Add(r);
                        lower++;
                    }
                }
            }
            // the far-view stand-ins, and the game's shadow-only stand-ins on the world's layers (not a player's: in first
            // person your body and gear cast through shadow-only renderers)
            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (++n % 4000 == 0) { yield return null; if (gen != _gen) yield break; }
                if (r == null || !r.gameObject.scene.IsValid() || r.shadowCastingMode == ShadowCastingMode.Off) continue;
                int id = r.GetInstanceID();
                if (taken.Contains(id)) continue;
                bool standIn = VoxelScene.IsStandIn(id);
                bool proxy = !standIn && r.shadowCastingMode == ShadowCastingMode.ShadowsOnly && VoxelScene.IsWorldLayer(r.gameObject.layer)
                             && r.GetComponentInParent<EFT.Player>() == null;
                if (!standIn && !proxy) continue;
                taken.Add(id);
                if (KeepsGameShadow(r, null)) { kept++; _keptIds.Add(id); continue; }
                _rs.Add(r);
                if (standIn) standIns++; else proxies++;
            }
            for (int i = _was.Count; i < _rs.Count; i++) { _was.Add(ShadowCastingMode.On); _how.Add(0); }
            _index.Clear();
            for (int i = 0; i < _rs.Count; i++) if (_rs[i] != null) _index[_rs[i].GetInstanceID()] = i;
            _listed = true;
            _listing = null;
            if (!_saidList)
            {
                _saidList = true;
                Plugin.MyLog.LogInfo("[TraceSix] Sun Shadow Mode RayTracedWorld: " + _rs.Count + " meshes to take out of the game's sun shadow - "
                    + inScene + " in the ray-traced scene, " + lower + " lower levels of detail of them, " + proxies + " shadow-only stand-ins, "
                    + standIns + " far-view stand-ins; " + kept + " alpha-tested ones keep the game's shadow (listed in "
                    + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") + " ms of wall time).");
            }
        }
    }

    // On the scope's camera: GiSunWorld gives the game its shadows back while the scope renders.
    internal class GiScopeStamp : MonoBehaviour
    {
        private void OnPreCull() => GiSunWorld.ScopeRendered();
    }
}
