using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // The voxel copy of the world the light probes trace through: four cubes of Res^3 voxels centred on the
    // player (0.5 m, 1 m, 2 m and 8 m voxels = 64 m, 128 m, 256 m and 1 km across), each as two textures:
    // opacity (how much of the voxel is covered by surfaces facing each of the six axis directions) and colour.
    // All but the 8 m one have a third: where in the voxel the surfaces facing each way sit (0.5 m: what the
    // light lookup steps off; 1 m and 2 m: which side of a floor or wall a light cell's centre is on).
    // The 1 m one (0.22.0) is there for what lies 25 - 50 m out: the 1 m light cells reach that far, and through
    // 2 m voxels a room or a corridor is a few voxels of mostly wall. A setting: "One-Metre Voxels".
    //
    // The game's meshes cannot be read on the CPU (recon: 8,547 of 8,886 are GPU-only), so a volume is built
    // by DRAWING the meshes (see TraceSixVoxelize.shader): fragments add coverage and colour into an integer
    // scratch volume, and two resolve passes turn that into the opacity and colour textures. The game is CPU-bound, so
    // the drawing is spread over frames with a draw budget; the volume itself changes in one frame, at the
    // resolve. A volume only rebuilds when the player has moved a fifth of its size from its centre.
    //
    // Meshes are drawn with DrawMesh from a list taken once per raid, not through their renderers: the game's
    // occlusion culling switches renderers off by what the CAMERA can see, and the voxel world must not
    // depend on where you are looking from.
    internal static class VoxelScene
    {
        public const int Res = 128;
        public const int Levels = 4;
        private const int Px = 4;                // raster pixels per voxel: 16 fragments = one fully covered voxel face
        private const int Slices = 16;           // scratch slices per voxel slice (the shader's SL): 128 x 16 = 2048, the most a 3D texture may have
        private const int GlowSlices = 4;        // (0.24.155) the glow's scratch, a texture of its own (the shader's GL)
        private const int PassOpacity = 2, PassColour = 3, PassFaces = 4, PassOccA = 5, PassOccB = 6, PassNormals = 7;
        private const int OccA = Res / 4, OccB = Res / 16;   // blocks of 4 and of 16 voxels a side
        public static readonly float[] VoxelSize = { 0.5f, 1f, 2f, 8f };
        public const int Mid = 1;                // the 1 m volume: optional

        private class Cand
        {
            public Mesh mesh;
            public Matrix4x4 l2w;
            public Bounds bounds;
            public float ext;        // largest side, metres
            public Material[] src;   // the game's material per submesh; null = not a solid surface
            public Material[] glass; // (0.24.117) the glass per submesh (GiGlass.InScene): the ray tracing's only, see-through; null = none
            public bool near, far;   // drawn into the fine volume / into the coarser ones
            public bool fixture;     // part of a lamp's own fixture (_lampIds)
            public int lod;          // its level in its LOD group, -1 = not in one
            public Transform door;   // a door's part: l2w and bounds are retaken from it at every rebuild
            public Matrix4x4 fine;   // ... where it was when the fine volume was last built
            public Matrix4x4 seen;   // ... and at the last look (still swinging?)
            public bool swinging;    // ... left out of the fine volume until the doors near the camera are still
            public int index;        // its place in _cands and _boxes
            public MeshRenderer r;   // (0.24.98) its renderer: "Sun Shadow Mode" = RayTracedWorld switches its game shadow off
            public bool glow;        // (0.24.155) its glowing materials give light (GiEmission.Allowed)
        }

        // The candidates' boxes, flat, for the scan a rebuild starts with (going through the ~77k Cand objects
        // with Bounds.Intersects took up to 5 ms of the main thread in that one frame).
        private struct Box { public Vector3 min, max; public float ext; public bool near, far; }
        private static Box[] _boxes = new Box[0];
        private static int _boxCount;        // how many of _cands _boxes holds; 0 = to be made
        private static readonly List<int> _holeNear = new List<int>();   // the openings in reach of the volume being resolved

        private class Level
        {
            public RenderTexture vol, col;       // opacity, colour
            public RenderTexture faces;          // where in the voxel each facing's surface sits
            public RenderTexture glow;           // (0.24.155) the voxel's glow (TraceSixVoxelize EMISSION)
            public RenderTexture occA, occB;     // which blocks of 4 / of 16 voxels hold anything (rays skip the rest)
            public Vector3 origin, nextOrigin;   // minimum corner
            public bool valid;
        }

        private static readonly List<Cand> _cands = new List<Cand>();
        // Doors swing, and this list is taken once: a door shut at raid start would stay a wall across its
        // doorway after it is opened. So door parts are drawn where they are when a volume is built, and a door
        // that has moved since the fine volume was built has it rebuilt once the door is at rest.
        private static readonly List<Cand> _doors = new List<Cand>();
        private static readonly HashSet<int> _doorIds = new HashSet<int>();
        // The meshes of the game's lamp fixtures (everything under an EFT.Interactive.LampController: the shade,
        // the panel, the pole - with the lights the game shines from it). Left out of the voxel world.
        //   Why. The game sets a fixture's light wherever it looks right and lets it shine through the fixture:
        // these lights cast no shadows. Interchange's hanging lamp 'shopping_mall_angle_lamp_lum_ceiling' is a
        // 5.2 x 2.4 m panel with its spot 1.5 - 2 m ABOVE the panel, pointing down. In the voxels the panel was
        // solid: its top caught the lamp from 1.5 m and bounced it onto the ceiling, and the floor beneath lay in
        // the panel's shadow - "these ceiling lights cast light the wrong way" (his press: 75 % of that
        // ceiling's light from a lamp-lit flat top 1.15 m under it). On that map 45 fixtures have a spot
        // whose beam crosses its own meshes (Tools/gisix: lampsnear.py, rendnear.py), and the lamp housing lit
        // by its own bulb from 10 cm (0.24.9) is the same thing in small.
        //   What goes: of 1285 fixtures there 497 have meshes, half of them under 2.2 m, the largest 14.8 m (a
        // hanging truss, a lamp post). No wall, floor or roof is under a LampController.
        private static readonly HashSet<int> _lampIds = new HashSet<int>();
        private static int _lampFixtures;
        private static readonly Dictionary<int, int> _taken = new Dictionary<int, int>();   // renderer -> 1 fine, 2 coarse (for the log)
        private static float _doorLookAt, _doorRestAt;
        private static Vector3 _doorAt;        // a door that has moved since the fine volume was built
        private static bool _rbForDoor;        // the rebuild under way is for it
        private static int _doorRebuilds, _doorsOut;   // _doorsOut: door parts with swinging set
        private static bool _collected, _collecting;
        private static readonly Level[] _lv = { new Level(), new Level(), new Level(), new Level() };
        private static RenderTexture _accum, _dummy;
        private static RenderTexture _accumGlow; // (0.24.155) the glow's scratch (TraceSixVoxelize: _GiAccumGlow, u2)
        private static Material _passMat;
        private static CommandBuffer _cb;
        private static readonly Dictionary<int, Material> _voxMats = new Dictionary<int, Material>();
        private static readonly Dictionary<int, bool> _solidShader = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> _solidMat = new Dictionary<int, bool>();
        private static int _blackColors;
        private static int _layerCulling = -2, _layerHighPoly = -2, _layerSky = -2;
        private static int _nHighPoly;       // meshes taken from the 'HighPolyCollider' layer
        private static int _nCullIn, _nStandIn;   // taken from the 'CullingMask' layer / left out there as far-view stand-ins
        private static readonly HashSet<int> _standIns = new HashSet<int>();
        // What the collection kept and dropped, for the log.
        private static int _nPlain, _nPaint, _nDecal, _nUntexturedMeshes, _nGlass, _nCullLayer, _nCullLayerTextured, _nTintOnly;
        private static readonly Dictionary<string, int> _tintOnlyShaders = new Dictionary<string, int>();
        private static int _nCulledIn;   // meshes taken although the game's culling had them switched off as the raid loaded
        private static readonly Dictionary<string, int> _untexturedShaders = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _cullLayerShaders = new Dictionary<string, int>();

        // Door and window openings marked by the game (its AnalyticSource boxes): kept free of voxels.
        private struct Hole { public Matrix4x4 toBox; public Bounds bounds; }
        private const int MaxHoles = 24;         // per resolve slice (the shader's array)
        private const float HoleDepth = 0.6f;    // metres kept open on each side of the marked opening
        private static readonly List<Hole> _holes = new List<Hole>();
        private static readonly Matrix4x4[] _holeBuf = new Matrix4x4[MaxHoles];
        private static int _holesMost;

        private static int _rbLevel = -1, _rbIdx, _rbDraws, _rbFrames, _builds;
        private static float _rbStart;
        private static readonly List<Cand> _rbList = new List<Cand>();

        private static readonly int IdOrigin = Shader.PropertyToID("_GiVoxOrigin");
        private static readonly int IdRes = Shader.PropertyToID("_GiVoxRes");
        // The published voxel world is read outside TraceSix too (ReflectSix marches it for the reflection rays that
        // leave the picture, with its own copy of TraceSixProbes' Through). This number names the layout those
        // readers were written for - opacity packing, origins, block maps, the march's STEP and STOP: change
        // any of them and change this, and a reader that does not know the new number stays off.
        public const float Format = 1f;
        private static readonly int IdFormat = Shader.PropertyToID("_GiVoxFormat");
        // How many times each volume has been built, for a reader that keeps something worked out from a
        // volume (ReflectSix: its filterable copy of the coverages): a door rebuilds a volume where it stands,
        // so the origin alone does not say.
        private static readonly int IdStamp = Shader.PropertyToID("_GiVoxStamp");
        private static Vector4 _stamp;
        private static readonly int IdPx = Shader.PropertyToID("_GiVoxPx");
        private static readonly int IdSlice = Shader.PropertyToID("_GiSlice");
        private static readonly int[] IdFaces = { Shader.PropertyToID("_GiVoxPos0"), Shader.PropertyToID("_GiVoxPos1"), Shader.PropertyToID("_GiVoxPos2"), Shader.PropertyToID("_GiVoxPos3") };
        private static readonly int IdNrm = Shader.PropertyToID("_GiVoxNrm");
        private static RenderTexture _nrm;       // every level's per-facing mean normals, the levels stacked along z (one texture: no choosing by level in the shader)
        private static readonly int IdSkip = Shader.PropertyToID("_GiSkip");
        private static readonly int[] IdOccA = { Shader.PropertyToID("_GiOccA0"), Shader.PropertyToID("_GiOccA1"), Shader.PropertyToID("_GiOccA2"), Shader.PropertyToID("_GiOccA3") };
        private static readonly int[] IdOccB = { Shader.PropertyToID("_GiOccB0"), Shader.PropertyToID("_GiOccB1"), Shader.PropertyToID("_GiOccB2"), Shader.PropertyToID("_GiOccB3") };
        private static readonly int[] IdVox = { Shader.PropertyToID("_GiVox0"), Shader.PropertyToID("_GiVox1"), Shader.PropertyToID("_GiVox2"), Shader.PropertyToID("_GiVox3") };
        private static readonly int[] IdCol = { Shader.PropertyToID("_GiVoxCol0"), Shader.PropertyToID("_GiVoxCol1"), Shader.PropertyToID("_GiVoxCol2"), Shader.PropertyToID("_GiVoxCol3") };
        private static readonly int[] IdOrg = { Shader.PropertyToID("_GiVoxOrigin0"), Shader.PropertyToID("_GiVoxOrigin1"), Shader.PropertyToID("_GiVoxOrigin2"), Shader.PropertyToID("_GiVoxOrigin3") };
        private static readonly int[] IdGlow = { Shader.PropertyToID("_GiVoxEmit0"), Shader.PropertyToID("_GiVoxEmit1"), Shader.PropertyToID("_GiVoxEmit2"), Shader.PropertyToID("_GiVoxEmit3") };
        private static int _passGlow = -1;   // (0.24.155) TraceSixVoxelize EMISSION
        private static int _nGlowPlain;      // glowing materials with no colour texture, taken for their glow (Factory's window slabs)

        // What the last Tick did, for the frame-spike log. Only filled with Debug Log on.
        public static string LastStep;
        // What this frame's Tick did to a voxel volume (debug log): StepLevel -1 = nothing; StepKind 1 = a
        // rebuild started, 2 = meshes drawn, 3 = the last meshes drawn + the volume resolved.
        public static int StepLevel = -1, StepKind, StepDraws;
        public static bool Ready => _lv[0].valid;
        public static bool AllReady
        {
            get
            {
                for (int l = 0; l < Levels; l++)
                    if (!_lv[l].valid && InUse(l)) return false;
                return true;
            }
        }
        private static bool InUse(int l) => l != Mid || GiConfig.MidVoxels.Value;
        public static bool LevelInUse(int l) => InUse(l);

        // New raid: the old map's meshes are gone.
        // How many meshes the last read of the map found (GiRaid: a map read too early - still loading - is read again).
        public static int SourceCount => _cands.Count;

        public static void Reset()
        {
            GiSunWorld.Forget(true);   // (its list is of these meshes)
            _cands.Clear();
            _boxCount = 0;
            _doors.Clear();
            _doorIds.Clear();
            _lampIds.Clear();
            _lampFixtures = 0;
            _taken.Clear();
            _doorRebuilds = _doorsOut = 0;
            _rbForDoor = false;
            _holes.Clear();
            _holesMost = 0;
            _collected = false;
            _rbLevel = -1;
            _rbList.Clear();
            _builds = 0;
            _blackColors = 0;
            _nPlain = _nPaint = _nDecal = _nUntexturedMeshes = _nGlass = _nCullLayer = _nCullLayerTextured = _nGlowPlain = _nTintOnly = 0;
            _tintOnlyShaders.Clear();
            _nCulledIn = 0;
            _nHighPoly = _nCullIn = _nStandIn = 0;
            _standIns.Clear();
            _untexturedShaders.Clear();
            _cullLayerShaders.Clear();
            foreach (Material m in _voxMats.Values) if (m != null) Object.Destroy(m);
            _voxMats.Clear();
            _solidShader.Clear();
            _solidMat.Clear();
            _kind.Clear();
            for (int l = 0; l < Levels; l++)
            {
                _lv[l].valid = false;
                Shader.SetGlobalVector(IdOrg[l], Vector4.zero);   // w = 0: "not built" for every reader
            }
        }

        //--- what gets voxelised ---------------------------------------------------------------------------

        // The hardware ray tracing test (GiRt): every full-detail mesh where it is now, with its solid submeshes
        // (the ones voxelised; null = not a solid surface), and whether it is part of a lamp's own fixture.
        internal static void EachNear(System.Action<Mesh, Matrix4x4, Material[], Material[], bool, Transform, MeshRenderer, bool> each)
        {
            foreach (Cand k in _cands)
                if (k.near && k.mesh != null) each(k.mesh, k.door != null ? k.door.localToWorldMatrix : k.l2w, k.src, k.glass, k.fixture, k.door, k.r, k.glow);
        }

        // (0.24.98) for GiSunWorld: the layers of the visible world, and the far-view stand-ins (left out of the voxels and
        // the ray tracing: the full-detail meshes they stand in for are in)
        internal static bool IsWorldLayer(int layer) => WorldLayer(layer);
        // (0.24.121) for GiGlass: part of the map (not gear, loot or a player) - on the visible world's layers and in the level
        internal static bool IsMapGeometry(GameObject go)
        {
            if (_layerCulling == -2) _layerCulling = LayerMask.NameToLayer("CullingMask");
            if (_layerHighPoly == -2) _layerHighPoly = LayerMask.NameToLayer("HighPolyCollider");
            if (_layerSky == -2) _layerSky = LayerMask.NameToLayer("Sky");
            return go != null && go.scene.IsValid() && WorldLayer(go.layer) && GiCulling.Present(go);
        }
        internal static bool IsStandIn(int id) => _standIns.Contains(id);

        internal static bool Collecting => _collecting;

        // One pass over the scene, spread over frames. Per LOD group the full-detail level, for every volume:
        // a simpler level may lack what stops the light. (Up to 0.15.1 the coarser volumes took the second level
        // of three or more. Interchange's OLI hall had a solid roof in the 0.5 m voxels and none in the 2 m ones:
        // beyond the fine volume the hall was lit as if open to the sky, until the fine volume reached it.)
        // "Coarse Voxels From Full Detail" off = as before. Plus every mesh that has no LOD group.
        public static IEnumerator Collect()
        {
            if (_collecting) yield break;
            _collecting = true;
            if (_layerCulling == -2) _layerCulling = LayerMask.NameToLayer("CullingMask");
            if (_layerHighPoly == -2) _layerHighPoly = LayerMask.NameToLayer("HighPolyCollider");
            if (_layerSky == -2) _layerSky = LayerMask.NameToLayer("Sky");
            float t0 = Time.realtimeSinceStartup;
            // Every mesh under one of the game's doors (EFT.Interactive.Door and what derives from it).
            int doorCount = 0;
            foreach (EFT.Interactive.Door comp in Resources.FindObjectsOfTypeAll<EFT.Interactive.Door>())
            {
                if (comp == null || !comp.gameObject.scene.IsValid()) continue;
                doorCount++;
                foreach (MeshRenderer r in comp.GetComponentsInChildren<MeshRenderer>(true)) _doorIds.Add(r.GetInstanceID());
            }
            // Every mesh under one of the game's lamps.
            foreach (EFT.Interactive.LampController comp in Resources.FindObjectsOfTypeAll<EFT.Interactive.LampController>())
            {
                if (comp == null || !comp.gameObject.scene.IsValid()) continue;
                int before = _lampIds.Count;
                foreach (MeshRenderer r in comp.GetComponentsInChildren<MeshRenderer>(true)) _lampIds.Add(r.GetInstanceID());
                if (_lampIds.Count > before) _lampFixtures++;
            }
            FindStandIns();
            GiStartup.Note("the doors, lamps and stand-ins");
            yield return null;
            var lodManaged = new HashSet<int>();
            var mats = new List<Material>();
            int n = 0, groups = 0;
            foreach (LODGroup g in Resources.FindObjectsOfTypeAll<LODGroup>())
            {
                if (++n % 1500 == 0) { GiStartup.Note("reading the map's meshes (LOD groups)"); yield return null; }
                if (g == null || !g.gameObject.scene.IsValid() || !GiCulling.Present(g.gameObject)) continue;
                LOD[] lods = g.GetLODs();
                if (lods.Length == 0) continue;
                groups++;
                int far = lods.Length >= 3 && !GiConfig.CoarseFull.Value ? 1 : 0;
                for (int l = 0; l < lods.Length; l++)
                {
                    Renderer[] rs = lods[l].renderers;
                    if (rs == null) continue;
                    foreach (Renderer r in rs)
                    {
                        if (r == null) continue;
                        lodManaged.Add(r.GetInstanceID());
                        if ((l == 0 || l == far) && r is MeshRenderer mr) Add(mr, mats, l == 0, l == far, l);
                    }
                }
            }
            yield return null;
            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (++n % 4000 == 0) { GiStartup.Note("reading the map's meshes (the rest)"); yield return null; }
                if (r == null || lodManaged.Contains(r.GetInstanceID())) continue;
                Add(r, mats, true, true, -1);
            }
            _collected = true;
            _collecting = false;
            Plugin.MyLog.LogInfo("[TraceSix] Doors: " + doorCount + " found; " + _doors.Count + " of their meshes are voxelised where the door is at the time, not where it was at raid start.");
            Plugin.MyLog.LogInfo("[TraceSix] Lamp fixtures: " + _lampFixtures + " of the game's lamps have meshes of their own (" + _lampIds.Count + " meshes); "
                + (GiConfig.LampFixtures.Value ? "they are IN the voxel world (Lamp Fixtures In The Voxel World is on)."
                                               : "they are left out of the voxel world, so a lamp neither lights nor is hidden by its own fixture."));
            Plugin.MyLog.LogInfo("[TraceSix] Voxel sources: " + _cands.Count + " meshes (" + groups + " LOD groups: " + (GiConfig.CoarseFull.Value ? "full detail for every volume" : "full detail for the fine volume, a simpler level for the others") + "), collected in "
                + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") + " ms of wall time. " + _nCulledIn
                + " of them were switched off by the game's culling at this moment. " + _nHighPoly + " are on the 'HighPolyCollider' layer (visible geometry that is also collision: taken since 0.15.6).");
            Plugin.MyLog.LogInfo("[TraceSix] Source materials (per submesh): plain " + _nPlain + ", painted 3-layer " + _nPaint + ", painted decal " + _nDecal
                + ", glowing with no colour texture (taken for their glow) " + _nGlowPlain + ", no colour texture (taken by their colour) " + _nTintOnly
                + " [" + Top(_tintOnlyShaders, 8) + "]. Skipped: " + _nUntexturedMeshes + " never-drawn helpers with no colour texture [" + Top(_untexturedShaders, 8) + "], " + _nGlass + " glass. '"
                + "CullingMask' layer: " + _nCullLayer + " meshes, " + _nCullLayerTextured + " textured [" + Top(_cullLayerShaders, 6) + "]; "
                + (GiConfig.CullLayer.Value ? _nCullIn + " of them voxelised" : "none voxelised (Voxelise Culling-Layer Geometry is off)")
                + ", " + _nStandIn + " left out as far-view stand-ins (" + _standIns.Count + " such meshes in the map).");
        }

        // The game lights its interiors with hand-placed markers at doors and windows (AnalyticSource objects,
        // named 'ambient_portal', 'door_analyticsource'): the opening is the rectangle DoorSize, as half sizes,
        // in the marker's own x-y plane, AT the marker (the back face of its light cube sits 1 - 2 m behind).
        // Read once per raid.
        public static void CollectHoles()
        {
            _holes.Clear();
            try
            {
                // Every marker in the level, not only the enabled ones: a zone the player is not in as the raid loads
                // has its markers switched off.
                int off = 0;
                var sample = new System.Text.StringBuilder();
                foreach (AnalyticSource comp in Resources.FindObjectsOfTypeAll<AnalyticSource>())
                {
                    if (comp == null || !comp.gameObject.scene.IsValid() || !GiCulling.Present(comp.gameObject)) continue;
                    // only markers that are switched on - or switched off by the culling, which is not "off"
                    if (!comp.enabled && !GiCulling.Culls(comp)) continue;
                    if (!comp.gameObject.activeInHierarchy || !comp.enabled) off++;
                    Vector2 door = comp.DoorSize;
                    Vector3 scale = comp.CubeScale, shift = comp.CubeShift;
                    if (door.x < 0.05f || door.y < 0.05f || door.x > 20f || door.y > 20f) continue;
                    Matrix4x4 l2w = comp.transform.localToWorldMatrix;
                    float zLen = Mathf.Max(l2w.GetColumn(2).magnitude, 1e-3f);
                    Matrix4x4 box = l2w * Matrix4x4.Scale(new Vector3(door.x, door.y, HoleDepth / zLen));
                    var b = new Bounds(box.MultiplyPoint3x4(Vector3.zero), Vector3.zero);
                    for (int k = 0; k < 8; k++) b.Encapsulate(box.MultiplyPoint3x4(new Vector3((k & 1) * 2 - 1, ((k >> 1) & 1) * 2 - 1, ((k >> 2) & 1) * 2 - 1)));
                    _holes.Add(new Hole { toBox = box.inverse, bounds = b });
                    if (_holes.Count <= 4)
                        sample.Append("\n      at ").Append(comp.transform.position.ToString("F1")).Append(" facing ").Append(comp.transform.forward.ToString("F2"))
                              .Append(": half size ").Append(door.ToString("F2")).Append(", cube scale ").Append(scale.ToString("F2")).Append(" shift ").Append(shift.ToString("F2"))
                              .Append(", object scale ").Append(comp.transform.lossyScale.ToString("F2")).Append(", '").Append(comp.name).Append('\'');
                }
                Plugin.MyLog.LogInfo("[TraceSix] Door/window markers: " + _holes.Count + " openings will be kept free of voxels (" + (GiConfig.OpenHoles.Value ? "on" : "switched off") + "), "
                    + off + " of them in places the game's culling has switched off for now. The first few:" + sample);
            }
            catch (System.Exception e) { Plugin.MyLog.LogWarning("[TraceSix] Door/window markers failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private static string Top(Dictionary<string, int> counts, int max)
        {
            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < max; i++)
                sb.Append(i > 0 ? ", " : "").Append(list[i].Key).Append(" x").Append(list[i].Value);
            return sb.ToString();
        }

        private static void Count(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int c);
            counts[key] = c + 1;
        }

        // The game's far-view stand-ins: a ScreenDistanceSwitcher shows a simplified copy of a group of objects
        // (its _controlLodGroup) in their place when they are far off. Not the world as it is near the player.
        private static void FindStandIns()
        {
            foreach (Koenigz.PerfectCulling.EFT.ScreenDistanceSwitcher comp in Resources.FindObjectsOfTypeAll<Koenigz.PerfectCulling.EFT.ScreenDistanceSwitcher>())
            {
                if (comp == null || !comp.gameObject.scene.IsValid()) continue;
                LODGroup g = comp._controlLodGroup;
                if (g == null) continue;
                foreach (MeshRenderer r in g.GetComponentsInChildren<MeshRenderer>(true)) _standIns.Add(r.GetInstanceID());
            }
        }

        // The layers whose meshes are the visible world.
        // (1.0.0) 'Sky' too: Ground Zero keeps whole towers and the window shells of others there (Frontier_Tower_01_LOD0,
        // background_04_Tower_Imposters_LOD0 - his Log Surface Ahead), while their shadow-only stand-ins sit on Default: left
        // out, RayTracedWorld took the stand-ins' shadow away and nothing cast one. The sky dome, rain and TOD's sky pieces on
        // that layer fail the size limit (Add) or the solid test.
        private static bool WorldLayer(int layer) => layer == 0 || layer == 11 || layer == _layerSky || (layer == _layerHighPoly && GiConfig.ColliderLayer.Value)
            || (layer == _layerCulling && GiConfig.CullLayer.Value);

        private static void Add(MeshRenderer r, List<Material> mats, bool near, bool far, int lod)
        {
            GameObject go = r.gameObject;
            // Part of the level: active, or switched off only by the game's culling for where the player is now.
            if (!go.scene.IsValid() || !GiCulling.Present(go)) return;
            // Not a lamp's own fixture (_lampIds).
            if (!GiConfig.LampFixtures.Value && _lampIds.Contains(r.GetInstanceID())) return;
            // Default + Terrain = the visible world. Not triggers, loot, sky. 'HighPolyCollider' is taken as well
            // (WorldLayer): the game keeps visible geometry there where the mesh on screen is also the one bullets
            // hit - Interchange's whole parking roof and floor ('parking_roof_LOD0', 'parking_floor_LOD0'). Left
            // out, the garage was open to the hall above for every ray going up.
            // 'CullingMask' is taken too (the Tarkov SEGI port voxelises it): the pieces the game's visibility
            // culling manages - on Reserve every wall, floor and ceiling of the underground
            // ('Reserve_Bunkers_Casarm2_Wall_LOD0'), elsewhere mostly small props. Left out, the bunkers had no
            // geometry at all for the light: open to the sky. Its shadow-only shells and decals fail the tests
            // below (no colour texture, not solid). (It was out since an early build on the suspicion of "white
            // shapes around buildings", which turned out to be the vertex-painted walls.)
            int layer = go.layer;
            if (layer == _layerCulling)
            {
                Material cm = r.sharedMaterial;
                _nCullLayer++;
                if (cm != null && cm.shader != null)
                {
                    if (Kind(cm) > 0) _nCullLayerTextured++;
                    Count(_cullLayerShaders, cm.shader.name);
                }
                if (_standIns.Contains(r.GetInstanceID())) { _nStandIn++; return; }
            }
            if (!WorldLayer(layer)) return;
            MeshFilter mf = r.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) return;
            r.GetSharedMaterials(mats);
            int count = Mathf.Min(mats.Count, mesh.subMeshCount);
            // From the mesh, not Renderer.bounds: a renderer the culling has switched off may not report them.
            Matrix4x4 l2w = r.localToWorldMatrix;
            Bounds b = TransformBounds(mesh.bounds, l2w);
            // (0.24.155) its glowing materials give light: not a lamp's own fixture, no game light on it (GiEmission)
            bool isFixture = _lampIds.Contains(r.GetInstanceID());
            bool glow = GiEmission.Allowed(r, mats, count, b, isFixture, near);
            // (1.0.0) drawn = seen in the picture: not a shadow-only caster, and enabled (or disabled only by the game's culling).
            // The enabled test IS needed: without it ~950 never-drawn textureless meshes went into the ray-traced scene and the
            // view rays hit them where nothing is seen (his screenshot: magenta over empty street).
            bool drawn = r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly && (r.enabled || GiCulling.Culls(r));
            Material[] src = null, glass = null;
            for (int i = 0; i < count; i++)
            {
                // (0.24.139) the see-through glass (GiGlass) is not Solid: in neither the voxels nor the ray-traced scene (0.24.117 -
                // 0.24.138 put it in the latter, see-through, only to tint the sun's light: "Sunlight Through Glass Is Tinted", removed)
                if (!Solid(mats[i])) continue;
                int kind = Kind(mats[i]);
                if (kind == 0 && glow && GiEmission.Of(mats[i]) != null)
                    _nGlowPlain++;   // (0.24.155) no colour texture, but it glows: taken (its colour = its tint)
                else if (kind == 0 && drawn)
                {
                    // (1.0.0) No colour texture but drawn: a real surface coloured by its tint alone - Ground Zero's glass-facade
                    // towers (p0/Reflective/Bumped Specular SMap: the look is all reflection), untextured Standard props, rocks.
                    // Left out (as up to now), they were missing from the voxels and the ray-traced scene: no ray-traced
                    // shadow, and under RayTracedWorld none at all once their shadow-only stand-ins were taken out.
                    _nTintOnly++;
                    Count(_tintOnlyShaders, mats[i].shader.name);
                }
                else if (kind == 0)
                {
                    // No colour texture and never drawn (shadow-only, or switched off for good): a helper mesh, not a surface.
                    _nUntexturedMeshes++;
                    Count(_untexturedShaders, mats[i].shader.name);
                    continue;
                }
                if (kind == 1) _nPlain++; else if (kind == 2) _nPaint++; else if (kind == 3) _nDecal++;   // (kind 0 here: glowing, counted above)
                if (src == null) src = new Material[count];
                src[i] = mats[i];
            }
            if (src == null && glass == null) return;
            if (src == null) src = new Material[count];   // (glass only: nothing for the voxels)
            float ext = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (ext < 0.25f || ext > 3000f) return;
            var cand = new Cand { mesh = mesh, l2w = l2w, bounds = b, ext = ext, src = src, glass = glass, near = near, far = far, lod = lod, fixture = isFixture, r = r, glow = glow };
            _cands.Add(cand);
            if (layer == _layerHighPoly) _nHighPoly++;
            if (layer == _layerCulling) _nCullIn++;
            if (!go.activeInHierarchy) _nCulledIn++;
            _taken.TryGetValue(r.GetInstanceID(), out int was);
            _taken[r.GetInstanceID()] = was | (near ? 1 : 0) | (far ? 2 : 0);
            if (_doorIds.Contains(r.GetInstanceID()))
            {
                cand.door = r.transform;
                cand.fine = cand.seen = l2w;
                _doors.Add(cand);
            }
        }

        // Opaque and cutout surfaces. Not the game's shadow-caster proxies (no colour, they sit inside the
        // visible mesh), decals, particles, or glass. Glass is caught by material name as well as by queue:
        // FogSix moves the game's opaque-shader window glass into the opaque queue.
        private static bool Solid(Material m)
        {
            if (m == null || m.shader == null || m.renderQueue >= 2500) return false;
            int id = m.shader.GetInstanceID();
            if (!_solidShader.TryGetValue(id, out bool ok))
            {
                string n = m.shader.name;
                ok = !(n.StartsWith("shadow") || n.StartsWith("Decal/") || n.StartsWith("Hidden/") || n.StartsWith("Global Fog/")
                       || n.StartsWith("FogSix/") || n.StartsWith("Time of Day/") || n.Contains("Particle") || n.Contains("Additive") || n.Contains("Glass"));
                _solidShader[id] = ok;
            }
            if (!ok) return false;
            int mid = m.GetInstanceID();
            if (!_solidMat.TryGetValue(mid, out bool mok))
            {
                mok = m.name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) < 0;
                _solidMat[mid] = mok;
            }
            if (!mok) _nGlass++;
            return mok;
        }

        // Alpha-tested (leaf cards, grates, fences): only part of the surface is there.
        internal static bool Cutout(Material m)
        {
            string n = m.shader != null ? m.shader.name : "";
            return m.renderQueue >= 2450 || n.Contains("Cutout") || n.Contains("SpeedTree") || n.Contains("Cutoff");
        }

        // A painted decal (roads, sidewalks: kind 3 below) - a sheet laid over the ground.
        internal static bool PaintedDecal(Material m) => Kind(m) == 3;
        // Where a material's colour comes from (Kind below): for the ray tracing's material atlas (GiRt).
        internal static int ColourKind(Material m) => Kind(m);

        // (0.24.101) BSG's TINTMASK (p0/Reflective/Bumped Specular SMap and kin; containers, cranes, painted metal): the
        // colour is _BaseTintColor laid over the texture through _TintMask - lerp(_MainTex x _Color, tint, mask.rgb), read
        // from the game's compiled deferred pass. The keyword alone switches it (no _HasTint test in that pass).
        internal static bool TintMasked(Material m) => m != null && m.IsKeywordEnabled("TINTMASK") && m.HasProperty("_TintMask")
                                                       && m.HasProperty("_BaseTintColor") && m.GetTexture("_TintMask") != null;

        // Where a material's colour comes from: 0 = it has no colour texture, 1 = _MainTex, 2 = the game's
        // three painted layers (_MainTex0..2, mixed by vertex colour), 3 = the same as a decal (roads).
        private static readonly Dictionary<int, int> _kind = new Dictionary<int, int>();
        private static int Kind(Material m)
        {
            int id = m.GetInstanceID();
            if (_kind.TryGetValue(id, out int k)) return k;
            if (m.HasProperty("_MainTex0") && m.HasProperty("_MainTex1") && m.GetTexture("_MainTex0") != null)
                k = m.shader.name.Contains("Decal") || m.shader.name.StartsWith("POMSix/Road") ? 3 : 2;   // POMSix swaps the road decal shader
            else if (m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null) k = 1;
            else k = 0;
            _kind[id] = k;
            return k;
        }

        private static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            Vector3 c = m.MultiplyPoint3x4(b.center), e = b.extents;
            Vector3 ax = m.MultiplyVector(new Vector3(e.x, 0f, 0f)), ay = m.MultiplyVector(new Vector3(0f, e.y, 0f)), az = m.MultiplyVector(new Vector3(0f, 0f, e.z));
            Vector3 we = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                                     Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                                     Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, we * 2f);
        }

        // The voxelize material for one of the game's materials: same textures and colours, our shader.
        private static readonly string[] ColorProps = { "_Color", "_Color0", "_Color1", "_Color2" };
        private static readonly string[] TexProps = { "_MainTex", "_MainTex0", "_MainTex1", "_MainTex2", "_TintMask" };

        // (0.24.155) glow: the copy that draws the material's glow too (GiEmission; the same material on a mesh beside a lamp
        // draws none) - a copy of its own, keyed by id x 2 + 1.
        private static Material VoxMat(Material src, bool glow)
        {
            GiEmission.Glow g = glow ? GiEmission.Of(src) : null;
            int id = src.GetInstanceID() * 2 + (g != null ? 1 : 0);
            if (_voxMats.TryGetValue(id, out Material m) && m != null) return m;
            m = new Material(GiAssets.Voxelize) { hideFlags = HideFlags.DontSave };
            m.CopyPropertiesFromMaterial(src);
            // After the copy the material only carries what the SOURCE had; anything our shader reads beyond
            // that is absent and reads as zero (the first build's black map: no source has all of these).
            // So every colour/texture the source lacks is set to white here. Asked of the source, not of the
            // copy: GetColor on an absent property logs an error.
            bool textured = Kind(src) != 0;   // (1.0.0) without a colour texture the tint IS the colour, black included
            foreach (string c in ColorProps)
            {
                if (!src.HasProperty(c)) { m.SetColor(c, Color.white); continue; }
                Color v = src.GetColor(c);
                if (v.r + v.g + v.b >= 0.003f || !textured) continue;
                m.SetColor(c, Color.white);   // a black tint on a textured surface: the game's shader ignores it
                _blackColors++;
            }
            foreach (string t in TexProps)
                if (!src.HasProperty(t) || src.GetTexture(t) == null) m.SetTexture(t, Texture2D.whiteTexture);

            int kind = Kind(src);
            m.SetFloat("_GiPaint", kind == 3 ? 2f : (kind == 2 ? 1f : 0f));
            bool tinted = TintMasked(src);
            m.SetFloat("_GiTint", tinted ? 1f : 0f);
            if (!src.HasProperty("_BaseTintColor")) m.SetColor("_BaseTintColor", Color.white);
            string n = src.shader.name;
            bool cutout = Cutout(src);
            m.SetFloat("_GiCutout", cutout ? 1f : 0f);
            // Drawn from both sides in the game (leaf cards, fences, sheets): it stops light from both sides.
            bool twoSided = cutout || (src.HasProperty("_Cull") && src.GetFloat("_Cull") < 0.5f)
                            || n.IndexOf("sided", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.Contains("Double");
            m.SetFloat("_GiTwoSided", twoSided ? 1f : 0f);
            if (!src.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            m.SetFloat("_GiEmit", g != null ? 1f : 0f);
            if (g != null)
            {
                m.SetTexture("_GiEmitTex0", g.tex0 != null ? g.tex0 : Texture2D.whiteTexture);
                m.SetTexture("_GiEmitTex1", g.tex1 != null ? g.tex1 : Texture2D.whiteTexture);
                m.SetTexture("_GiEmitTex2", g.tex2 != null ? g.tex2 : Texture2D.whiteTexture);
                m.SetVector("_GiEmitCol0", g.col0);
                m.SetVector("_GiEmitCol1", g.col1);
                m.SetVector("_GiEmitCol2", g.col2);
                m.SetVector("_GiEmitST1", g.st1);
                m.SetVector("_GiEmitST2", g.st2);
            }
            m.renderQueue = 2000;
            _voxMats[id] = m;
            return m;
        }

        //--- volumes -----------------------------------------------------------------------------------------

        private static void EnsureTargets()
        {
            for (int l = 0; l < Levels; l++)
            {
                if (_lv[l].vol != null && _lv[l].col != null) continue;
                GiStartup.Note("the voxel volumes' textures made");
                _lv[l].vol = Volume("TraceSix Voxel Opacity " + l);
                _lv[l].col = Volume("TraceSix Voxel Colour " + l);
                _lv[l].valid = false;
            }
            for (int l = 0; l < Levels; l++)
            {
                if (_lv[l].occA != null && _lv[l].occB != null) continue;
                _lv[l].occA = Blocks("TraceSix Voxel Blocks 4 " + l, OccA);
                _lv[l].occB = Blocks("TraceSix Voxel Blocks 16 " + l, OccB);
                _lv[l].valid = false;
                Shader.SetGlobalTexture(IdOccA[l], _lv[l].occA);
                Shader.SetGlobalTexture(IdOccB[l], _lv[l].occB);
            }
            for (int l = 0; l < Levels; l++)
            {
                if (_lv[l].faces != null) continue;
                _lv[l].faces = Volume("TraceSix Voxel Faces " + l);
                _lv[l].valid = false;
                Shader.SetGlobalTexture(IdFaces[l], _lv[l].faces);
            }
            // (0.24.155) the glow: rgb = sqrt(glow / 16), a = the way it faces - 8 bits a channel, like the colour
            for (int l = 0; l < Levels; l++)
            {
                if (_lv[l].glow != null || !GiEmission.Built) continue;
                _lv[l].glow = Volume("TraceSix Voxel Glow " + l);
                _lv[l].valid = false;
                Shader.SetGlobalTexture(IdGlow[l], _lv[l].glow);
            }
            if (_nrm == null)
            {
                _nrm = new RenderTexture(Res, Res, 0, RenderTextureFormat.RGInt, RenderTextureReadWrite.Linear)
                {
                    name = "TraceSix Voxel Normals", dimension = TextureDimension.Tex3D, volumeDepth = Res * Levels,
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
                };
                _nrm.Create();
                Shader.SetGlobalTexture(IdNrm, _nrm);
            }
            // Scratch for the volume being rebuilt: Slices integer slices per voxel slice.
            if (_accum == null)
            {
                _accum = new RenderTexture(Res, Res, 0, RenderTextureFormat.RInt, RenderTextureReadWrite.Linear)
                {
                    name = "TraceSix Voxel Scratch", dimension = TextureDimension.Tex3D, volumeDepth = Res * Slices, enableRandomWrite = true,
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
                };
                _accum.Create();
            }
            if (_accumGlow == null)
            {
                _accumGlow = new RenderTexture(Res, Res, 0, RenderTextureFormat.RInt, RenderTextureReadWrite.Linear)
                {
                    name = "TraceSix Voxel Glow Scratch", dimension = TextureDimension.Tex3D, volumeDepth = Res * GlowSlices, enableRandomWrite = true,
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
                };
                _accumGlow.Create();
            }
            if (_dummy == null)
            {
                _dummy = new RenderTexture(Res * Px, Res * Px, 0, RenderTextureFormat.R8) { name = "TraceSix Voxel Raster", hideFlags = HideFlags.DontSave };
                _dummy.Create();
            }
            if (_passMat == null)
            {
                _passMat = new Material(GiAssets.Voxelize) { hideFlags = HideFlags.DontSave };
                _passGlow = _passMat.FindPass("EMISSION");
            }
            if (_cb == null) _cb = new CommandBuffer { name = "TraceSix Voxelize" };
        }

        private static RenderTexture Blocks(string name, int size)
        {
            var rt = new RenderTexture(size, size, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
            {
                name = name, dimension = TextureDimension.Tex3D, volumeDepth = size,
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
            };
            rt.Create();
            return rt;
        }

        private static RenderTexture Volume(string name)
        {
            var rt = new RenderTexture(Res, Res, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = name, dimension = TextureDimension.Tex3D, volumeDepth = Res,
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
            };
            rt.Create();
            return rt;
        }

        // Every frame in a raid.
        public static void Tick(Camera cam)
        {
            LastStep = null;
            StepLevel = -1;
            StepKind = StepDraws = 0;
            if (!GiConfig.On || !_collected || !GiAssets.Load()) return;
            EnsureTargets();
            GiEmission.SetSwitch();   // (0.24.155) "Glowing Surfaces Give Light": live
            // Rays skip empty blocks once every volume has its blocks worked out (they are, with the volume).
            Shader.SetGlobalFloat(IdSkip, AllReady ? 1f : 0f);   // rays cross empty blocks in one go, once the block maps are built
            if (_rbLevel >= 0) { ContinueRebuild(); return; }

            Vector3 cp = cam.transform.position;
            if (AllReady && Time.unscaledTime >= _doorLookAt)
            {
                _doorLookAt = Time.unscaledTime + 0.1f;   // ("Doors Let Light In As They Open": always on since 0.24.148)
                int door = DoorMoved(cp, true);
                if (door > 0)
                {
                    if (GiConfig.DebugLog.Value || _doorRebuilds < 6)
                        Plugin.MyLog.LogInfo(door == 1
                            ? "[TraceSix] A door near the camera has begun to move: the fine voxel volume is rebuilt without it until it is still."
                            : "[TraceSix] A door near the camera has moved: the fine voxel volume is rebuilt with it where it is now.");
                    _doorRebuilds++;
                    StartRebuild(0, cp);
                    _rbForDoor = true;
                    return;
                }
            }
            for (int l = 0; l < Levels; l++)
            {
                if (!InUse(l))
                {
                    // switched off: no reader takes it (w = 0), and the 2 m voxels answer there as before
                    if (_lv[l].valid)
                    {
                        _lv[l].valid = false;
                        Shader.SetGlobalVector(IdOrg[l], Vector4.zero);
                    }
                    continue;
                }
                float size = Res * VoxelSize[l];
                Vector3 d = cp - (_lv[l].origin + Vector3.one * (size * 0.5f));
                // How far the camera may be from a volume's centre before it is built anew, as a share of its
                // width. The 0.5 m and the 1 m one must reach as far as the light cells of their size do.
                float slack = l == 0 ? GiProbes.FineSlack : l == Mid ? GiProbes.MidSlack : 0.2f;
                if (!_lv[l].valid || Mathf.Max(Mathf.Abs(d.x), Mathf.Max(Mathf.Abs(d.y), Mathf.Abs(d.z))) > size * slack)
                {
                    StartRebuild(l, cp);
                    return;
                }
            }
        }

        // 2: a door part inside the fine volume is not where it was when the volume was built (or one is left out
        // of it), with every door near the camera at rest for the last 0.4 s - rebuilt with them all where they
        // are. 1 (early): a door part has begun to move - rebuilt without it, so the light comes in while it
        // swings instead of after. A closing door was open, mostly out of the way already. Doors further than
        // the volume reaches are not looked at: they are placed afresh when the volume is next built around them.
        private static int DoorMoved(Vector3 camPos, bool early)
        {
            float reach = Res * VoxelSize[0] * 0.75f;
            bool moved = false, begun = false;
            for (int i = 0; i < _doors.Count; i++)
            {
                Cand k = _doors[i];
                if (!k.near || k.door == null || (k.bounds.center - camPos).sqrMagnitude > reach * reach) continue;
                Matrix4x4 now = k.door.localToWorldMatrix;
                if (Differs(now, k.seen))
                {
                    k.seen = now;
                    _doorRestAt = Time.unscaledTime + 0.4f;
                }
                if (Differs(now, k.fine))
                {
                    moved = true;
                    _doorAt = k.bounds.center;
                    if (early && !k.swinging)
                    {
                        k.swinging = true;
                        _doorsOut++;
                        begun = true;
                    }
                }
            }
            if ((moved || _doorsOut > 0) && Time.unscaledTime >= _doorRestAt)
            {
                if (_doorsOut > 0) foreach (Cand k in _doors) k.swinging = false;
                _doorsOut = 0;
                return 2;
            }
            return begun ? 1 : 0;
        }

        // More than a centimetre or about half a degree apart.
        private static bool Differs(Matrix4x4 a, Matrix4x4 b)
        {
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 3; r++)
                    if (Mathf.Abs(a[r, c] - b[r, c]) > 0.01f) return true;
            return false;
        }

        // Door parts where they are now.
        private static void PlaceDoors(bool fine)
        {
            for (int i = 0; i < _doors.Count; i++)
            {
                Cand k = _doors[i];
                if (k.door == null || k.mesh == null) continue;   // gone with its scene
                k.l2w = k.door.localToWorldMatrix;
                k.bounds = TransformBounds(k.mesh.bounds, k.l2w);
                if (fine) k.fine = k.seen = k.l2w;
            }
        }

        private static void BindWrite(Vector3 origin, float voxel)
        {
            _cb.SetRenderTarget(_dummy);
            _cb.SetRandomWriteTarget(1, _accum);
            _cb.SetRandomWriteTarget(2, _accumGlow);
            _cb.SetGlobalVector(IdOrigin, new Vector4(origin.x, origin.y, origin.z, 1f / voxel));
            _cb.SetGlobalFloat(IdRes, Res);
            _cb.SetGlobalFloat(IdPx, Px);
        }

        private static void StartRebuild(int l, Vector3 camPos)
        {
            Level lv = _lv[l];
            float vs = VoxelSize[l], size = Res * vs, snap = vs * 8f;
            // Snapped to whole voxels so a rebuild lands on the same grid as the one before.
            Vector3 c = new Vector3(Mathf.Round(camPos.x / snap) * snap, Mathf.Round(camPos.y / snap) * snap, Mathf.Round(camPos.z / snap) * snap);
            lv.nextOrigin = c - Vector3.one * (size * 0.5f);
            var box = new Bounds(c, Vector3.one * size);

            PlaceDoors(l == 0);
            if (_boxCount != _cands.Count) MakeBoxes();
            else for (int i = 0; i < _doors.Count; i++) SetBox(_doors[i]);   // the doors' boxes move with them
            // Coarse volumes skip what is far smaller than their voxels: it would add next to no coverage.
            _rbList.Clear();
            float minExt = l == 0 ? 0f : vs * 0.25f;
            bool fine = l == 0;
            Vector3 lo = box.min, hi = box.max;
            Box[] bx = _boxes;
            for (int i = 0; i < _boxCount; i++)
                if ((fine ? bx[i].near : bx[i].far) && bx[i].ext >= minExt
                    && bx[i].min.x <= hi.x && bx[i].max.x >= lo.x && bx[i].min.y <= hi.y && bx[i].max.y >= lo.y
                    && bx[i].min.z <= hi.z && bx[i].max.z >= lo.z
                    && !(fine && _doorsOut > 0 && _cands[i].swinging))       // a door part on the move: left out (DoorMoved)
                    _rbList.Add(_cands[i]);
            _rbLevel = l;
            _rbIdx = 0;
            _rbDraws = 0;
            _rbFrames = 0;
            _rbStart = Time.realtimeSinceStartup;

            _cb.Clear();
            _cb.SetRenderTarget(new RenderTargetIdentifier(_accum, 0, CubemapFace.Unknown, RenderTargetIdentifier.AllDepthSlices));
            _cb.ClearRenderTarget(false, true, Color.clear);
            _cb.SetRenderTarget(new RenderTargetIdentifier(_accumGlow, 0, CubemapFace.Unknown, RenderTargetIdentifier.AllDepthSlices));
            _cb.ClearRenderTarget(false, true, Color.clear);
            BindWrite(lv.nextOrigin, vs);
            foreach (Terrain t in Terrain.activeTerrains)
            {
                TerrainData td = t != null ? t.terrainData : null;
                if (td == null) continue;
                Vector3 tp = t.transform.position;
                if (!new Bounds(tp + td.size * 0.5f, td.size).Intersects(box)) continue;
                _cb.SetGlobalTexture("_GiTerrainHeight", td.heightmapTexture);
                _cb.SetGlobalVector("_GiTerrainPos", tp);
                _cb.SetGlobalVector("_GiTerrainSize", new Vector4(td.size.x, td.size.y, td.size.z, td.heightmapResolution));
                Texture2D[] alphas = td.alphamapTextures;
                for (int a = 0; a < 4; a++)
                    _cb.SetGlobalTexture("_GiTerrainAlpha" + a, a < alphas.Length && alphas[a] != null ? (Texture)alphas[a] : Texture2D.blackTexture);
                Material tm = t.materialTemplate;
                Texture diffuse = tm != null && tm.HasProperty("_Diffuse") ? tm.GetTexture("_Diffuse") : null;
                bool layered = diffuse is Texture2DArray;
                if (layered) _cb.SetGlobalTexture("_GiTerrainDiffuse", diffuse);
                _cb.SetGlobalVector("_GiTerrainLayers", new Vector4(td.alphamapLayers, layered ? 1f : 0f, 0f, 0f));
                _cb.DrawProcedural(Matrix4x4.identity, _passMat, 1, MeshTopology.Triangles, 3);
            }
            _cb.ClearRandomWriteTargets();
            Graphics.ExecuteCommandBuffer(_cb);
            StepLevel = l;
            StepKind = 1;
            if (GiConfig.DebugLog.Value) LastStep = "voxel volume " + l + " rebuild started (" + _rbList.Count + " meshes picked from " + _cands.Count + ")";
        }

        private static void SetBox(Cand k)
        {
            _boxes[k.index] = new Box { min = k.bounds.min, max = k.bounds.max, ext = k.ext, near = k.near, far = k.far };
        }

        private static void MakeBoxes()
        {
            if (_boxes.Length < _cands.Count) _boxes = new Box[_cands.Count];
            for (int i = 0; i < _cands.Count; i++)
            {
                _cands[i].index = i;
                SetBox(_cands[i]);
            }
            _boxCount = _cands.Count;
        }

        private static void ContinueRebuild()
        {
            Level lv = _lv[_rbLevel];
            int l = _rbLevel, budget = GiConfig.DrawBudget.Value, drawn = 0;
            _cb.Clear();
            BindWrite(lv.nextOrigin, VoxelSize[l]);
            while (_rbIdx < _rbList.Count && drawn < budget)
            {
                Cand k = _rbList[_rbIdx++];
                if (k.mesh == null) continue;
                for (int s = 0; s < k.src.Length; s++)
                {
                    Material sm = k.src[s];
                    if (sm == null) continue;
                    _cb.DrawMesh(k.mesh, k.l2w, VoxMat(sm, k.glow), s, 0);
                    drawn++;
                }
            }
            _cb.ClearRandomWriteTargets();
            _rbDraws += drawn;
            _rbFrames++;
            bool done = _rbIdx >= _rbList.Count;
            if (done)
            {
                // Everything is in the scratch volume: resolve it into the level's two textures, slice by
                // slice, in this one frame, so readers only ever see a complete volume.
                _cb.SetGlobalTexture("_GiAccumRead", _accum);
                _cb.SetGlobalTexture("_GiAccumGlowRead", _accumGlow);
                float vsize = VoxelSize[l];
                var slab = new Bounds(lv.nextOrigin + new Vector3(Res * vsize * 0.5f, Res * vsize * 0.5f, 0f), new Vector3(Res * vsize, Res * vsize, vsize));
                // The openings anywhere near this volume, once (a voxel of margin), so each of the 128 slices
                // tests those and not all ~560: the test per slice below is unchanged.
                _holeNear.Clear();
                if (l == 0 && GiConfig.OpenHoles.Value)
                {
                    var all = new Bounds(lv.nextOrigin + Vector3.one * (Res * vsize * 0.5f), Vector3.one * ((Res + 2) * vsize));
                    for (int h = 0; h < _holes.Count; h++)
                        if (_holes[h].bounds.Intersects(all)) _holeNear.Add(h);
                }
                for (int z = 0; z < Res; z++)
                {
                    // The openings that reach this slice (fine volume only: a door is smaller than a coarse voxel).
                    int holes = 0;
                    if (l == 0 && GiConfig.OpenHoles.Value)
                    {
                        slab.center = new Vector3(slab.center.x, slab.center.y, lv.nextOrigin.z + (z + 0.5f) * vsize);
                        for (int i = 0; i < _holeNear.Count; i++)
                        {
                            Hole hole = _holes[_holeNear[i]];
                            if (!hole.bounds.Intersects(slab)) continue;
                            if (holes < MaxHoles) _holeBuf[holes] = hole.toBox;
                            holes++;
                        }
                        _holesMost = Mathf.Max(_holesMost, holes);
                        holes = Mathf.Min(holes, MaxHoles);
                    }
                    _cb.SetGlobalMatrixArray("_GiHoles", _holeBuf);
                    _cb.SetGlobalFloat("_GiHoleCount", holes);
                    _cb.SetGlobalFloat(IdSlice, z);
                    _cb.SetRenderTarget(new RenderTargetIdentifier(lv.vol, 0, CubemapFace.Unknown, z));
                    _cb.DrawProcedural(Matrix4x4.identity, _passMat, PassOpacity, MeshTopology.Triangles, 3);
                    _cb.SetRenderTarget(new RenderTargetIdentifier(lv.col, 0, CubemapFace.Unknown, z));
                    _cb.DrawProcedural(Matrix4x4.identity, _passMat, PassColour, MeshTopology.Triangles, 3);
                    _cb.SetRenderTarget(new RenderTargetIdentifier(lv.faces, 0, CubemapFace.Unknown, z));
                    _cb.DrawProcedural(Matrix4x4.identity, _passMat, PassFaces, MeshTopology.Triangles, 3);
                    _cb.SetRenderTarget(new RenderTargetIdentifier(_nrm, 0, CubemapFace.Unknown, l * Res + z));
                    _cb.DrawProcedural(Matrix4x4.identity, _passMat, PassNormals, MeshTopology.Triangles, 3);
                    if (lv.glow != null && _passGlow >= 0)
                    {
                        _cb.SetRenderTarget(new RenderTargetIdentifier(lv.glow, 0, CubemapFace.Unknown, z));
                        _cb.DrawProcedural(Matrix4x4.identity, _passMat, _passGlow, MeshTopology.Triangles, 3);
                    }
                }
                // Which blocks of the finished volume hold anything: 4 voxels a side, then 16 from those.
                _cb.SetGlobalTexture("_GiVolRead", lv.vol);
                for (int z = 0; z < OccA; z++)
                {
                    _cb.SetGlobalFloat(IdSlice, z);
                    _cb.SetRenderTarget(new RenderTargetIdentifier(lv.occA, 0, CubemapFace.Unknown, z));
                    _cb.DrawProcedural(Matrix4x4.identity, _passMat, PassOccA, MeshTopology.Triangles, 3);
                }
                _cb.SetGlobalTexture("_GiOccRead", lv.occA);
                for (int z = 0; z < OccB; z++)
                {
                    _cb.SetGlobalFloat(IdSlice, z);
                    _cb.SetRenderTarget(new RenderTargetIdentifier(lv.occB, 0, CubemapFace.Unknown, z));
                    _cb.DrawProcedural(Matrix4x4.identity, _passMat, PassOccB, MeshTopology.Triangles, 3);
                }
            }
            Graphics.ExecuteCommandBuffer(_cb);
            StepLevel = l;
            StepKind = done ? 3 : 2;
            StepDraws = drawn;
            if (GiConfig.DebugLog.Value) LastStep = "voxel volume " + l + ": " + drawn + " draws" + (done ? " + the resolve (512 slices)" : "");
            if (!done) return;

            lv.origin = lv.nextOrigin;
            lv.valid = true;
            if (l == 0 && _rbForDoor)
            {
                _rbForDoor = false;
                GiProbes.Wake(_doorAt);          // the light around it has to be found again, and quickly
                GiProbes.Rush(_doorAt);
            }
            Shader.SetGlobalTexture(IdVox[l], lv.vol);
            Shader.SetGlobalTexture(IdCol[l], lv.col);
            Shader.SetGlobalTexture(IdFaces[l], lv.faces);
            Shader.SetGlobalTexture(IdOccA[l], lv.occA);
            Shader.SetGlobalTexture(IdOccB[l], lv.occB);
            if (lv.glow != null) Shader.SetGlobalTexture(IdGlow[l], lv.glow);
            Shader.SetGlobalVector(IdOrg[l], new Vector4(lv.origin.x, lv.origin.y, lv.origin.z, 1f / VoxelSize[l]));
            Shader.SetGlobalFloat(IdRes, Res);
            Shader.SetGlobalFloat(IdFormat, Format);
            _stamp[l] += 1f;
            Shader.SetGlobalVector(IdStamp, _stamp);
            _rbLevel = -1;
            if (GiConfig.DebugLog.Value || _builds < Levels)
            {
                Plugin.MyLog.LogInfo("[TraceSix] Voxel volume " + l + " (" + VoxelSize[l] + " m voxels, " + (Res * VoxelSize[l]) + " m across) built: "
                    + _rbList.Count + " meshes, " + _rbDraws + " draws over " + _rbFrames + " frames, "
                    + ((Time.realtimeSinceStartup - _rbStart) * 1000f).ToString("F0") + " ms of wall time, "
                    + _voxMats.Count + " materials (" + _blackColors + " with a black tint, treated as unused)."
                    + (l == 0 ? " Marked openings in reach of one slice: at most " + _holesMost + " (room for " + MaxHoles + ")." : ""));
                LogContents(l, lv.vol);
            }
            _builds++;
            _rbList.Clear();
        }

        // Ground truth for the log: how much of a finished volume holds anything, and how much of that stops a
        // ray along every axis (walls, floors, solid things) as opposed to thin or one-directional cover.
        private static void LogContents(int l, RenderTexture volume)
        {
            AsyncGPUReadback.Request(volume, 0, 0, Res, 0, Res, 0, Res, TextureFormat.RGBA32, req =>
            {
                if (req.hasError) { Plugin.MyLog.LogWarning("[TraceSix] Voxel volume " + l + ": readback failed."); return; }
                long any = 0, solid = 0, total = 0;
                for (int k = 0; k < req.layerCount; k++)
                {
                    var px = req.GetData<byte>(k);
                    for (int i = 0; i + 3 < px.Length; i += 4 * 13)   // a sample: the whole volume is two million voxels
                    {
                        total++;
                        uint u = (uint)(px[i] | px[i + 1] << 8 | px[i + 2] << 16 | px[i + 3] << 24);
                        if (u == 0) continue;
                        any++;
                        if (Mathf.Max(u & 31, (u >> 15) & 31) >= 16 && Mathf.Max((u >> 5) & 31, (u >> 20) & 31) >= 16
                            && Mathf.Max((u >> 10) & 31, (u >> 25) & 31) >= 16) solid++;
                    }
                }
                total = System.Math.Max(1, total);
                Plugin.MyLog.LogInfo("[TraceSix] Voxel volume " + l + " contents: " + (100.0 * any / total).ToString("F2") + "% of voxels hold something; "
                    + (100.0 * solid / System.Math.Max(1, any)).ToString("F0") + "% of those stop a ray along every axis.");
            });
        }

        //--- readback for "the lookup under me" ----------------------------------------------------------------

        // Debug: the fine volume's opacity and faces textures. origin = its minimum corner; the samplers take a
        // voxel index and give the packed six numbers (uint.MaxValue outside the volume). Two 8 MB copies.
        public static void ReadFine(System.Action<Vector3, System.Func<Vector3Int, uint>, System.Func<Vector3Int, uint>> done)
        {
            Level lv = _lv[0];
            if (!lv.valid || lv.faces == null) { done(Vector3.zero, null, null); return; }
            Vector3 org = lv.origin;
            Read(lv.vol, a => Read(lv.faces, b => done(org, a, b)));
        }

        private static void Read(RenderTexture rt, System.Action<System.Func<Vector3Int, uint>> done)
        {
            AsyncGPUReadback.Request(rt, 0, 0, Res, 0, Res, 0, Res, TextureFormat.RGBA32, req =>
            {
                if (req.hasError) { done(null); return; }
                var layers = new byte[Res][];
                for (int z = 0; z < Res; z++) layers[z] = req.GetData<byte>(z).ToArray();
                done(v =>
                {
                    if (v.x < 0 || v.y < 0 || v.z < 0 || v.x >= Res || v.y >= Res || v.z >= Res) return uint.MaxValue;
                    byte[] px = layers[v.z];
                    int i = (v.y * Res + v.x) * 4;
                    return (uint)(px[i] | px[i + 1] << 8 | px[i + 2] << 16 | px[i + 3] << 24);
                });
            });
        }

        // Debug: one level's opacity texture. origin = its minimum corner, then its voxel size and a sampler by
        // voxel index (null while the level is not built).
        public static void ReadOpacity(int level, System.Action<Vector3, float, System.Func<Vector3Int, uint>> done)
        {
            Level lv = _lv[level];
            float vs = VoxelSize[level];
            if (!lv.valid) { done(Vector3.zero, vs, null); return; }
            Vector3 org = lv.origin;
            Read(lv.vol, a => done(org, vs, a));
        }

        // Debug: one level's faces texture, a sampler by voxel index (null while it is not built, or has none).
        public static void ReadFaces(int level, System.Action<System.Func<Vector3Int, uint>> done)
        {
            Level lv = _lv[level];
            if (!lv.valid || lv.faces == null) { done(null); return; }
            Read(lv.faces, done);
        }

        // Debug: the 2 x 2 x 2 voxels from b of one level - their packed opacity (x fastest, then y, then z) and
        // their stored normals (two numbers each: the + facings, the - facings). null = could not be read.
        public static void ReadBlock(int level, Vector3Int b, System.Action<uint[], int[]> done)
        {
            Level lv = _lv[level];
            if (!lv.valid || _nrm == null) { done(null, null); return; }
            AsyncGPUReadback.Request(lv.vol, 0, b.x, 2, b.y, 2, b.z, 2, TextureFormat.RGBA32, rq =>
            {
                if (rq.hasError) { done(null, null); return; }
                var cov = new uint[8];
                for (int z = 0; z < 2; z++)
                {
                    var px = rq.GetData<byte>(z);
                    for (int i = 0; i < 4; i++) cov[z * 4 + i] = (uint)(px[i * 4] | px[i * 4 + 1] << 8 | px[i * 4 + 2] << 16 | px[i * 4 + 3] << 24);
                }
                AsyncGPUReadback.Request(_nrm, 0, b.x, 2, b.y, 2, level * Res + b.z, 2, rn =>
                {
                    if (rn.hasError) { done(cov, null); return; }
                    var nr = new int[16];
                    for (int z = 0; z < 2; z++)
                    {
                        var px = rn.GetData<int>(z);
                        if (px.Length < 8) { done(cov, null); return; }
                        for (int i = 0; i < 8; i++) nr[z * 8 + i] = px[i];
                    }
                    done(cov, nr);
                });
            });
        }

        // Debug: every mesh in the scene whose bounds hold one of the points, and whether it is in the voxel
        // world - and if not, which of the collection's rules left it out (the rules of Add, in its order).
        private const float Near = 2f;   // Explain: a box this wide around each point
        public static void Explain(Vector3[] points, System.Text.StringBuilder sb)
        {
            var mats = new List<Material>();
            var outs = new List<string>();
            var ins = new List<string>();
            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                Bounds b = TransformBounds(mesh.bounds, r.localToWorldMatrix);
                bool holds = false;
                foreach (Vector3 q in points) if (b.Intersects(new Bounds(q, Vector3.one * Near))) { holds = true; break; }
                if (!holds) continue;
                GameObject go = r.gameObject;
                int id = r.GetInstanceID();
                float ext = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                r.GetSharedMaterials(mats);
                bool usable = false;
                var ml = new System.Text.StringBuilder();
                for (int i = 0; i < mats.Count && i < 3; i++)
                {
                    Material m = mats[i];
                    if (m == null || m.shader == null) { ml.Append(" [none]"); continue; }
                    bool solid = m.renderQueue < 2500 && Solid(m);
                    int kind = Kind(m);
                    if (solid && kind > 0) usable = true;
                    ml.Append(" [").Append(m.name).Append(" / ").Append(m.shader.name).Append(" / queue ").Append(m.renderQueue)
                      .Append(solid ? "" : " / NOT solid").Append(kind > 0 ? "" : " / NO colour texture").Append(']');
                }
                string why;
                bool taken = _taken.TryGetValue(id, out int f);
                if (taken) why = "IN the " + (f == 3 ? "fine and coarse volumes" : (f == 1 ? "fine volume only" : "coarse volumes only")) + (_doorIds.Contains(id) ? " (a door part)" : "");
                else if (!go.activeInHierarchy) why = "OUT: its object is switched off";
                else if (_lampIds.Contains(id) && !GiConfig.LampFixtures.Value) why = "OUT: part of a lamp's own fixture";
                else if (go.layer == _layerCulling && !GiConfig.CullLayer.Value) why = "OUT: on the 'CullingMask' layer (Voxelise Culling-Layer Geometry is off)";
                else if (go.layer == _layerCulling && _standIns.Contains(id)) why = "OUT: a far-view stand-in";
                else if (!WorldLayer(go.layer)) why = "OUT: on layer '" + LayerMask.LayerToName(go.layer) + "'";
                else if (!usable) why = "OUT: no solid material with a colour texture";
                else if (ext < 0.25f || ext > 3000f) why = "OUT: by its size";
                else
                {
                    why = "OUT: not a LOD level that is taken";
                    LODGroup g = r.GetComponentInParent<LODGroup>();
                    if (g == null) why = "OUT: no rule found (not in a LOD group; was it there when the raid loaded?)";
                    else
                    {
                        LOD[] lods = g.GetLODs();
                        for (int l = 0; l < lods.Length; l++)
                            if (lods[l].renderers != null && System.Array.IndexOf(lods[l].renderers, r) >= 0)
                            { why = "OUT: it is LOD level " + l + " of " + lods.Length + " (taken: 0 for the fine volume, " + (lods.Length >= 3 ? 1 : 0) + " for the coarse)"; break; }
                    }
                }
                // Its place among the detail levels of its object, and how much mesh it is.
                string lodText = "";
                LODGroup lg = r.GetComponentInParent<LODGroup>();
                if (lg != null)
                {
                    LOD[] ll = lg.GetLODs();
                    for (int l = 0; l < ll.Length; l++)
                        if (ll[l].renderers != null && System.Array.IndexOf(ll[l].renderers, r) >= 0) { lodText = ", detail level " + l + " of " + ll.Length; break; }
                }
                string line = "\n    " + why + ": '" + r.name + "' " + b.size.ToString("F1") + " m" + lodText + ", " + mesh.vertexCount + " vertices," + ml;
                (taken ? ins : outs).Add(line);
            }
            sb.Append("\n  Meshes within a metre of those points - in the voxel world: ").Append(ins.Count).Append(", NOT in it: ").Append(outs.Count).Append('.');
            for (int i = 0; i < ins.Count && i < 30; i++) sb.Append(ins[i]);
            if (ins.Count > 30) sb.Append("\n    ... ").Append(ins.Count - 30).Append(" more in it");
            // What is left out, without the sky, the weather and the game's trigger boxes (huge, and never the answer).
            int shown = 0, big = 0;
            foreach (string o in outs)
            {
                if (o.Contains("SnowFlakes") || o.Contains("RainFall") || o.Contains("on layer 'Sky'") || o.Contains("on layer 'DisablerCullingObject'") || o.Contains("on layer 'Triggers'")
                    || o.Contains("Time of Day/")) { big++; continue; }
                if (shown++ < 30) sb.Append(o);
            }
            if (shown > 30) sb.Append("\n    ... ").Append(shown - 30).Append(" more left out");
            if (big > 0) sb.Append("\n    (and ").Append(big).Append(" sky, weather and trigger objects, not listed)");
        }

        // How many of the game's marked door / window openings hold a point (their voxels are kept empty).
        public static int HolesAt(Vector3 p)
        {
            int n = 0;
            foreach (Hole h in _holes)
            {
                Vector3 q = h.toBox.MultiplyPoint3x4(p);
                if (Mathf.Abs(q.x) <= 1f && Mathf.Abs(q.y) <= 1f && Mathf.Abs(q.z) <= 1f) n++;
            }
            return n;
        }

        //--- "what is overhead" ---------------------------------------------------------------------------------

        // Debug: a place counts as indoors if something is overhead, and the march that asks goes through
        // whichever voxel volume holds the place - the 0.5 m one near the camera, the 1 m and the 2 m one beyond
        // it. This goes straight up through each of the three separately, from the surface under the crosshair
        // and from the camera, and names the meshes where a march is stopped: do they agree that there is a roof?
        public static void Overhead(Camera cam)
        {
            RaycastHit hit = default;
            float best = float.MaxValue;
            Transform self = cam.transform.root;
            foreach (RaycastHit h in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 300f, ~0, QueryTriggerInteraction.Ignore))
                if (h.distance > 1.5f && h.distance < best && !h.collider.transform.IsChildOf(self)) { best = h.distance; hit = h; }
            Vector3 eye = cam.transform.position;
            bool any = best < float.MaxValue;
            Vector3 p = any ? hit.point + hit.normal * 0.75f : eye;
            string what = any ? "'" + hit.collider.name + "' " + best.ToString("F1") + " m away, facing " + hit.normal.ToString("F2") : "nothing between 1.5 and 300 m";
            Vector3 fine = _lv[0].valid ? _lv[0].origin + Vector3.one * (0.5f * Res * VoxelSize[0]) : eye;
            ReadOpacity(0, (org0, vs0, vox0) => ReadOpacity(1, (org1, vs1, vox1) => ReadOpacity(2, (org2, vs2, vox2) =>
            {
                try
                {
                    var org = new[] { org0, org1, org2 };
                    var vs = new[] { vs0, vs1, vs2 };
                    var vox = new[] { vox0, vox1, vox2 };
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[TraceSix] Overhead. Under the crosshair: ").Append(what).Append(". The 0.5 m voxel volume is centred ")
                      .Append((fine - eye).magnitude.ToString("F1")).Append(" m from the camera and reaches ").Append((0.5f * Res * VoxelSize[0]).ToString("F0")).Append(" m each way")
                      .Append(vox1 == null ? "; the 1 m one is not in use." : ".");
                    var at = new List<Vector3>();
                    if (any)
                    {
                        // The surface itself: is what makes it up in the voxel world, and what do the voxels hold there?
                        Vector3 on = hit.point;
                        at.Add(on);
                        sb.Append("\n  The voxels at that surface, at ").Append(on.ToString("F1")).Append(", from 3 m above it to 3 m below (coverage of 31 by what faces up / down):");
                        for (int k = 0; k < vox.Length; k++) Layers(on, org[k], vs[k], vox[k], sb, (vs[k] + " m").PadRight(5));
                        Column("0.75 m off that surface, at " + p.ToString("F1"), p, org, vs, vox, sb, at);
                    }
                    Column("the camera, at " + eye.ToString("F1"), eye, org, vs, vox, sb, at);
                    // Along the line of sight: aimed at the sky through a skylight, does the light get in?
                    Vector3 fwd = cam.transform.forward;
                    sb.Append("\n  Along your line of sight from the camera, looking ").Append(fwd.ToString("F2")).Append(':');
                    var stops = new List<float>();
                    for (int k = 0; k < vox.Length; k++)
                    {
                        float a = Along(eye, fwd, org[k], vs[k], vox[k], sb, (vs[k] + " m voxels").PadRight(12));
                        if (a < 0f || stops.Exists(s => Mathf.Abs(s - a) <= 1.5f)) continue;
                        stops.Add(a);
                        at.Add(eye + fwd * a);
                    }
                    if (at.Count > 0)
                    {
                        sb.Append("\n  At the surface under the crosshair and the places a march was stopped:");
                        Explain(at.ToArray(), sb);
                    }
                    Plugin.MyLog.LogInfo(sb.ToString());
                }
                catch (System.Exception e) { Plugin.MyLog.LogWarning("[TraceSix] Overhead failed: " + e); }
            })));
        }

        private static void Column(string from, Vector3 p, Vector3[] org, float[] vs, System.Func<Vector3Int, uint>[] vox, System.Text.StringBuilder sb, List<Vector3> at)
        {
            sb.Append("\n  Straight up from ").Append(from).Append(" (inside one of the game's interior volumes: ")
              .Append(GiProbes.IndoorCpu(p) ? "yes" : "NO, so it counts as outdoors whatever is overhead").Append("):");
            var stops = new List<float>();
            int roofed = 0, open = 0;
            for (int k = 0; k < vox.Length; k++)
            {
                float h = Up(p, org[k], vs[k], vox[k], sb, (vs[k] + " m voxels").PadRight(12));
                Vector3 q = (p - org[k]) / vs[k];
                if (vox[k] != null && q.x >= 0f && q.y >= 0f && q.z >= 0f && q.x < Res && q.y < Res && q.z < Res)
                {
                    if (h >= 0f) roofed++;
                    else open++;
                }
                if (h < 0f || stops.Exists(s => Mathf.Abs(s - h) <= 1.5f)) continue;
                stops.Add(h);
                at.Add(p + Vector3.up * h);
            }
            if (roofed > 0 && open > 0)
                sb.Append("\n    THEY DISAGREE: this place changes between indoors and outdoors as the finer voxel volumes move with you.");
            if (stops.Count == 0) return;
            sb.Append("\n    The voxels around that height, from 3 m above it to 3 m below (coverage of 31 by what faces up / down):");
            for (int k = 0; k < vox.Length; k++) Layers(p + Vector3.up * stops[0], org[k], vs[k], vox[k], sb, (vs[k] + " m").PadRight(5));
        }

        private static void Layers(Vector3 at, Vector3 org, float vs, System.Func<Vector3Int, uint> vox, System.Text.StringBuilder sb, string label)
        {
            sb.Append("\n      ").Append(label).Append(':');
            if (vox == null) { sb.Append(" not built."); return; }
            int last = int.MinValue;
            for (float dy = 3f; dy >= -3f; dy -= 0.25f)
            {
                Vector3 q = (at + Vector3.up * dy - org) / vs;
                var v = new Vector3Int(Mathf.FloorToInt(q.x), Mathf.FloorToInt(q.y), Mathf.FloorToInt(q.z));
                if (v.y == last) continue;
                last = v.y;
                uint u = vox(v);
                sb.Append(' ').Append(u == uint.MaxValue ? "--" : ((u >> 5) & 31) + "/" + ((u >> 20) & 31));
            }
        }

        // The shader's Through() along a direction, in one voxel volume. Returns how far out it is stopped, or -1.
        private static float Along(Vector3 ro, Vector3 rd, Vector3 org, float vs, System.Func<Vector3Int, uint> vox, System.Text.StringBuilder sb, string label)
        {
            sb.Append("\n    ").Append(label).Append(": ");
            if (vox == null) { sb.Append("not built."); return -1f; }
            var ad = new Vector3(Mathf.Abs(rd.x), Mathf.Abs(rd.y), Mathf.Abs(rd.z));
            float run = 0f, trans = 1f;
            for (float t = 0f; t < 120f; t += vs * 0.7f)
            {
                Vector3 q = (ro + rd * t - org) / vs;
                uint u = vox(new Vector3Int(Mathf.FloorToInt(q.x), Mathf.FloorToInt(q.y), Mathf.FloorToInt(q.z)));
                if (u == uint.MaxValue)
                {
                    sb.Append(t <= 0f ? "the camera is outside this volume." : "left this volume " + t.ToString("F1") + " m out with " + (100f * trans * (1f - run)).ToString("F0") + "% of the ray left.");
                    return -1f;
                }
                // Coverage by the surfaces that face the ray (a ray going +x meets what faces -x).
                float o = (((rd.x > 0f ? u >> 15 : u) & 31) * ad.x + ((rd.y > 0f ? u >> 20 : u >> 5) & 31) * ad.y + ((rd.z > 0f ? u >> 25 : u >> 10) & 31) * ad.z) / 31f;
                if (o > 0f)
                {
                    run += 0.7f * o;
                    if (run >= 0.5f) { sb.Append("STOPPED ").Append(t.ToString("F1")).Append(" m out (").Append((100f * trans).ToString("F0")).Append("% of the ray had got that far). Voxel: ").Append(Describe(u)); return t; }
                }
                else if (run > 0f) { trans *= 1f - run; run = 0f; }
            }
            sb.Append("nothing stops it within 120 m (").Append((100f * trans * (1f - run)).ToString("F0")).Append("% of the ray left).");
            return -1f;
        }

        // The shader's Through(), straight up, in one voxel volume. Returns how high it is stopped, or -1.
        private static float Up(Vector3 p, Vector3 org, float vs, System.Func<Vector3Int, uint> vox, System.Text.StringBuilder sb, string label)
        {
            sb.Append("\n    ").Append(label).Append(": ");
            if (vox == null) { sb.Append("not built."); return -1f; }
            float run = 0f, trans = 1f;
            for (float t = 0f; t < 64f; t += vs * 0.7f)
            {
                Vector3 q = (p + Vector3.up * t - org) / vs;
                uint u = vox(new Vector3Int(Mathf.FloorToInt(q.x), Mathf.FloorToInt(q.y), Mathf.FloorToInt(q.z)));
                if (u == uint.MaxValue)
                {
                    sb.Append(t <= 0f ? "the place is outside this volume." : "left this volume " + t.ToString("F1") + " m up with " + (100f * trans * (1f - run)).ToString("F0") + "% of the ray left.");
                    return -1f;
                }
                float o = ((u >> 20) & 31) / 31f;                                // what faces down is what a ray going up meets
                if (o > 0f)
                {
                    run += 0.7f * o;
                    if (run >= 0.5f) { sb.Append("STOPPED ").Append(t.ToString("F1")).Append(" m up. Voxel: ").Append(Describe(u)); return t; }
                }
                else if (run > 0f) { trans *= 1f - run; run = 0f; }
            }
            sb.Append("nothing stops it within 64 m (").Append((100f * trans * (1f - run)).ToString("F0")).Append("% of the ray left).");
            return -1f;
        }

        //--- "what is ahead" ------------------------------------------------------------------------------------

        // Debug: marches the fine volume from the camera along its view the way a probe's ray goes
        // (TraceSixProbes.shader: MarchStep) and names the meshes at every place that dims or stops it. Answers
        // "what in the voxel world is in this window / doorway" with mesh and material names.
        public static void Ahead(Camera cam)
        {
            Level lv = _lv[0];
            if (!lv.valid) { Plugin.MyLog.LogInfo("[TraceSix] Ahead: the fine voxel volume is not built yet."); return; }
            Vector3 ro = cam.transform.position, fwdDir = cam.transform.forward, org = lv.origin;
            AsyncGPUReadback.Request(lv.vol, 0, 0, Res, 0, Res, 0, Res, TextureFormat.RGBA32, req =>
            {
                if (req.hasError) { Plugin.MyLog.LogWarning("[TraceSix] Ahead: readback failed."); return; }
                var sb = new System.Text.StringBuilder();
                sb.Append("[TraceSix] Ahead of the camera at ").Append(ro.ToString("F1")).Append(", looking ").Append(fwdDir.ToString("F2")).Append(':');
                Trace(req, org, ro, fwdDir, sb, true);
                // Sanity check of the readback itself: straight down must stop at the floor under your feet.
                sb.Append("\n  Straight down:");
                Trace(req, org, ro, Vector3.down, sb, false);
                Plugin.MyLog.LogInfo(sb.ToString());
            });
        }

        private static void Trace(AsyncGPUReadbackRequest req, Vector3 org, Vector3 ro, Vector3 rd, System.Text.StringBuilder sb, bool names)
        {
            float vs = VoxelSize[0], run = 0f, trans = 1f, start = 0f;
            Vector3 ad = new Vector3(Mathf.Abs(rd.x), Mathf.Abs(rd.y), Mathf.Abs(rd.z));
            for (float t = 0f; t < 40f; t += vs * 0.7f)
            {
                Vector3 p = ro + rd * t, q = (p - org) / vs;
                if (q.x < 0f || q.y < 0f || q.z < 0f || q.x >= Res || q.y >= Res || q.z >= Res)
                {
                    sb.Append("\n  left the fine volume after ").Append(t.ToString("F1")).Append(" m with ").Append((100f * trans * (1f - run)).ToString("F0")).Append("% of the ray left.");
                    return;
                }
                var px = req.GetData<byte>((int)q.z);
                int i = ((int)q.y * Res + (int)q.x) * 4;
                uint u = (uint)(px[i] | px[i + 1] << 8 | px[i + 2] << 16 | px[i + 3] << 24);
                // Coverage by the surfaces that face the ray (a ray going +x meets what faces -x).
                float o = (((rd.x > 0f ? u >> 15 : u) & 31) * ad.x + ((rd.y > 0f ? u >> 20 : u >> 5) & 31) * ad.y + ((rd.z > 0f ? u >> 25 : u >> 10) & 31) * ad.z) / 31f;
                if (o > 0f)
                {
                    if (run == 0f) start = t;
                    run += 0.7f * o;
                    if (run >= 0.5f)
                    {
                        sb.Append("\n  STOPPED at ").Append(t.ToString("F1")).Append(" m (").Append((100f * trans).ToString("F0")).Append("% of the ray arrived). Voxel: ").Append(Describe(u));
                        if (names) Names(p, sb);
                        return;
                    }
                }
                else if (run > 0f)
                {
                    sb.Append("\n  dimmed by ").Append((100f * run).ToString("F0")).Append("% between ").Append(start.ToString("F1")).Append(" and ").Append(t.ToString("F1")).Append(" m.");
                    if (names) Names(ro + rd * ((start + t) * 0.5f), sb);
                    trans *= 1f - run;
                    run = 0f;
                }
            }
            sb.Append("\n  nothing stopped it within 40 m; ").Append((100f * trans * (1f - run)).ToString("F0")).Append("% of the ray left.");
        }

        private static string Describe(uint u)
        {
            return "facing +x/+y/+z " + (u & 31) + "/" + ((u >> 5) & 31) + "/" + ((u >> 10) & 31) + ", -x/-y/-z " + ((u >> 15) & 31) + "/" + ((u >> 20) & 31) + "/" + ((u >> 25) & 31) + " (of 31).";
        }

        // The fine volume's meshes whose bounds hold a point.
        private static void Names(Vector3 p, System.Text.StringBuilder sb)
        {
            int n = 0;
            var box = new Bounds(p, Vector3.one * 0.5f);
            foreach (Cand k in _cands)
            {
                if (!k.near || k.mesh == null || !k.bounds.Intersects(box)) continue;
                if (++n > 8) { sb.Append("\n      ..."); break; }
                sb.Append("\n      mesh '").Append(k.mesh.name).Append("' ").Append(k.bounds.size.ToString("F1")).Append(" m").Append(k.lod >= 0 ? ", LOD " + k.lod : "").Append(':');
                foreach (Material m in k.src)
                    if (m != null) sb.Append(" [").Append(m.name).Append(" / ").Append(m.shader.name).Append(" / queue ").Append(m.renderQueue).Append(']');
            }
            if (n == 0) sb.Append("\n      (no mesh from the list there: terrain, or cover written from a neighbouring voxel)");
        }
    }
}
