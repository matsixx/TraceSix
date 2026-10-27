using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // Hardware ray-traced indirect light (route B; native/TraceSixRT.cpp does the D3D12 part). Phase 0 (0.24.61)
    // measured the trace; phase 1 (0.24.62) lights the rays' ends; phase 2 (0.24.66) denoises it. Switched on in a raid ("Ray Tracing Test",
    // "Trace Rays"): this mod's own list of full-detail meshes (the voxel world's) plus the terrain, made into
    // triangles here, is handed to the native side, which reads their triangles off the GPU and builds the ray
    // tracing structures in one go on the render thread - the game stops for that, once a raid. Then every frame
    // GiRtCamera makes rays from the picture, the native side traces them, and TraceSix's own pass lights where
    // they ended. The picture is untouched: "Show Ray Traced Light" shows it.
    internal static class GiRt
    {
        private const string Dll = "TraceSixRT";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr RtGetRenderEventFunc();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int RtStatus();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int RtApiVersion();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void RtSetVerbose(int on);
        // The native exports this DLL calls, as they are now: a TraceSixRT.dll left over from an older build would read
        // the arguments wrongly (a pointer taken for a count), so one that does not say this number is not used.
        private const int ApiVersion = 23;   // (18 = 0.24.120: RtSetScope; 19 = 0.24.125: RtSetExtraSun; 20 = 0.24.128: RtSetExtraRefl; 21 = 0.24.136: RtSetExtraRide;
                                             //  22 = 0.24.155: RtSetMaterials takes TileFloats a tile - the glow's tile and strength; 23 = 0.24.159: RtSetVerbose)
        private const int TileFloats = 12;   // (0.24.155) per atlas tile in the table: tiling / offset; cutoff, alpha-tested, painted, glass; glow tile, glow strength, 0, 0
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int RtSceneReady();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RtSetScene(MeshDesc[] meshes, int meshCount, int[] subs, int subCount, int[] instMesh, float[] instMat, int[] instFlags, int instCount);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RtSetVariants(int[] varMesh, int[] varFirst, int varCount, int[] varTiles, int tileCount, int[] instVar, int instCount);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RtSetFrame(int slot, IntPtr rayO, IntPtr rayD, IntPtr rayV, IntPtr open, IntPtr[] outs, float[] c, float[] lamps);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void RtSetAo(int slot, IntPtr aoO, IntPtr aoN, IntPtr ao);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void RtSetRefl(int slot, IntPtr reflO, IntPtr reflD, IntPtr hit, IntPtr pick, IntPtr cell, IntPtr col);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void RtSetExtra(int slot, IntPtr alb, IntPtr sunO, IntPtr sunOut, IntPtr lampO, IntPtr sunTint);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void RtSetScope(int slot, IntPtr sunO, IntPtr sunOut, IntPtr sunTint, float[] c);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void RtSetMaterials(IntPtr atlas, IntPtr atlasLow, int tileSize, int cols, float[] tiles, int tileCount);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int RtPopLog(byte[] buf, int cap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void RtUpdateInstances(int count, int[] index, float[] mats);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int RtGetStats(float[] stats, int n);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);

        // As the native MeshDesc (pack 4).
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct MeshDesc
        {
            public IntPtr vb, ib;
            public int stride, posOffset, posFormat, vertexCount, index32, firstSub, subCount;
            public int uvStride, uvOffset, uvFormat;   // (0.24.89) the texture coordinate: 0 = none, 1 float32, 2 float16, 3 unorm16
            public IntPtr uvVb;                        //   and its stream's buffer
            public int colStride, colOffset, colFormat; // (0.24.94) the vertex colour (painted materials' layer weights): 0 = none,
            public IntPtr colVb;                        //   1 unorm8, 2 float32, 3 float16, 4 unorm16; and its stream's buffer
        }

        // (0.24.94) One tile of the material atlas: a material and which of its colour textures - -1 = _MainTex x _Color (or
        // _Color alone), 0..2 = a painted material's layer _MainTexN x _ColorN.
        // glass: (0.24.117) see-through, tints the sun. (0.24.155) layer -2 = the material's GLOW tile (GiEmission; RTGLOWTILE);
        // glow1 = on a colour tile, its glow tile + 1 (0 = it does not glow)
        private struct TileSrc { public Material mat; public int layer; public bool glass; public int glow1; }

        private static bool _tried, _loaded, _ready, _building, _saidStatus;
        private static IntPtr _func;
        private static int _raid, _builtFor;
        private static readonly byte[] _buf = new byte[2048];
        // The terrain as triangles: meshes of this mod's own, kept until the raid is over (the build reads them on
        // the render thread).
        private static readonly List<Mesh> _terrain = new List<Mesh>();
        // (0.24.89) "Ray-Traced Textures": the materials' atlas and its eighth-size copy, held until the native side has
        // copied them at the build (then let go); the tile size and tiles a row it was made with (0 = none this raid).
        private static RenderTexture _atlas, _atlasLow;
        internal static int AtlasTile, AtlasCols;
        // (0.24.95) "Log Surface Ahead": the materials each mesh's first use handed the ray tracing, by mesh id (names and
        // ids only - nothing here keeps the map's assets alive past the raid)
        private static readonly Dictionary<int, string> _firstMats = new Dictionary<int, string>();
        // (0.24.81) The doors' parts in the scene: their instance index, their transform, the matrix last handed over.
        // Each frame those that moved go to the native side, which refreshes the top level in place before the trace:
        // a door opening lets the rays (and the sun and lamps) through its doorway as it swings.
        private struct DoorPart { public int index; public Transform t; public Matrix4x4 sent; }
        private static readonly List<DoorPart> _doors = new List<DoorPart>();
        private static int[] _doorIdx = new int[16];
        private static float[] _doorMat = new float[16 * 12];

        public static IntPtr Func => _func;
        public static bool Ready => _loaded && _ready;
        // The ray-traced light lights the picture this frame (GiRtCamera): GiCamera leaves its own last pass out.
        public static bool Applying;
        // (0.24.115) the frame whose share of the lamps' light that gets through (RTLAMPTEMPORAL) was last made: GiLightPass lays it
        // into the lamps' light the frame after
        public static int LampVisFrame = -10;

        private static bool Load()
        {
            if (_tried) return _loaded;
            _tried = true;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(GiRt).Assembly.Location), "native", Dll + ".dll");
                if (!File.Exists(path))
                {
                    Plugin.MyLog.LogWarning("[TraceSix] Ray tracing: " + path + " is missing.");
                    return false;
                }
                if (LoadLibrary(path) == IntPtr.Zero)
                {
                    Plugin.MyLog.LogWarning("[TraceSix] Ray tracing: " + path + " did not load (error " + Marshal.GetLastWin32Error() + ").");
                    return false;
                }
                int api = -1;
                try { api = RtApiVersion(); } catch (EntryPointNotFoundException) { }
                if (api != ApiVersion)
                {
                    Plugin.MyLog.LogWarning("[TraceSix] Ray tracing: " + path + " is from another build (its version " + api + ", this one needs " + ApiVersion
                        + "): copy the TraceSixRT.dll that came with this TraceSix into that folder. Off until then.");
                    return false;
                }
                _func = RtGetRenderEventFunc();
                _loaded = _func != IntPtr.Zero;
            }
            catch (Exception e) { Plugin.MyLog.LogWarning("[TraceSix] Ray tracing: the native part failed to load: " + e.GetType().Name + ": " + e.Message); }
            return _loaded;
        }

        // Every frame from GiRaid.Update. raid = the raid's id (0 = none), worldRead = its meshes have been listed.
        private static int _saidVrSkip, _verbose;   // (_verbose: what the native side was last told; it starts at 0)
        public static void Tick(MonoBehaviour host, int raid, bool worldRead)
        {
            _raid = raid;
            if (!_loaded) { if (!GiConfig.RtTest.Value || raid == 0) return; }
            else
            {
                // (0.24.159) the native side's every-300-frames GPU time / video memory lines: only with Debug Log
                int verbose = GiConfig.DebugLog.Value ? 1 : 0;
                if (verbose != _verbose) { _verbose = verbose; RtSetVerbose(verbose); }
                Drain();
                _ready = RtSceneReady() != 0;
                int status = RtStatus();
                if (status < 0 && !_saidStatus)
                {
                    _saidStatus = true;
                    Plugin.MyLog.LogWarning("[TraceSix] Ray tracing: off (native status " + status + ", the reason is in the line above).");
                }
                if (status < 0) return;
                if (_ready) FollowDoors();
                if (_ready && _atlas != null) ReleaseAtlas();   // (the native side has its own copy now)
            }
            if (!GiConfig.RtTest.Value || raid == 0 || !worldRead || _building || _builtFor == raid || VoxelScene.Collecting) return;
            // (0.24.158) in VR the rays run only with "Ray Tracing In VR (EXPERIMENTAL)": without it the map is not read in
            // (the pause and the ~1 GB). Turned on mid-raid, it is read in then.
            Camera rc = GiRaid.Cam;
            if (rc != null && rc.stereoEnabled && !(GiConfig.VrSupport.Value && GiConfig.RtVr.Value))
            {
                if (_saidVrSkip != raid)
                {
                    _saidVrSkip = raid;
                    Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: VR, and Ray Tracing In VR (EXPERIMENTAL) is off - the map is not read in for the rays; VR keeps the voxel light.");
                }
                return;
            }
            if (!Load()) { _builtFor = raid; return; }
            _building = true;
            host.StartCoroutine(Build(raid));
        }

        // The door parts that moved since last handed over (a few hundredths of a millimetre or a turn of that order
        // count): to the native side, for the next trace.
        // (0.24.82) Only doors within DoorReach of the camera: on Streets the bots open doors all over the map, and each
        // moving door costs a refresh of the whole top level; a far one is caught up once the player comes near.
        public static float DoorReach = 60f;
        internal static int LastDoorsSent;
        private static void FollowDoors()
        {
            int n = 0;
            Camera cam = GiRaid.Cam != null ? GiRaid.Cam : Camera.main;   // (0.24.143: in VR Camera.main can be the menu's)
            Vector3 cp = cam != null ? cam.transform.position : Vector3.zero;
            float reach2 = DoorReach * DoorReach;
            for (int i = 0; i < _doors.Count; i++)
            {
                DoorPart d = _doors[i];
                if (d.t == null || (cam != null && (d.t.position - cp).sqrMagnitude > reach2)) continue;
                Matrix4x4 m = d.t.localToWorldMatrix;
                bool moved = false;
                for (int e = 0; e < 12 && !moved; e++) moved = Mathf.Abs(m[e >> 2, e & 3] - d.sent[e >> 2, e & 3]) > 1e-4f;
                if (!moved) continue;
                d.sent = m;
                _doors[i] = d;
                if (n == _doorIdx.Length)
                {
                    System.Array.Resize(ref _doorIdx, n * 2);
                    System.Array.Resize(ref _doorMat, n * 2 * 12);
                }
                _doorIdx[n] = d.index;
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 4; c++) _doorMat[n * 12 + r * 4 + c] = m[r, c];
                n++;
            }
            if (n > 0) RtUpdateInstances(n, _doorIdx, _doorMat);
            LastDoorsSent = n;
        }

        // For the frame-spike line (Debug Log): what the ray tracing did around then.
        private static readonly float[] _stats = new float[24];
        internal static string SpikeNote()
        {
            if (!_loaded) return "";
            try { RtGetStats(_stats, 24); } catch { return ""; }
            return "; ray tracing: " + LastDoorsSent + " door part(s) sent, the trace's render-thread time " + _stats[15].ToString("F1")
                + " ms, " + _stats[16].ToString("F0") + " door refreshes so far";
        }


        // rayV = the reuse rays' ends (zero = none), open = which cells hold light (zero = no cell reads checked). Then event
        // 16 + slot starts the trace, 20 + slot collects it.
        public static void SetFrame(int slot, IntPtr rayO, IntPtr rayD, IntPtr rayV, IntPtr open, IntPtr[] outs, float[] c, float[] lamps) => RtSetFrame(slot, rayO, rayD, rayV, open, outs, c, lamps);
        // (0.24.86) "Ambient Occlusion" = RayTraced: the AO rays' starts and normals, and where the AO goes (zero = none)
        public static void SetAo(int slot, IntPtr aoO, IntPtr aoN, IntPtr ao) => RtSetAo(slot, aoO, aoN, ao);
        // (0.24.87) ReflectSix's off-screen reflection rays and where their ends go (zero = none)
        public static void SetRefl(int slot, IntPtr reflO, IntPtr reflD, IntPtr hit, IntPtr pick, IntPtr cell, IntPtr col) => RtSetRefl(slot, reflO, reflD, hit, pick, cell, col);
        // (0.24.89) the light rays' hit colours out; the sun's shadow rays in and its shadow out (zero = none)
        public static void SetExtra(int slot, IntPtr alb, IntPtr sunO, IntPtr sunOut, IntPtr lampO, IntPtr sunTint) => RtSetExtra(slot, alb, sunO, sunOut, lampO, sunTint);
        // (0.24.120) the scope's sun rays (GiRtScope): events 28 / 32 + slot
        public static void SetScope(int slot, IntPtr sunO, IntPtr sunOut, IntPtr sunTint, float[] c) => RtSetScope(slot, sunO, sunOut, sunTint, c);
        // (0.24.137: the native side's extra sets 1 - 3 - the glass's sun and reflection rays, RtSetExtraSun / RtSetExtraRefl /
        // RtSetExtraRide - are no longer used: the glass has no ray tracing)

        // "Cell Reads Check Visibility": which light cells hold light, for the D3D12 side (TraceSixProbes CELLOPEN: a texel
        // per cell of each of the 4 volumes' windows, and a row with the windows themselves). Each job draws it afresh
        // just before its trace, from the volumes that job's lighting reads.
        private static RenderTexture _open;
        private static IntPtr _openPtr;
        public static IntPtr OpenPtr => _openPtr;
        public static RenderTexture OpenCells()
        {
            int w = GiProbes.CellsX * GiProbes.CellsZ, h = GiProbes.CellsY * 4 + 1;
            if (_open == null || !_open.IsCreated() || _open.width != w || _open.height != h)
            {
                if (_open != null) { _open.Release(); UnityEngine.Object.Destroy(_open); }
                _open = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                    { name = "TraceSixRT Open Cells", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                _open.Create();
                _openPtr = _open.GetNativeTexturePtr();
            }
            return _open;
        }

        // The raid is over: the native side lets go of its structures (event 3: on the render thread, after the
        // frames that may still trace), and the terrain meshes go.
        public static void EndRaid()
        {
            if (_loaded && _builtFor != 0) GL.IssuePluginEvent(_func, 3);
            _builtFor = 0;
            _ready = false;
            _doors.Clear();
            _firstMats.Clear();
            GiSunWorld.Forget(false);   // (the map's renderers go with the raid)
            foreach (Mesh m in _terrain) if (m != null) UnityEngine.Object.Destroy(m);
            _terrain.Clear();
            ReleaseAtlas();
            AtlasTile = AtlasCols = 0;
        }

        // (0.24.95) "Log Surface Ahead": everything about the meshes under the crosshair that decides their colour - each
        // material's shader, keywords and properties, what the renderer overrides per object (a property block), the mesh's
        // vertex colours, and which materials the ray tracing took for that mesh (its first use). For surfaces whose
        // ray-traced colour differs from the game's.
        // (0.24.103) "Log Character Meshes": the native side's buffer-sharing test, on the render thread (event 40).
        internal static void ProbeShareBuffer()
        {
            if (!_loaded) { Plugin.MyLog.LogInfo("[TraceSixRT] Buffer sharing: the native part is not loaded."); return; }
            GL.IssuePluginEvent(_func, 40);
        }

        internal static void Surface(Camera cam)
        {
            Vector3 ro = cam.transform.position, rd = cam.transform.forward;
            // the nearest collider past your own body and gun
            Collider col = null;
            float dist = 300f;
            foreach (RaycastHit h in Physics.RaycastAll(ro, rd, 300f, ~0, QueryTriggerInteraction.Ignore))
                if (h.distance > 0.8f && h.distance < dist) { dist = h.distance; col = h.collider; }
            bool hitSome = col != null;
            Vector3 p = ro + rd * dist;
            var sb = new StringBuilder();
            sb.Append("[TraceSix] Surface ahead: ");
            if (hitSome) sb.Append("collider '").Append(col.name).Append("' at ").Append(dist.ToString("F1")).Append(" m (").Append(p.ToString("F1")).Append(')');
            else sb.Append("no collider within 300 m: the meshes the view passes through");
            // the renderers there: those whose box takes the hit point (or, with no collider hit, the view ray), smallest first
            var found = new List<Renderer>();
            var ray = new Ray(ro, rd);
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                Bounds b = r.bounds;
                b.Expand(0.3f);
                if (hitSome ? !b.Contains(p) : !b.IntersectRay(ray)) continue;
                found.Add(r);
            }
            found.Sort((a, b) => Vol(a.bounds).CompareTo(Vol(b.bounds)));
            if (found.Count == 0) sb.Append("\n  no renderer there.");
            var block = new MaterialPropertyBlock();
            for (int n = 0; n < found.Count && n < 6; n++)
            {
                Renderer r = found[n];
                sb.Append("\n  RENDERER '").Append(PathOf(r.transform)).Append("' (").Append(r.GetType().Name).Append(", layer ")
                  .Append(LayerMask.LayerToName(r.gameObject.layer)).Append(", ").Append(r.bounds.size.ToString("F1")).Append(" m)");
                // (0.24.116) its sun shadow: as the game has it, the game's culling, RayTracedWorld
                sb.Append("\n    shadow ").Append(r.shadowCastingMode).Append(", ").Append(r.enabled ? "enabled" : "DISABLED")
                  .Append(r.forceRenderingOff ? ", not drawn (forceRenderingOff)" : "")
                  .Append(GiCulling.Culls(r) ? ", switched on and off by the game's culling zones" : "")
                  .Append("; ").Append(GiSunWorld.Status(r));
                Mesh mesh = r is MeshRenderer ? r.GetComponent<MeshFilter>()?.sharedMesh : r is SkinnedMeshRenderer sk ? sk.sharedMesh : null;
                if (mesh != null)
                {
                    sb.Append("\n    mesh '").Append(mesh.name).Append("' #").Append(mesh.GetInstanceID()).Append(", ").Append(mesh.subMeshCount).Append(" submeshes, ")
                      .Append(mesh.vertexCount).Append(" vertices, ").Append(mesh.isReadable ? "readable" : "NOT readable");
                    if (mesh.HasVertexAttribute(VertexAttribute.Color))
                    {
                        sb.Append(", vertex colours ").Append(mesh.GetVertexAttributeFormat(VertexAttribute.Color)).Append('x').Append(mesh.GetVertexAttributeDimension(VertexAttribute.Color));
                        if (mesh.isReadable)
                        {
                            Color32[] cs = mesh.colors32;
                            if (cs != null && cs.Length > 0)
                            {
                                Vector4 sum = Vector4.zero, lo = Vector4.one * 255f, hi = Vector4.zero;
                                foreach (Color32 c in cs)
                                {
                                    var v = new Vector4(c.r, c.g, c.b, c.a);
                                    sum += v;
                                    lo = Vector4.Min(lo, v);
                                    hi = Vector4.Max(hi, v);
                                }
                                sb.Append(" (mean ").Append((sum / cs.Length).ToString("F0")).Append(", min ").Append(lo.ToString("F0")).Append(", max ").Append(hi.ToString("F0")).Append(" of 255)");
                            }
                        }
                    }
                    else sb.Append(", no vertex colours");
                    sb.Append("\n    ray tracing took: ").Append(_firstMats.TryGetValue(mesh.GetInstanceID(), out string took) ? took : "(this mesh is not in the ray-traced scene)");
                }
                Material[] mats = r.sharedMaterials;
                // what the renderer overrides per object, for all its materials and for each one
                bool any = false;
                r.GetPropertyBlock(block);
                string all = BlockText(block, mats);
                if (all.Length > 0) { any = true; sb.Append("\n    PROPERTY BLOCK (all materials):").Append(all); }
                for (int k = 0; k < mats.Length; k++)
                {
                    r.GetPropertyBlock(block, k);
                    string t = BlockText(block, mats);
                    if (t.Length > 0) { any = true; sb.Append("\n    PROPERTY BLOCK (material ").Append(k).Append("):").Append(t); }
                }
                if (!any) sb.Append("\n    no property block (HasPropertyBlock ").Append(r.HasPropertyBlock()).Append(')');
                for (int k = 0; k < mats.Length; k++)
                {
                    Material m = mats[k];
                    if (m == null) { sb.Append("\n    material ").Append(k).Append(": none"); continue; }
                    Shader sh = m.shader;
                    sb.Append("\n    material ").Append(k).Append(": '").Append(m.name).Append("' #").Append(m.GetInstanceID()).Append(", shader '").Append(sh.name)
                      .Append("', queue ").Append(m.renderQueue).Append(", colour kind ").Append(VoxelScene.ColourKind(m))
                      .Append(", keywords [").Append(string.Join(" ", m.shaderKeywords)).Append(']');
                    sb.Append("\n      ");
                    for (int i = 0; i < sh.GetPropertyCount(); i++)
                    {
                        string name = sh.GetPropertyName(i);
                        switch (sh.GetPropertyType(i))
                        {
                            case ShaderPropertyType.Color:
                            case ShaderPropertyType.Vector:
                                sb.Append(name).Append('=').Append(m.GetVector(name).ToString("F2")).Append(' ');
                                break;
                            case ShaderPropertyType.Float:
                            case ShaderPropertyType.Range:
                                sb.Append(name).Append('=').Append(m.GetFloat(name).ToString("0.##")).Append(' ');
                                break;
                            case ShaderPropertyType.Texture:
                                Texture tx = m.GetTexture(name);
                                if (tx != null) sb.Append(name).Append("='").Append(tx.name).Append("' ").Append(tx.width).Append('x').Append(tx.height)
                                                  .Append(' ').Append(m.GetTextureScale(name).ToString("F2")).Append(m.GetTextureOffset(name).ToString("F2")).Append(' ');
                                break;
                        }
                    }
                }
            }
            if (found.Count > 6) sb.Append("\n  ... ").Append(found.Count - 6).Append(" more renderers there (larger).");
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        private static float Vol(Bounds b) => b.size.x * b.size.y * b.size.z;

        private static string PathOf(Transform t)
        {
            string s = t.name;
            for (int i = 0; i < 3 && t.parent != null; i++) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }

        // A property block's values for the properties of the renderer's shaders (a block cannot list itself).
        private static string BlockText(MaterialPropertyBlock b, Material[] mats)
        {
            if (b.isEmpty) return "";
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            foreach (Material m in mats)
            {
                if (m == null) continue;
                Shader sh = m.shader;
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string name = sh.GetPropertyName(i);
                    if (!seen.Add(name)) continue;
                    if (b.HasColor(name)) sb.Append(' ').Append(name).Append('=').Append(b.GetColor(name).ToString("F2"));
                    else if (b.HasVector(name)) sb.Append(' ').Append(name).Append('=').Append(b.GetVector(name).ToString("F2"));
                    else if (b.HasFloat(name)) sb.Append(' ').Append(name).Append('=').Append(b.GetFloat(name).ToString("0.##"));
                    else if (b.HasTexture(name)) { Texture tx = b.GetTexture(name); sb.Append(' ').Append(name).Append("='").Append(tx != null ? tx.name : "null").Append('\''); }
                }
            }
            return sb.Length > 0 ? sb.ToString() : " (values for none of its shaders' properties)";
        }

        private static void ReleaseAtlas()
        {
            foreach (RenderTexture rt in new[] { _atlas, _atlasLow })
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            _atlas = _atlasLow = null;
        }

        // (0.24.89) The materials' atlas for the ray tracing: a tile per material (its colour texture at the mip that fits x its
        // tint, as the voxels take it; and its alpha), tile t at column t % cols, row t / cols; then an eighth-size copy (three
        // halvings: each tile stays inside its own cell). Drawn tile by tile into a tile-sized target and copied into place, a
        // few hundred a frame. Out: the tile table, 8 floats a tile (tiling, offset; cutoff, alpha-tested, 0, 0).
        private static int tinted;   // (0.24.101) tiles drawn with a TINTMASK paint mask, for the log
        private static IEnumerator BuildAtlas(List<TileSrc> mats, int tile, Action<int, int, float[]> done)
        {
            int cols = Mathf.Max(1, 4096 / tile), maxRows = Mathf.Max(1, 8192 / tile);
            int count = Mathf.Min(mats.Count, cols * maxRows);
            // (a painted material's three layers stay together; (0.24.155) and a colour tile with its glow tile)
            while (count > 0 && count < mats.Count && (mats[count].layer > 0 || mats[count].layer == -2)) count--;
            int rows = (count + cols - 1) / cols;
            tinted = 0;
            var mat = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
            int pass = mat.FindPass("RTTILE"), glowPass = mat.FindPass("RTGLOWTILE");
            if (count == 0 || pass < 0) { UnityEngine.Object.Destroy(mat); done(0, 0, null); yield break; }
            ReleaseAtlas();
            RenderTexture Make(string name, int w, int h)
            {
                var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                    { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                rt.Create();
                return rt;
            }
            _atlas = Make("TraceSixRT Material Atlas", cols * tile, rows * tile);
            RenderTexture one = Make("TraceSixRT Material Tile", tile, tile);
            var data = new float[count * TileFloats];
            var cb = new CommandBuffer { name = "TraceSix RT Atlas" };
            var mpb = new MaterialPropertyBlock();
            for (int t = 0; t < count; t++)
            {
                Material src = mats[t].mat;
                int layer = mats[t].layer;
                if (layer == -2)
                {
                    // (0.24.155) a glow tile: the glow over its peak (the colour tile's entry carries the peak)
                    GiEmission.Glow g = GiEmission.Of(src);
                    mpb.SetTexture("_GiTileTex", g != null && g.tex0 != null ? g.tex0 : Texture2D.whiteTexture);
                    mpb.SetTexture("_GiTileTex1", g != null && g.tex1 != null ? g.tex1 : Texture2D.whiteTexture);
                    mpb.SetTexture("_GiTileTex2", g != null && g.tex2 != null ? g.tex2 : Texture2D.whiteTexture);
                    float pk = g != null ? Mathf.Max(g.peak, 1e-4f) : 1f;
                    mpb.SetVector("_GiTileGlow0", g != null ? g.col0 / pk : Vector4.zero);
                    mpb.SetVector("_GiTileGlow1", g != null ? g.col1 / pk : Vector4.zero);
                    mpb.SetVector("_GiTileGlow2", g != null ? g.col2 / pk : Vector4.zero);
                    mpb.SetVector("_GiTileST1", g != null ? g.st1 : new Vector4(1f, 1f, 0f, 0f));
                    mpb.SetVector("_GiTileST2", g != null ? g.st2 : new Vector4(1f, 1f, 0f, 0f));
                    mpb.SetVector("_GiTileLods", new Vector4(Lod(g?.tex0, tile), Lod(g?.tex1, tile), Lod(g?.tex2, tile), 0f));
                    mpb.SetFloat("_GiTileSize", tile);
                    cb.SetRenderTarget(one);
                    if (glowPass >= 0) cb.DrawProcedural(Matrix4x4.identity, mat, glowPass, MeshTopology.Triangles, 3, 1, mpb);
                    else cb.ClearRenderTarget(false, true, Color.clear);
                    cb.CopyTexture(one, 0, 0, 0, 0, tile, tile, _atlas, 0, 0, (t % cols) * tile, (t / cols) * tile);
                    data[t * TileFloats] = 1f; data[t * TileFloats + 1] = 1f;
                    data[t * TileFloats + 8] = -1f;
                    continue;
                }
                string texProp = layer < 0 ? "_MainTex" : "_MainTex" + layer, colProp = layer < 0 ? "_Color" : "_Color" + layer;
                Texture tex = src != null && src.HasProperty(texProp) ? src.GetTexture(texProp) as Texture2D : null;
                Color tint = src != null && src.HasProperty(colProp) ? src.GetColor(colProp) : Color.white;
                if (tint.r + tint.g + tint.b < 0.003f && tex != null) tint = Color.white;   // (a black tint on a textured surface: the game ignores it, as VoxMat; without a texture it is the colour)
                Vector2 sc = tex != null ? src.GetTextureScale(texProp) : Vector2.one, of = tex != null ? src.GetTextureOffset(texProp) : Vector2.zero;
                mpb.SetTexture("_GiTileTex", tex != null ? tex : Texture2D.whiteTexture);
                mpb.SetColor("_GiTileColor", tint);
                mpb.SetFloat("_GiTileLod", tex != null ? Mathf.Max(0f, Mathf.Log(Mathf.Max(tex.width, tex.height) / (float)tile, 2f)) : 0f);
                mpb.SetFloat("_GiTileSize", tile);
                // (0.24.101) TINTMASK: the paint laid over through its mask, as the game does (VoxelScene.TintMasked)
                Texture mask = layer < 0 && VoxelScene.TintMasked(src) ? src.GetTexture("_TintMask") : null;
                mpb.SetTexture("_GiTileMask", mask != null ? mask : Texture2D.whiteTexture);
                Color paint = mask != null ? src.GetColor("_BaseTintColor") : Color.clear;
                mpb.SetVector("_GiTileTint", new Vector4(paint.r, paint.g, paint.b, mask != null ? 1f : 0f));
                mpb.SetFloat("_GiTileMaskLod", mask != null ? Mathf.Max(0f, Mathf.Log(Mathf.Max(mask.width, mask.height) / (float)tile, 2f)) : 0f);
                if (mask != null) tinted++;
                cb.SetRenderTarget(one);
                cb.DrawProcedural(Matrix4x4.identity, mat, pass, MeshTopology.Triangles, 3, 1, mpb);
                cb.CopyTexture(one, 0, 0, 0, 0, tile, tile, _atlas, 0, 0, (t % cols) * tile, (t / cols) * tile);
                bool glassTile = mats[t].glass;
                bool cut = !glassTile && layer < 0 && src != null && VoxelScene.Cutout(src);
                int f = t * TileFloats;
                data[f] = sc.x; data[f + 1] = sc.y; data[f + 2] = of.x; data[f + 3] = of.y;
                data[f + 4] = glassTile ? 1f : cut ? (src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f) : 0f;
                data[f + 5] = cut ? 1f : 0f;
                data[f + 6] = layer == 0 ? 1f : 0f;   // (a painted material's first layer: the kernel mixes it with the next two)
                data[f + 7] = glassTile ? 1f : 0f;    // (0.24.117) glass: every ray passes it, the sun's takes its colour (x: all of it)
                // (0.24.155) its glow tile (-1 none, or past what the atlas holds) and the glow's strength (the tile x it)
                int gt = mats[t].glow1 - 1;
                GiEmission.Glow gl = gt >= 0 && gt < count ? GiEmission.Of(src) : null;
                data[f + 8] = gl != null ? gt : -1f;
                data[f + 9] = gl != null ? gl.peak : 0f;
                if ((t & 255) == 255)
                {
                    Graphics.ExecuteCommandBuffer(cb);
                    cb.Clear();
                    yield return null;
                }
            }
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
            // the eighth-size copy: three halvings, each a 2 x 2 average (bilinear exactly between texels)
            RenderTexture a = Make("TraceSixRT Atlas Half", cols * tile / 2, rows * tile / 2), b = Make("TraceSixRT Atlas Quarter", cols * tile / 4, rows * tile / 4);
            _atlasLow = Make("TraceSixRT Material Atlas Small", cols * tile / 8, rows * tile / 8);
            Graphics.Blit(_atlas, a);
            Graphics.Blit(a, b);
            Graphics.Blit(b, _atlasLow);
            foreach (RenderTexture rt in new[] { a, b, one }) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            UnityEngine.Object.Destroy(mat);
            done(count, cols, data);
        }

        // (0.24.155) the mip of a texture that fits a tile (0 for none: white)
        private static float Lod(Texture tex, int tile) => tex != null ? Mathf.Max(0f, Mathf.Log(Mathf.Max(tex.width, tex.height) / (float)tile, 2f)) : 0f;

        private static void Drain()
        {
            int n;
            while ((n = RtPopLog(_buf, _buf.Length)) > 0) Plugin.MyLog.LogInfo(Encoding.UTF8.GetString(_buf, 0, n));
        }

        // A terrain as one mesh of triangles from its heightmap, about a metre apart at most (every sample up to
        // 1025 a side), its holes (tunnel and bunker mouths) left open. The voxel world takes the terrain from the
        // heightmap texture instead (VoxelScene): there is no mesh of it to read.
        private static Mesh TerrainMesh(Terrain t, out int triangles)
        {
            triangles = 0;
            TerrainData td = t.terrainData;
            int res = td.heightmapResolution;
            int step = Mathf.Max(1, (res - 1) / 1024), n = (res - 1) / step + 1;
            float[,] h = td.GetHeights(0, 0, res, res);
            bool[,] solid = null;
            try { solid = td.GetHoles(0, 0, td.holesResolution, td.holesResolution); } catch (Exception) { }
            Vector3 size = td.size;
            var v = new Vector3[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int si = Mathf.Min(i * step, res - 1), sj = Mathf.Min(j * step, res - 1);
                    v[j * n + i] = new Vector3(si / (float)(res - 1) * size.x, h[sj, si] * size.y, sj / (float)(res - 1) * size.z);
                }
            var idx = new List<int>((n - 1) * (n - 1) * 6);
            int hw = solid != null ? solid.GetLength(1) : 0, hh = solid != null ? solid.GetLength(0) : 0;
            for (int j = 0; j < n - 1; j++)
                for (int i = 0; i < n - 1; i++)
                {
                    if (solid != null && hw > 0 && hh > 0)
                    {
                        bool hole = false;
                        for (int b = 0; b < step && !hole; b++)
                            for (int a = 0; a < step && !hole; a++)
                                hole = !solid[Mathf.Min(j * step + b, hh - 1), Mathf.Min(i * step + a, hw - 1)];
                        if (hole) continue;
                    }
                    int c0 = j * n + i, c1 = c0 + 1, c2 = c0 + n, c3 = c2 + 1;
                    idx.Add(c0); idx.Add(c2); idx.Add(c1);
                    idx.Add(c1); idx.Add(c2); idx.Add(c3);
                }
            triangles = idx.Count / 3;
            var mesh = new Mesh { name = "TraceSixRT Terrain", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
            mesh.vertices = v;
            mesh.SetIndices(idx, MeshTopology.Triangles, 0, false);
            mesh.UploadMeshData(true);
            return mesh;
        }

        // The voxel world's full-detail meshes and the terrain: one entry per mesh (its solid submeshes, as
        // voxelised, each marked alpha-tested or not), one instance per placed copy. Then each mesh's GPU buffers
        // and layout - asking Unity for a mesh's native buffer waits on the render thread, so that part is spread
        // over frames (4 ms of each).
        private static IEnumerator Build(int raid)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var index = new Dictionary<Mesh, int>();
            var meshes = new List<Mesh>();
            var solid = new List<bool[]>();
            var cut = new List<bool[]>();
            var instMesh = new List<int>();
            var instMat = new List<float>();
            var instFlags = new List<int>();   // 1 = part of a lamp's own fixture: lamp shadow rays look past it; 2 = (0.24.98) it
                                               // keeps the game's sun shadow in RayTracedWorld (GiSunWorld.KeepsGameShadow)
            // (0.24.89) "Ray-Traced Textures": each submesh's material tile (at the mesh's first use, as its cutouts), the
            // materials in tile order - those whose colour is a texture x tint, or a tint alone; the painted 3-layer ones
            // (mixed by vertex colour) and the terrain keep the voxels' colour
            bool textures = GiConfig.RtTextures.Value;
            var tileOf = new List<int[]>();
            var tileIndex = new Dictionary<long, int>();
            var tileMats = new List<TileSrc>();
            int painted = 0, glowTiles = 0;
            // (0.24.155) glow: the copy that glows (a glow tile of its own after its colour tile); the same material on a mesh
            // beside a lamp or in a fixture takes the copy that does not - a tile entry of its own, a variant where it differs
            int TileFor(Material mat, bool glow)
            {
                if (!textures || mat == null) return -1;
                int kind = VoxelScene.ColourKind(mat);   // (0 tint only, 1 _MainTex, 2 three painted layers; 3 decals: left out)
                if (kind != 0 && kind != 1 && kind != 2) return -1;
                bool isGlass = GiGlass.IsGlass(mat);
                bool glows = glow && kind != 2 && !isGlass && GiEmission.Of(mat) != null;
                long key = (long)mat.GetInstanceID() * 2 + (glows ? 1 : 0);
                if (tileIndex.TryGetValue(key, out int t)) return t;
                t = tileMats.Count;
                tileIndex[key] = t;
                if (kind == 2)
                {
                    for (int layer = 0; layer < 3; layer++) tileMats.Add(new TileSrc { mat = mat, layer = layer });
                    painted++;
                }
                else
                {
                    tileMats.Add(new TileSrc { mat = mat, layer = -1, glass = isGlass, glow1 = glows ? t + 2 : 0 });
                    if (glows) { tileMats.Add(new TileSrc { mat = mat, layer = -2 }); glowTiles++; }
                }
                return t;
            }
            int fixtures = 0;
            int decals = 0;
            // (0.24.102) copies of a mesh placed with other materials than its first copy (a red dumpster beside a blue one):
            // each set of tiles is a variant - its own geometry-table rows on the native side, the mesh's geometry
            var instVar = new List<int>();
            var varMesh = new List<int>();
            var varTiles = new List<int[]>();
            var varIndex = new Dictionary<string, int>();
            int varCopies = 0;
            _doors.Clear();
            _firstMats.Clear();
            int glassParts = 0;
            // (0.24.117) a submesh's material for the ray tracing: the solid one, or the glass (see-through: only with the atlas -
            // without a tile the kernel could not tell it from a leaf)
            Material Part(Material[] src, Material[] glass, int k)
            {
                if (src != null && k < src.Length && src[k] != null) return src[k];
                return textures && glass != null && k < glass.Length ? glass[k] : null;
            }
            void Place(Mesh mesh, Matrix4x4 l2w, Material[] src, Material[] glass, bool fixture, Transform door, MeshRenderer rend, bool glow)
            {
                if (door != null) _doors.Add(new DoorPart { index = instMesh.Count, t = door, sent = l2w });
                if (!index.TryGetValue(mesh, out int i))
                {
                    i = meshes.Count;
                    index[mesh] = i;
                    meshes.Add(mesh);
                    solid.Add(new bool[mesh.subMeshCount]);
                    cut.Add(new bool[mesh.subMeshCount]);
                    tileOf.Add(new int[mesh.subMeshCount]);
                    // (the first use of a mesh says which of its submeshes are alpha-tested, and their materials' tiles)
                    for (int k = 0; k < mesh.subMeshCount; k++) tileOf[i][k] = -1;
                    var took = new StringBuilder();
                    for (int k = 0; src != null && k < src.Length && k < mesh.subMeshCount; k++)
                    {
                        Material e = Part(src, glass, k);
                        bool g = e != null && src[k] == null;
                        cut[i][k] = g || (e != null && VoxelScene.Cutout(e));
                        tileOf[i][k] = TileFor(e, glow);
                        took.Append(k > 0 ? ", " : "").Append(e != null ? e.name + " #" + e.GetInstanceID() + (g ? " GLASS" : "") : "none")
                            .Append(" (tile ").Append(tileOf[i][k]).Append(')');
                    }
                    _firstMats[mesh.GetInstanceID()] = took.ToString();
                }
                bool[] s = solid[i];
                for (int k = 0; k < s.Length; k++)
                {
                    bool isGlass = (src == null || k >= src.Length || src[k] == null) && Part(src, glass, k) != null && tileOf[i][k] >= 0;
                    if (isGlass) { if (!s[k]) glassParts++; s[k] = true; continue; }
                    if (src != null && (k >= src.Length || src[k] == null)) continue;
                    // Painted decals (roads, sidewalks) are left out: a sheet laid a little above the ground, while
                    // the depth the game leaves there is at or below the ground (POMSix's road stamp sinks the edges,
                    // where it clips the tessellated / carved terrain shows) - rays started under the sheet and hit its
                    // underside at once (black patches on the roads that moved with the player). The ground under a
                    // decal stops the rays; its colour is still in the voxels.
                    if (src != null && VoxelScene.PaintedDecal(src[k])) { decals++; continue; }
                    s[k] = true;
                }
                // this copy's own tiles, if its materials differ from the first copy's
                int v = -1;
                if (textures && src != null)
                {
                    int[] mine = null;
                    for (int k = 0; k < src.Length && k < mesh.subMeshCount; k++)
                    {
                        Material e = Part(src, glass, k);
                        int t = e != null ? TileFor(e, glow) : -1;
                        if (mine == null && t != tileOf[i][k]) mine = (int[])tileOf[i].Clone();
                        if (mine != null) mine[k] = t;
                    }
                    if (mine != null)
                    {
                        string key = i + ":" + string.Join(",", mine);
                        if (!varIndex.TryGetValue(key, out v)) { v = varMesh.Count; varIndex[key] = v; varMesh.Add(i); varTiles.Add(mine); }
                        varCopies++;
                    }
                }
                instVar.Add(v);
                instMesh.Add(i);
                instFlags.Add((fixture ? 1 : 0) | (rend != null && GiSunWorld.KeepsGameShadow(rend, src) ? 2 : 0));
                if (fixture) fixtures++;
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 4; c++) instMat.Add(l2w[r, c]);
            }
            VoxelScene.EachNear(Place);
            if (glassParts > 0) Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: " + glassParts + " glass parts of meshes are in the scene as see-through, tinting the sun's light.");
            double listMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();

            foreach (Mesh m in _terrain) if (m != null) UnityEngine.Object.Destroy(m);
            _terrain.Clear();
            int terrainTris = 0;
            foreach (Terrain t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                try
                {
                    Mesh tm = TerrainMesh(t, out int tris);
                    _terrain.Add(tm);
                    terrainTris += tris;
                    Place(tm, Matrix4x4.Translate(t.transform.position), null, null, false, null, null, false);
                }
                catch (Exception e) { Plugin.MyLog.LogWarning("[TraceSix] Ray tracing: terrain '" + t.name + "' could not be made into triangles: " + e.GetType().Name + ": " + e.Message); }
            }
            double terrainMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            yield return null;

            var descs = new MeshDesc[meshes.Count];
            var listedK = new List<int>[meshes.Count];   // (0.24.102) each mesh's submeshes in the order they are listed, for the variants
            var subs = new List<int>();
            int noPos = 0, oddFormat = 0, notTriangles = 0, cutouts = 0, frames = 1;
            var slice = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < meshes.Count; i++)
            {
                if (slice.ElapsedMilliseconds >= 4)
                {
                    yield return null;
                    frames++;
                    slice.Restart();
                    if (_raid != raid) { _building = false; yield break; }
                }
                Mesh m = meshes[i];
                if (m == null) continue;
                int stream = m.GetVertexAttributeStream(VertexAttribute.Position);
                if (stream < 0) { noPos++; continue; }
                VertexAttributeFormat f = m.GetVertexAttributeFormat(VertexAttribute.Position);
                int dim = m.GetVertexAttributeDimension(VertexAttribute.Position);
                int pf = dim < 3 ? -1 : f == VertexAttributeFormat.Float32 ? 0 : f == VertexAttributeFormat.Float16 ? 1 : -1;
                if (pf < 0) { oddFormat++; continue; }
                var d = new MeshDesc
                {
                    stride = m.GetVertexBufferStride(stream),
                    posOffset = m.GetVertexAttributeOffset(VertexAttribute.Position),
                    posFormat = pf,
                    vertexCount = m.vertexCount,
                    index32 = m.indexFormat == IndexFormat.UInt32 ? 1 : 0,
                    firstSub = subs.Count / 5,   // (5 ints a submesh since 0.24.89: + its material tile)
                };
                bool[] s = solid[i];
                bool terrain = _terrain.Contains(m);
                for (int k = 0; k < s.Length; k++)
                {
                    if (!s[k]) continue;
                    SubMeshDescriptor sd = m.GetSubMesh(k);
                    if (sd.topology != MeshTopology.Triangles) { notTriangles++; continue; }
                    subs.Add(sd.indexStart);
                    subs.Add(sd.indexCount);
                    subs.Add(sd.baseVertex);
                    subs.Add((cut[i][k] ? 1 : 0) | (terrain ? 2 : 0));   // 2: one-sided (the native side says why)
                    subs.Add(tileOf[i][k]);                               // (its material's tile: -1 none - limited below to the atlas's)
                    (listedK[i] ??= new List<int>()).Add(k);
                    if (cut[i][k]) cutouts++;
                    d.subCount++;
                }
                if (d.subCount == 0) continue;
                d.vb = m.GetNativeVertexBufferPtr(stream);
                d.ib = m.GetNativeIndexBufferPtr();
                // its texture coordinates (the first set), where they are in a format the native side reads
                int ust = textures ? m.GetVertexAttributeStream(VertexAttribute.TexCoord0) : -1;
                if (ust >= 0 && m.GetVertexAttributeDimension(VertexAttribute.TexCoord0) >= 2)
                {
                    VertexAttributeFormat uf = m.GetVertexAttributeFormat(VertexAttribute.TexCoord0);
                    d.uvFormat = uf == VertexAttributeFormat.Float32 ? 1 : uf == VertexAttributeFormat.Float16 ? 2 : uf == VertexAttributeFormat.UNorm16 ? 3 : 0;
                    if (d.uvFormat > 0)
                    {
                        d.uvOffset = m.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
                        d.uvStride = m.GetVertexBufferStride(ust);
                        d.uvVb = ust == stream ? d.vb : m.GetNativeVertexBufferPtr(ust);
                    }
                }
                // its vertex colours (a painted material's layer weights)
                int cst = textures ? m.GetVertexAttributeStream(VertexAttribute.Color) : -1;
                if (cst >= 0 && m.GetVertexAttributeDimension(VertexAttribute.Color) >= 3)
                {
                    VertexAttributeFormat cf = m.GetVertexAttributeFormat(VertexAttribute.Color);
                    int cdim = m.GetVertexAttributeDimension(VertexAttribute.Color);
                    d.colFormat = cdim != 4 ? 0 : cf == VertexAttributeFormat.UNorm8 ? 1 : cf == VertexAttributeFormat.Float32 ? 2
                                : cf == VertexAttributeFormat.Float16 ? 3 : cf == VertexAttributeFormat.UNorm16 ? 4 : 0;
                    if (d.colFormat > 0)
                    {
                        d.colOffset = m.GetVertexAttributeOffset(VertexAttribute.Color);
                        d.colStride = m.GetVertexBufferStride(cst);
                        d.colVb = cst == stream ? d.vb : cst == ust ? d.uvVb : m.GetNativeVertexBufferPtr(cst);
                    }
                }
                descs[i] = d;
            }
            double ptrMs = sw.Elapsed.TotalMilliseconds;
            // the materials' atlas (drawn here over a few frames; the native side copies it at the build)
            int tileCount = 0, tileCols = 0, tileSize = Mathf.Clamp(GiConfig.RtTexSize.Value, 32, 256);
            float[] tileData = null;
            sw.Restart();
            if (textures && tileMats.Count > 0 && GiAssets.Probes != null)
            {
                IEnumerator e = BuildAtlas(tileMats, tileSize, (n, c, data) => { tileCount = n; tileCols = c; tileData = data; });
                while (e.MoveNext()) yield return e.Current;   // (run inside this coroutine, frame by frame)
            }
            if (_raid != raid) { ReleaseAtlas(); _building = false; yield break; }
            double atlasMs = sw.Elapsed.TotalMilliseconds;
            if (tileCount < tileMats.Count)   // (past what the atlas holds: the voxels' colour)
                for (int q = 4; q < subs.Count; q += 5) if (subs[q] >= tileCount) subs[q] = -1;
            AtlasTile = tileCount > 0 ? tileSize : 0;
            AtlasCols = tileCols;
            RtSetMaterials(_atlas != null ? _atlas.GetNativeTexturePtr() : IntPtr.Zero, _atlasLow != null ? _atlasLow.GetNativeTexturePtr() : IntPtr.Zero,
                tileSize, tileCols, tileData ?? new float[TileFloats], tileCount);
            if (tileCount > 0)
                Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: " + tileCount + " tiles drawn into the atlas (" + tileIndex.Count + " materials, " + painted + " of them painted: three layers each; " + tinted + " painted through a tint mask; "
                    + glowTiles + " glowing: a glow tile each; "
                    + tileSize + " px a tile, " + _atlas.width + "x" + _atlas.height
                    + ", " + atlasMs.ToString("F0") + " ms over frames)" + (tileCount < tileMats.Count ? "; " + (tileMats.Count - tileCount) + " more did not fit (the voxels' colour for those)" : "") + ".");
            // the variants' tiles, one per listed submesh of their mesh (past what the atlas holds: none)
            var varFirst = new int[varMesh.Count];
            var varFlat = new List<int>();
            for (int v = 0; v < varMesh.Count; v++)
            {
                varFirst[v] = varFlat.Count;
                List<int> ks = listedK[varMesh[v]];
                if (ks != null) foreach (int k in ks) { int t = varTiles[v][k]; varFlat.Add(t >= tileCount ? -1 : t); }
            }
            RtSetVariants(varMesh.ToArray(), varFirst, varMesh.Count, varFlat.ToArray(), varFlat.Count, instVar.ToArray(), instVar.Count);
            if (varMesh.Count > 0)
                Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: " + varCopies + " placed copies have other materials than their mesh's first copy (" + varMesh.Count
                    + " material variants: their own colours, the mesh's geometry).");
            RtSetScene(descs, descs.Length, subs.ToArray(), subs.Count / 5, instMesh.ToArray(), instMat.ToArray(), instFlags.ToArray(), instMesh.Count);
            GL.IssuePluginEvent(_func, 1);
            Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: " + meshes.Count + " meshes (" + _terrain.Count + " of them terrain, " + (terrainTris / 1e6f).ToString("F2") + " million triangles), "
                + instMesh.Count + " placed (" + fixtures + " of them lamp fixtures: lamp shadow rays look past those), " + cutouts + " submeshes alpha-tested, " + decals + " placed road / sidewalk decal submeshes left out (the ground under them stops the rays), handed to the native side (the list " + listMs.ToString("F0") + " ms; the terrain "
                + terrainMs.ToString("F0") + " ms; the meshes' GPU buffers " + ptrMs.ToString("F0") + " ms over " + frames + " frames; left out: " + noPos + " without positions, "
                + oddFormat + " with positions in another format, " + notTriangles + " submeshes not of triangles); " + _doors.Count + " of the placed meshes are door parts, followed as they swing. It builds on the render thread now: the game stops for that, once.");
            _builtFor = raid;
            _building = false;
        }
    }

    // The per-frame part, on the main camera, after the lighting (G-buffer and depth done, the light volumes
    // current), where the voxel light goes into the picture: RTRAYS makes the rays from the picture, the native
    // side traces them (event 16 + slot), RTSHADE lights where they ended; the denoiser (after ReBLUR) carries
    // each traced pixel's light over from the frames before where its surface was then (RTTEMPORAL; for what
    // moves with the view - the gun - the same traced pixel) and blurs it along its own surface, twice (RTBLUR);
    // "Light The Picture" adds it to the picture in place of the voxel light (RTAPPLY). "Show Ray Traced Light"
    // puts it over the picture (RTVIEW) where this mod's other debug views go.
    internal class GiRtCamera : MonoBehaviour
    {
        private Camera _cam;
        private CommandBuffer _cbTrace, _cbLate, _cbRays;
        private Material _mat;
        private int _passRays = -1, _passShade = -1, _passTemporal = -1, _passBlur = -1, _passView = -1, _passApply = -1, _passOpen = -1;
        private int _passAoRays = -1, _passAoTemporal = -1, _passAoBlur = -1, _passReflShade = -1;
        private int _passSunRays = -1, _passSunTemporal = -1, _passSunApply = -1;
        private bool _traceOn, _said;
        private RenderTexture _rayO, _rayD, _rayV, _hit, _bounce, _pick, _pick2, _cell, _raw, _hitD, _blur, _final, _pre;
        // (0.24.105) "Variance-Guided Denoising" (NVIDIA ReLAX's structure): the temporal pass's light before the clamp, the fast
        // history and the moments (this frame's and the last), the variance (RTATROUS's ping-pong)
        private RenderTexture _tmpLight;
        private RenderTexture[] _fast, _mom;
        private readonly RenderTexture[] _var = new RenderTexture[2];
        private int _passClamp = -1, _passAtrous = -1;
        // "Reuse Ray Samples" (ReSTIR GI): this frame's and the last frame's kept samples (RTSHADE)
        private RenderTexture[] _resA, _resB, _resC;
        // (0.24.104) "Spread Ray Samples": each traced pixel's lasting spread sample (point + W, light + M; its facing in _resC.zw)
        private RenderTexture[] _resD, _resE;
        // How far a neighbour a pixel may take a sample from can be, in traced pixels. (How many frames' worth a kept
        // sample may stand for: the setting Reuse History.)
        public static float ReuseRadius = 8f;
        // (0.24.86) "Ambient Occlusion" = RayTraced, per picture pixel: the AO rays' starts and normals (RTAORAYS), the
        // D3D12 side's AO, this frame's and the last frame's accumulated AO, the blurred AO. Made only while in use.
        private RenderTexture _aoO, _aoN, _aoRaw, _aoFinal;
        private readonly RenderTexture[] _aoHist = new RenderTexture[2];
        private IntPtr _pAoO, _pAoN, _pAoRaw;
        private bool _hadAo;
        // (0.24.87) ReflectSix's off-screen reflection rays (its textures, made by its RTRAYS pass at AfterReflections), their
        // ends from the D3D12 side, and what each shows (RTREFLSHADE) - published for ReflectSix as _GiRtRefl.
        private RenderTexture _reflSrcO, _reflSrcD, _reflHit, _reflPick, _reflCell, _reflOut, _reflAlb;
        private IntPtr _pReflO, _pReflD, _pReflHit, _pReflPick, _pReflCell, _pReflAlb;
        // (0.24.89) each light ray's hit colour from its material (the D3D12 side's; RTSHADE)
        private RenderTexture _alb;
        private IntPtr _pAlb;
        // (0.24.89) "Ray-Traced Sun Shadows": the sun rays (RTSUNRAYS, one per 2 x 2) and the D3D12 side's shadow for them, the
        // accumulated shadow per picture pixel (this frame's and the last); the sun light's two command buffers - collect +
        // accumulate before its shadow mask, the shadow into the mask after - on the light only through this camera's culling
        // (OnPreCull to OnPreRender: Unity takes a light's buffers when it culls). _sunFrame: OnPreCull put them on this frame;
        // _sunP: this frame's half of the 2 x 2 and whether the history starts over; _sunCur: the history's ping-pong.
        private RenderTexture _sunO, _sunRaw;
        private readonly RenderTexture[] _sunHist = new RenderTexture[2];
        private IntPtr _pSunO, _pSunRaw;
        private CommandBuffer _cbSunBefore, _cbSunAfter;
        private Light _sunLight;
        private bool _hadSun, _saidSun, _sunFrame;
        private Vector4 _sunP;
        private int _sunCur;
        private bool _wasAlbView;   // (0.24.92) the albedo view last frame: the light's history starts over on either side of it
        // (0.24.115) "Ray-Traced Direct Lamp Shadows": the picture's lamp shadow rays (RTLAMPRAYS: one per 2 x 2, the sun rays'
        // pixel of it; their ends come back in the sun's output, yzw) and per picture pixel the share of the lamps' light that
        // gets through (RTLAMPTEMPORAL; this frame's and the last) - which TraceSix's copy of the game's light pass lays into the
        // lamps' light a frame later. Decided in OnPreCull with the sun's (_lampFrame).
        private RenderTexture _lampO;
        private readonly RenderTexture[] _lampHist = new RenderTexture[2];
        // (0.24.151) "Denoise Ray-Traced Shadows": the lamps' share's second moment (RTLAMPTEMPORAL's second target; this frame's
        // and the last), and RTSHADOWDN's two targets for the sun's and the lamps' (its three passes ping-pong, the last into [0])
        private readonly RenderTexture[] _lampM2 = new RenderTexture[2], _sunDn = new RenderTexture[2], _lampDn = new RenderTexture[2];
        private int _passShadowDn = -1;
        // (0.24.154) "Reuse Lamp Choices" (ReSTIR DI): per traced pixel the reservoir made this frame (RTLAMPRIS) and the kept ones
        // after their shadow ray (RTLAMPRESFIN, ping-pong; _lampResIdx = the one the next RTLAMPRIS reads), RTLAMPRIS's (lamp 1,
        // lamp 2, rho, kappa), and the traced 2 x 2 pixel of the frame the kept ones were made in
        private RenderTexture _lampResCur, _lampInfo;
        private readonly RenderTexture[] _lampResHist = new RenderTexture[2];
        private int _passLampRis = -1, _passLampSel = -1, _passLampResFin = -1, _lampResIdx;
        private bool _hadLampRes;
        private Vector2 _lampResP;
        private IntPtr _pLampO;
        private int _passLampRays = -1, _passLampTemporal = -1, _lampCur;
        // (0.24.156) the lamps' share made before the lighting this frame (OnPreRender); ReSTIR DI's kept-reservoir index, the
        // frame's sun pixel and whether it ran - taken over only after this frame's rays pass has read last frame's (ApplyLampState)
        private bool _lampEarly, _pendHadRes;
        private int _pendResIdx = -1;
        private Vector2 _pendResP;
        private bool _lampFrame, _hadLamp, _saidLamp;
        // EFT's distant-shadow mask (DistantShadow._shadowMaskFull, when it precomputes one): the rays' shadow goes in there too
        private DistantShadow _ds;
        private float _dsNextFind;
        // this frame's and the last frame's: the surface points and facings, and the accumulated light
        private RenderTexture[] _geoP, _geoN, _accum;
        private RenderTexture[] _stab;   // (1.0.0) "Stabilize Output": the stabilized light, this frame's and the last
        // (0.24.146) VR: each eye traced on its own (his call after trace-once - 0.24.142 - and the second eye tracing only its gaps -
        // 0.24.145 - both left seams where the eyes saw different things). What carries over from frame to frame is kept per eye
        // (the arrays above are the current eye's - UseEye at the start of each eye's OnPreRender); the rays, their ends and the
        // denoiser's scratch both eyes use in turn (each eye's trace is collected before the other eye starts; the D3D12 side's
        // shared copies stay the same ones).
        private sealed class EyeHist
        {
            public readonly RenderTexture[] geoP = new RenderTexture[2], geoN = new RenderTexture[2], accum = new RenderTexture[2],
                fast = new RenderTexture[2], mom = new RenderTexture[2], resA = new RenderTexture[2], resB = new RenderTexture[2],
                resC = new RenderTexture[2], resD = new RenderTexture[2], resE = new RenderTexture[2],
                stab = new RenderTexture[2];   // (1.0.0) "Stabilize Output"
            public int cur;
            public bool hasPrev, wasDenoised, wasAlbView, wasStab;
            public Matrix4x4 prevW2V;
            public Vector4 prevUV;
            // (0.24.157) "Stereo Blend In VR": this eye's denoised light as its last render left it, with that render's surface
            // points and facings (its geoP / geoN of then - kept until its next render writes the other of the pair) and camera
            public RenderTexture light, lightGeoP, lightGeoN;
            public Matrix4x4 lightW2V;
            public Vector4 lightUV;
            public int lightFrame = -10;
            public RenderTexture[][] All => new[] { geoP, geoN, accum, fast, mom, resA, resB, resC, resD, resE, stab };
        }
        private readonly EyeHist[] _eyes = { new EyeHist(), new EyeHist() };
        // (0.24.157) the stereo blend: its pass and its output (one: the eyes render one after the other)
        private int _passStereo = -1;
        private int _passPre = -1;   // (1.0.0) RTPREBLUR, "Pre-Blur" (not in the bundle: none)
        // (1.0.0) RTSTABILIZE, "Stabilize Output" (not in the bundle: none); its most frames, its box (in spreads of the unblurred
        // history over the 5 x 5) and the box's floor (a share of the light)
        private int _passStab = -1;
        private const float StabFrames = 16f, StabBox = 2f, StabFloor = 0.02f;
        private static readonly int IdStabRef = Shader.PropertyToID("_GiRtStabRef"), IdStabHist = Shader.PropertyToID("_GiRtStabHist"),
            IdStab = Shader.PropertyToID("_GiRtStab");
        // (1.0.0) "Voxel Light For New Pixels" (RTTEMPORAL): how many frames the voxel light counts as for a pixel with no history
        private static readonly int IdVoxNew = Shader.PropertyToID("_GiRtVoxNew");
        // (1.0.0) "Instant Light Switches": the lights that just switched (GiLamps) and how the light round them is let go of - the
        // change in a pixel's light (a share of the brighter) that starts its history over, the frames it keeps then, the most a
        // kept ray sample stands for there
        private static readonly int IdSwitch = Shader.PropertyToID("_GiRtSwitch"), IdSwitchP = Shader.PropertyToID("_GiRtSwitchP");
        private const float SwitchChange = 0.5f, SwitchKeep = 2f, SwitchM = 2f;
        private RenderTexture _stereoOut;
        private static readonly int IdStereoSrc = Shader.PropertyToID("_GiRtStereoSrc"), IdOtherLight = Shader.PropertyToID("_GiRtOtherLight"),
            IdOtherGeoP = Shader.PropertyToID("_GiRtOtherGeoP"), IdOtherGeoN = Shader.PropertyToID("_GiRtOtherGeoN"),
            IdOtherW2V = Shader.PropertyToID("_GiRtOtherW2V"), IdOtherUV = Shader.PropertyToID("_GiRtOtherUV"),
            IdStereoVar = Shader.PropertyToID("_GiRtStereoVar"), IdStereo = Shader.PropertyToID("_GiRtStereo");
        private int _eye;
        private bool _stereo;
        // Asked once per texture: GetNativeTexturePtr waits on the render thread.
        private IntPtr _pRayO, _pRayD, _pRayV;
        private readonly IntPtr[] _pOut = new IntPtr[5];   // the hit, the second bounce, the two lamp picks, the cell read (the native side's outputs)
        private readonly float[] _lamps = new float[GiLamps.Max * 16];
        private int _w, _h, _step, _rays, _cur;
        private readonly float[] _c = new float[32];   // (the native side reads 32: kConst)
        // the last frame's camera (the denoiser finds where each surface point was on its picture)
        private bool _hasPrev, _wasDenoised, _wasStab;
        private Matrix4x4 _prevW2V;
        private Vector4 _prevUV;
        // (0.24.142) VR (SPT-VR, MultiPass: each eye a render of its own, OnPreRender before each - as the VR mod's AO and jitter
        // use it): each eye traced, shaded and denoised as the flat screen is, with its own view, projection and position
        private bool _ambientZeroed, _saidVr, _saidVrOff;
        private const CameraEvent TraceEvt = CameraEvent.AfterLighting, LateEvt = CameraEvent.BeforeImageEffectsOpaque;   // (the view: see GiDebugView)
        // "Trace During Lighting": where the rays are made and the trace started. Not BeforeReflections: EFT puts its
        // water (and GPU Instancer its data) into the G-buffer there (AOSix's notes); by BeforeLighting it is whole.
        private const CameraEvent RaysEvt = CameraEvent.BeforeLighting;
        private static readonly int IdGrid = Shader.PropertyToID("_GiRtGrid"), IdParams = Shader.PropertyToID("_GiRtParams"),
            IdRayO = Shader.PropertyToID("_GiRtRayO"), IdRayD = Shader.PropertyToID("_GiRtRayD"), IdHit = Shader.PropertyToID("_GiRtHit"),
            IdRaw = Shader.PropertyToID("_GiRtRaw"), IdGeoP = Shader.PropertyToID("_GiRtGeoP"), IdGeoN = Shader.PropertyToID("_GiRtGeoN"),
            IdGeoPPrev = Shader.PropertyToID("_GiRtGeoPPrev"), IdGeoNPrev = Shader.PropertyToID("_GiRtGeoNPrev"), IdHist = Shader.PropertyToID("_GiRtHist"),
            IdHitD = Shader.PropertyToID("_GiRtHitD"), IdSrc = Shader.PropertyToID("_GiRtSrc"), IdBlur = Shader.PropertyToID("_GiRtBlur"),
            IdFastHist = Shader.PropertyToID("_GiRtFastHist"), IdMomHist = Shader.PropertyToID("_GiRtMomHist"), IdFastCur = Shader.PropertyToID("_GiRtFastCur"),
            IdMomCur = Shader.PropertyToID("_GiRtMomCur"), IdVar = Shader.PropertyToID("_GiRtVar"), IdRelax = Shader.PropertyToID("_GiRtRelax"),
            IdAtrous = Shader.PropertyToID("_GiRtAtrous"), IdWMax = Shader.PropertyToID("_GiRtWMax"), IdGuide = Shader.PropertyToID("_GiRtGuide"), IdViewDbg = Shader.PropertyToID("_GiRtViewDbg"), IdShadowG = Shader.PropertyToID("_GiRtShadowG"), IdEdge = Shader.PropertyToID("_GiRtEdge"),
            IdDen = Shader.PropertyToID("_GiRtDen"), IdPrevUV = Shader.PropertyToID("_GiRtPrevUV"), IdPrevW2V = Shader.PropertyToID("_GiRtPrevW2V"),
            IdLight = Shader.PropertyToID("_GiRtLight"), IdSunDir = Shader.PropertyToID("_GiSunDir"), IdCamPos = Shader.PropertyToID("_GiRtCamPos"),
            IdBounce = Shader.PropertyToID("_GiRtBounce"), IdPick = Shader.PropertyToID("_GiRtPick"), IdPick2 = Shader.PropertyToID("_GiRtPick2"), IdOpt = Shader.PropertyToID("_GiRtOpt"),
            IdCell = Shader.PropertyToID("_GiRtCell"), IdReuse = Shader.PropertyToID("_GiRtReuse"),
            IdResA = Shader.PropertyToID("_GiRtResA"), IdResB = Shader.PropertyToID("_GiRtResB"), IdResC = Shader.PropertyToID("_GiRtResC"),
            IdResD = Shader.PropertyToID("_GiRtResD"), IdResE = Shader.PropertyToID("_GiRtResE"),
            IdFast = Shader.PropertyToID("_GiRtFast"), IdAo = Shader.PropertyToID("_GiRtAo"), IdAoRaw = Shader.PropertyToID("_GiRtAoRaw"),
            IdAoHist = Shader.PropertyToID("_GiRtAoHist"), IdAoTex = Shader.PropertyToID("_GiRtAoTex"), IdRtLitFrame = Shader.PropertyToID("_GiRtLitFrame"),
            IdRtFrame = Shader.PropertyToID("_GiRtFrame"), IdSsrRayO = Shader.PropertyToID("_SsrRtRayO"), IdSsrRayD = Shader.PropertyToID("_SsrRtRayD"),
            IdSsrRayFrame = Shader.PropertyToID("_SsrRtRayFrame"), IdReflO = Shader.PropertyToID("_GiRtReflO"), IdReflD = Shader.PropertyToID("_GiRtReflD"),
            IdReflHit = Shader.PropertyToID("_GiRtReflHit"), IdReflPick = Shader.PropertyToID("_GiRtReflPick"), IdReflCell = Shader.PropertyToID("_GiRtReflCell"),
            IdRefl = Shader.PropertyToID("_GiRtRefl"), IdReflFrame = Shader.PropertyToID("_GiRtReflFrame"),
            IdAlb = Shader.PropertyToID("_GiRtAlb"), IdReflAlb = Shader.PropertyToID("_GiRtReflAlb"), IdSunRaw = Shader.PropertyToID("_GiRtSunRaw"),
            IdSunHist = Shader.PropertyToID("_GiRtSunHist"), IdSunTex = Shader.PropertyToID("_GiRtSunTex"), IdSunP = Shader.PropertyToID("_GiRtSunP"),
            IdSunGrid = Shader.PropertyToID("_GiRtSunGrid"), IdGiSunDir = Shader.PropertyToID("_GiSunDir"), IdDebug = Shader.PropertyToID("_GiRtDebug"),
            IdLampHist = Shader.PropertyToID("_GiRtLampHist"), IdLampP = Shader.PropertyToID("_GiRtLampP"), IdLampVis = Shader.PropertyToID("_GiLampVis"),
            IdLampVisW2V = Shader.PropertyToID("_GiLampVisW2V"), IdLampVisUV = Shader.PropertyToID("_GiLampVisUV"), IdLampVisSize = Shader.PropertyToID("_GiLampVisSize"),
            IdSunSkipAway = Shader.PropertyToID("_GiRtSunSkipAway"),
            IdShDn = Shader.PropertyToID("_GiRtShDn"), IdLampM2Hist = Shader.PropertyToID("_GiRtLampM2Hist"), IdDnSrc = Shader.PropertyToID("_GiDnSrc"),
            IdDnM2 = Shader.PropertyToID("_GiDnM2"), IdDnP = Shader.PropertyToID("_GiDnP"), IdLampTop3 = Shader.PropertyToID("_GiRtLampTop3"),
            IdLampTotal = Shader.PropertyToID("_GiRtLampTotal"), IdLampRemap = Shader.PropertyToID("_GiLampRemap"), IdLampResP = Shader.PropertyToID("_GiRtLampResP"),
            IdLampResCur = Shader.PropertyToID("_GiRtLampResCur"), IdLampResHist = Shader.PropertyToID("_GiRtLampResHist"), IdLampInfo = Shader.PropertyToID("_GiRtLampInfoTex");

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            UseEye(0);
        }

        private void OnDisable() => Detach();

        // (0.24.145) the eye whose render comes next: its histories and last camera into the fields (0 = the left eye - and the
        // flat screen's - 1 the right)
        private void UseEye(int e)
        {
            _eye = e;
            EyeHist h = _eyes[e];
            _geoP = h.geoP; _geoN = h.geoN; _accum = h.accum; _fast = h.fast; _mom = h.mom; _stab = h.stab;
            _resA = h.resA; _resB = h.resB; _resC = h.resC; _resD = h.resD; _resE = h.resE;
            _cur = h.cur; _hasPrev = h.hasPrev; _wasDenoised = h.wasDenoised; _wasAlbView = h.wasAlbView; _wasStab = h.wasStab;
            _prevW2V = h.prevW2V; _prevUV = h.prevUV;
        }

        // ... and what this eye's render leaves for its next one
        private void KeepEye()
        {
            EyeHist h = _eyes[_eye];
            h.cur = _cur; h.hasPrev = _hasPrev; h.wasDenoised = _wasDenoised; h.wasAlbView = _wasAlbView; h.wasStab = _wasStab;
            h.prevW2V = _prevW2V; h.prevUV = _prevUV;
        }

        private void OnDestroy()
        {
            Detach();
            _cbTrace?.Release();
            _cbLate?.Release();
            _cbRays?.Release();
            _cbTrace = _cbLate = _cbRays = null;
            _cbSunBefore?.Release();
            _cbSunAfter?.Release();
            _cbSunBefore = _cbSunAfter = null;
            FreeTargets();
            if (_mat != null) Destroy(_mat);
            _mat = null;
        }

        // The sun light's command buffers come off after this camera's render (they are the light's, run for every camera).
        private void OnPostRender()
        {
            DetachSun();
            // (0.24.142) VR: GiCamera does not run there - the game's ambient was taken out for this eye's render here
            if (_ambientZeroed) GameAmbient.Restore();
            _ambientZeroed = false;
        }

        private void DetachSun()
        {
            if (_sunLight != null)
            {
                if (_cbSunBefore != null) _sunLight.RemoveCommandBuffer(LightEvent.BeforeScreenspaceMask, _cbSunBefore);
                if (_cbSunAfter != null) _sunLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, _cbSunAfter);
            }
            _sunLight = null;
        }

        private void Detach()
        {
            DetachSun();
            if (_traceOn && _cam != null)
            {
                _cam.RemoveCommandBuffer(TraceEvt, _cbTrace);
                _cam.RemoveCommandBuffer(LateEvt, _cbLate);
                _cam.RemoveCommandBuffer(RaysEvt, _cbRays);
            }
            _traceOn = false;
            _hasPrev = false;
            foreach (EyeHist e in _eyes) e.hasPrev = false;
            GiRt.Applying = false;   // frames were missed: what the history holds is no longer last frame's
        }

        private void FreeTargets()
        {
            foreach (RenderTexture rt in new[] { _rayO, _rayD, _rayV, _hit, _bounce, _pick, _pick2, _cell, _raw, _hitD, _blur, _final, _pre, _tmpLight, _var[0], _var[1] })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _rayO = _rayD = _rayV = _hit = _bounce = _pick = _pick2 = _cell = _raw = _hitD = _blur = _final = _pre = null;
            _tmpLight = _var[0] = _var[1] = null;
            foreach (EyeHist e in _eyes)
                foreach (RenderTexture[] pair in e.All)
                    for (int i = 0; i < 2; i++)
                    {
                        if (pair[i] != null) { pair[i].Release(); Destroy(pair[i]); }
                        pair[i] = null;
                    }
            foreach (EyeHist e in _eyes)
            {
                if (e.light != null) { e.light.Release(); Destroy(e.light); }
                e.light = e.lightGeoP = e.lightGeoN = null;
                e.lightFrame = -10;
            }
            if (_stereoOut != null) { _stereoOut.Release(); Destroy(_stereoOut); }
            _stereoOut = null;
            FreeAo();
            FreeRefl();
            FreeSun();
            FreeLamp();
            if (_alb != null) { _alb.Release(); Destroy(_alb); }
            _alb = null;
            _pAlb = IntPtr.Zero;
            _pRayO = _pRayD = _pRayV = IntPtr.Zero;
            for (int i = 0; i < _pOut.Length; i++) _pOut[i] = IntPtr.Zero;
        }

        private void FreeAo()
        {
            foreach (RenderTexture rt in new[] { _aoO, _aoN, _aoRaw, _aoFinal, _aoHist[0], _aoHist[1] })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _aoO = _aoN = _aoRaw = _aoFinal = _aoHist[0] = _aoHist[1] = null;
            _pAoO = _pAoN = _pAoRaw = IntPtr.Zero;
            _hadAo = false;
        }

        private void FreeRefl()
        {
            foreach (RenderTexture rt in new[] { _reflHit, _reflPick, _reflCell, _reflOut, _reflAlb })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _reflHit = _reflPick = _reflCell = _reflOut = _reflAlb = null;
            _reflSrcO = _reflSrcD = null;     // (ReflectSix's: not ours to free)
            _pReflO = _pReflD = _pReflHit = _pReflPick = _pReflCell = _pReflAlb = IntPtr.Zero;
        }

        private void FreeLamp()
        {
            foreach (RenderTexture rt in new[] { _lampO, _lampHist[0], _lampHist[1], _lampM2[0], _lampM2[1], _lampDn[0], _lampDn[1],
                                                 _lampResCur, _lampInfo, _lampResHist[0], _lampResHist[1] })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _lampO = _lampHist[0] = _lampHist[1] = _lampM2[0] = _lampM2[1] = _lampDn[0] = _lampDn[1] = null;
            _lampResCur = _lampInfo = _lampResHist[0] = _lampResHist[1] = null;
            _hadLampRes = false;
            _pLampO = IntPtr.Zero;
            _hadLamp = false;
        }

        // (0.24.115) the lamps' shadow rays, one per 2 x 2 of the picture (half floats: the only kind the D3D12 side can share), and
        // the per-pixel share of their light that gets through
        private bool EnsureLamp(int w, int h)
        {
            int sw = (w + 1) / 2, sh = (h + 1) / 2;
            if (Created(_lampO) && Created(_lampHist[0]) && Created(_lampHist[1]) && Created(_lampM2[0]) && Created(_lampM2[1])
                && _lampO.width == sw * 2 && _lampO.height == sh && _lampHist[0].width == w && _lampHist[0].height == h) return true;
            FreeLamp();
            // (0.24.154) twice as wide: the left half the ray starts (RTLAMPRAYS), the right half the lamps RTLAMPSEL chose for them
            // (Reuse Lamp Choices) - one texture to the D3D12 side as before, no new hand-over
            _lampO = Target("TraceSixRT Lamp Ray Starts", sw * 2, sh, RenderTextureFormat.ARGBHalf);
            for (int i = 0; i < 2; i++) _lampHist[i] = Target("TraceSixRT Lamp Shadow " + i, w, h, RenderTextureFormat.ARGBHalf);
            for (int i = 0; i < 2; i++) _lampM2[i] = Target("TraceSixRT Lamp Shadow Moments " + i, w, h, RenderTextureFormat.RHalf);
            _pLampO = _lampO.GetNativeTexturePtr();
            return true;
        }

        // (0.24.115) "Ray-Traced Direct Lamp Shadows": the bundle's two passes, and TraceSix's copy of the light pass in use with the
        // part that lays the share into the lamps' light (its V2)
        private bool LampsWanted() => GiConfig.RtDirectLamps.Value && !GiTest.RtNoDirectLamps && _passLampRays >= 0 && _passLampTemporal >= 0
                                      && GiAssets.LightVis && GiLightPass.Active;

        private void FreeSun()
        {
            foreach (RenderTexture rt in new[] { _sunO, _sunRaw, _sunHist[0], _sunHist[1], _sunDn[0], _sunDn[1] })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _sunO = _sunRaw = _sunHist[0] = _sunHist[1] = _sunDn[0] = _sunDn[1] = null;
            _pSunO = _pSunRaw = IntPtr.Zero;
            _hadSun = false;
        }

        // The sun shadow's textures: its rays and their shadow one per 2 x 2 of the picture, the accumulated shadow per pixel.
        private bool EnsureSun(int w, int h)
        {
            int sw = (w + 1) / 2, sh = (h + 1) / 2;
            bool ok = Created(_sunO) && Created(_sunRaw) && Created(_sunHist[0]) && Created(_sunHist[1]) && _sunO.width == sw && _sunO.height == sh
                      && _sunHist[0].width == w && _sunHist[0].height == h;
            if (ok) return true;
            FreeSun();
            _sunO = Target("TraceSixRT Sun Ray Starts", sw, sh, RenderTextureFormat.ARGBHalf);
            _sunRaw = Target("TraceSixRT Sun Traced", sw, sh, RenderTextureFormat.ARGBHalf);
            for (int i = 0; i < 2; i++) _sunHist[i] = Target("TraceSixRT Sun Shadow " + i, w, h, RenderTextureFormat.ARGBHalf);
            _pSunO = _sunO.GetNativeTexturePtr();
            _pSunRaw = _sunRaw.GetNativeTexturePtr();
            return true;
        }

        // (0.24.151) "Denoise Ray-Traced Shadows": RTSHADOWDN's two targets at the picture's size (made when first wanted)
        private static bool EnsureDn(RenderTexture[] pp, string name, int w, int h)
        {
            if (Created(pp[0]) && Created(pp[1]) && pp[0].width == w && pp[0].height == h) return true;
            for (int i = 0; i < 2; i++)
            {
                if (pp[i] != null) { pp[i].Release(); Destroy(pp[i]); }
                pp[i] = Target(name + " " + i, w, h, RenderTextureFormat.ARGBHalf);
            }
            return Created(pp[0]) && Created(pp[1]);
        }

        private bool ShadowDnWanted() => GiConfig.RtShadowDn.Value && !GiTest.RtNoShadowDn && _passShadowDn >= 0;

        // (0.24.115 / 0.24.155) The lamps' shadow rays (collected) into the share of the lamps' light that gets through, per pixel,
        // into b: the accumulation, ReSTIR DI's kept reservoirs, the denoiser; and that share with the camera it was made with,
        // for TraceSix's copy of the light pass. The state the next frame's rays pass reads is taken over by ApplyLampState.
        private void LampShare(CommandBuffer b, int w, int h, bool lampRes, Vector4 sunP, Matrix4x4 w2v, Matrix4x4 proj)
        {
            int lprev = _lampCur;
            _lampCur ^= 1;
            b.SetGlobalVector(IdSunP, sunP);
            b.SetGlobalVector(IdSunGrid, new Vector4(_sunO.width, _sunO.height, 0f, 0f));
            b.SetGlobalTexture(IdSunRaw, _sunRaw);
            b.SetGlobalVector(IdLampP, new Vector4(!_hasPrev || !_hadLamp ? 1f : 0f, 0f, 0f, 0f));
            b.SetGlobalTexture(IdLampHist, _lampHist[lprev]);
            b.SetGlobalTexture(IdLampM2Hist, _lampM2[lprev]);
            bool ldn = ShadowDnWanted();
            b.SetGlobalFloat(IdShDn, ldn ? 1f : 0f);
            // (0.24.154) ReSTIR DI: this frame's reservoirs kept for the next, each one's W zeroed where its lamp turned out blocked
            _pendHadRes = lampRes;
            _pendResIdx = -1;
            if (lampRes)
            {
                int rcur = _lampResIdx ^ 1;
                b.SetGlobalTexture(IdLampResCur, _lampResCur);
                b.SetRenderTarget(_lampResHist[rcur]);
                b.DrawProcedural(Matrix4x4.identity, _mat, _passLampResFin, MeshTopology.Triangles, 3);
                b.SetGlobalTexture(IdLampInfo, _lampInfo);
                _pendResIdx = rcur;
                _pendResP = new Vector2(sunP.x, sunP.y);
            }
            // which kind of lamp answer the kernel gave: 0 one random pick, 1 two + one of the rest (bit 8), 2 ReSTIR DI (bit 16)
            b.SetGlobalFloat(IdLampTop3, lampRes ? 2f : GiAssets.LampTop3 ? 1f : 0f);
            b.SetRenderTarget(new RenderTargetIdentifier[] { _lampHist[_lampCur], _lampM2[_lampCur] }, _lampHist[_lampCur]);
            b.DrawProcedural(Matrix4x4.identity, _mat, _passLampTemporal, MeshTopology.Triangles, 3);
            // (0.24.151) filtered for the light pass; the history stays as accumulated
            RenderTexture lampVis = _lampHist[_lampCur];
            if (ldn && EnsureDn(_lampDn, "TraceSixRT Lamp Shadow Denoised", w, h))
            {
                ShadowDn(b, _lampHist[_lampCur], _lampM2[_lampCur], _lampDn, true);
                lampVis = _lampDn[0];
            }
            b.SetGlobalTexture(IdLampVis, lampVis);
            b.SetGlobalMatrix(IdLampVisW2V, w2v);
            b.SetGlobalVector(IdLampVisUV, UVToView(proj));
            b.SetGlobalVector(IdLampVisSize, new Vector4(lampVis.width, lampVis.height, 0f, 0f));
            GiRt.LampVisFrame = Time.frameCount;
        }

        // ReSTIR DI's state for the next frame - after this frame's rays pass (OnPreRender) has read the last frame's
        private void ApplyLampState()
        {
            if (_pendResIdx >= 0) { _lampResIdx = _pendResIdx; _lampResP = _pendResP; }
            _hadLampRes = _pendHadRes;
            _pendResIdx = -1;
        }

        // (0.24.154) "Reuse Lamp Choices": the bundle has the passes and the switch is on
        private bool LampRestirWanted() => GiConfig.RtLampReuse.Value && !GiTest.RtNoLampReuse && GiAssets.LampRestir
                                           && _passLampRis >= 0 && _passLampSel >= 0 && _passLampResFin >= 0;

        // Its textures at the traced grid's size: the reservoirs (32-bit: W spans orders of magnitude) and the lamps' choice
        private bool EnsureLampRes(int sw, int sh)
        {
            if (Created(_lampResCur) && Created(_lampInfo) && Created(_lampResHist[0]) && Created(_lampResHist[1])
                && _lampResCur.width == sw && _lampResCur.height == sh) return true;
            foreach (RenderTexture rt in new[] { _lampResCur, _lampInfo, _lampResHist[0], _lampResHist[1] })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _lampResCur = Target("TraceSixRT Lamp Reservoirs", sw, sh, RenderTextureFormat.ARGBFloat);
            _lampInfo = Target("TraceSixRT Lamp Choice", sw, sh, RenderTextureFormat.ARGBHalf);
            for (int i = 0; i < 2; i++) _lampResHist[i] = Target("TraceSixRT Lamp Reservoirs Kept " + i, sw, sh, RenderTextureFormat.ARGBFloat);
            _hadLampRes = false;
            return Created(_lampResCur) && Created(_lampInfo) && Created(_lampResHist[0]) && Created(_lampResHist[1]);
        }

        // Three passes of RTSHADOWDN (steps 1, 2, 4 pixels): src (a temporal pass's output) -> pp[0] -> pp[1] -> pp[0]. m2: the
        // lamps' second moments (null: the sun's, in src's w); keepW: carry src's w through (the lamps' "some lamp lights it").
        private void ShadowDn(CommandBuffer b, RenderTexture src, RenderTexture m2, RenderTexture[] pp, bool keepW)
        {
            if (m2 != null) b.SetGlobalTexture(IdDnM2, m2);
            RenderTexture from = src;
            for (int i = 0; i < 3; i++)
            {
                RenderTexture to = pp[i == 1 ? 1 : 0];
                b.SetGlobalTexture(IdDnSrc, from);
                b.SetGlobalVector(IdDnP, new Vector4(1 << i, i == 0 ? 1f : 0f, m2 == null ? 1f : 0f, keepW ? 1f : 0f));
                b.SetRenderTarget(to);
                b.DrawProcedural(Matrix4x4.identity, _mat, _passShadowDn, MeshTopology.Triangles, 3);
                from = to;
            }
        }

        // EFT's distant-shadow mask, when it precomputes one at the picture's size (else null: Unity's mask only).
        private RenderTexture DistantMask(int w, int h)
        {
            if (_ds == null && Time.unscaledTime >= _dsNextFind)
            {
                _dsNextFind = Time.unscaledTime + 5f;
                _ds = UnityEngine.Object.FindObjectOfType<DistantShadow>();
            }
            if (_ds == null || !_ds.PreComputeMask) return null;
            RenderTexture rt = _ds._shadowMaskFull;
            return rt != null && rt.IsCreated() && rt.width == w && rt.height == h ? rt : null;
        }

        // ReflectSix's ray textures (their native pointers asked once per texture) and ours for their ends, at their size.
        private bool EnsureRefl(RenderTexture o, RenderTexture d)
        {
            if (o != _reflSrcO) { _reflSrcO = o; _pReflO = o.GetNativeTexturePtr(); }
            if (d != _reflSrcD) { _reflSrcD = d; _pReflD = d.GetNativeTexturePtr(); }
            bool ok = Created(_reflHit) && Created(_reflPick) && Created(_reflCell) && Created(_reflOut) && Created(_reflAlb) && _reflHit.width == o.width && _reflHit.height == o.height;
            if (ok) return true;
            foreach (RenderTexture rt in new[] { _reflHit, _reflPick, _reflCell, _reflOut, _reflAlb })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _reflHit = Target("TraceSixRT Reflection Ends", o.width, o.height, RenderTextureFormat.ARGBHalf);
            _reflPick = Target("TraceSixRT Reflection Lamps", o.width, o.height, RenderTextureFormat.ARGBHalf);
            _reflCell = Target("TraceSixRT Reflection Cells", o.width, o.height, RenderTextureFormat.ARGBHalf);
            _reflOut = Target("TraceSixRT Reflections", o.width, o.height, RenderTextureFormat.ARGBHalf);
            _reflAlb = Target("TraceSixRT Reflection Colours", o.width, o.height * 2, RenderTextureFormat.ARGB32);   // (0.24.155) the lower half: the glow
            _pReflAlb = _reflAlb.GetNativeTexturePtr();
            _pReflHit = _reflHit.GetNativeTexturePtr();
            _pReflPick = _reflPick.GetNativeTexturePtr();
            _pReflCell = _reflCell.GetNativeTexturePtr();
            return true;
        }

        // The ray-traced AO's textures at the picture's size (made on first use, again when the size changes).
        private bool EnsureAo(int w, int h)
        {
            bool ok = Created(_aoO) && Created(_aoN) && Created(_aoRaw) && Created(_aoFinal) && Created(_aoHist[0]) && Created(_aoHist[1])
                      && _aoO.width == w && _aoO.height == h;
            if (ok) return true;
            FreeAo();
            _aoO = Target("TraceSixRT AO Ray Starts", w, h, RenderTextureFormat.ARGBHalf);
            _aoN = Target("TraceSixRT AO Normals", w, h, RenderTextureFormat.ARGBHalf);
            _aoRaw = Target("TraceSixRT AO Traced", w, h, RenderTextureFormat.ARGBHalf);
            _aoFinal = Target("TraceSixRT AO", w, h, RenderTextureFormat.RHalf);
            for (int i = 0; i < 2; i++) _aoHist[i] = Target("TraceSixRT AO Accumulated " + i, w, h, RenderTextureFormat.ARGBHalf);
            _pAoO = _aoO.GetNativeTexturePtr();
            _pAoN = _aoN.GetNativeTexturePtr();
            _pAoRaw = _aoRaw.GetNativeTexturePtr();
            return true;
        }

        internal static RenderTexture Target(string name, int w, int h, RenderTextureFormat f)
        {
            var rt = new RenderTexture(w, h, 0, f, RenderTextureReadWrite.Linear)
                { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            rt.Create();
            return rt;
        }

        internal static bool Created(RenderTexture rt) => rt != null && rt.IsCreated();

        private bool AllCreated()
        {
            foreach (RenderTexture rt in new[] { _rayO, _rayD, _rayV, _hit, _bounce, _pick, _pick2, _cell, _raw, _hitD, _blur, _final, _pre, _tmpLight, _var[0], _var[1], _alb })
                if (!Created(rt)) return false;
            for (int e = 0; e < (_stereo ? 2 : 1); e++)
                foreach (RenderTexture[] pair in _eyes[e].All)
                    if (!Created(pair[0]) || !Created(pair[1])) return false;
            return true;
        }

        // (0.24.157) the stereo blend's textures at the traced grid's size: the shared output, and this eye's kept light
        private bool EnsureStereo(EyeHist me, int gw, int gh)
        {
            if (!Created(_stereoOut) || _stereoOut.width != gw || _stereoOut.height != gh)
            {
                if (_stereoOut != null) { _stereoOut.Release(); Destroy(_stereoOut); }
                _stereoOut = Target("TraceSixRT Stereo Blended Light", gw, gh, RenderTextureFormat.ARGBHalf);
            }
            if (!Created(me.light) || me.light.width != gw || me.light.height != gh)
            {
                if (me.light != null) { me.light.Release(); Destroy(me.light); }
                me.light = Target("TraceSixRT Eye Light " + (me == _eyes[0] ? "(left)" : "(right)"), gw, gh, RenderTextureFormat.ARGBHalf);
                me.lightFrame = -10;
            }
            return Created(_stereoOut) && Created(me.light);
        }

        // GiCamera's _GiUVToView for a projection: view.xy = (uv * xy + zw) * depth.
        internal static Vector4 UVToView(Matrix4x4 p) => new Vector4(2f / p.m00, 2f / p.m11, (p.m02 - 1f) / p.m00, (p.m12 - 1f) / p.m11);

        // The rays' ends are lit from the light volumes: the indirect light must be running on this camera.
        // (0.24.142) a stereo camera too, with "Ray Tracing In VR"
        private bool RtOn() => GiConfig.RtTest.Value && !GiTest.RtOff && GiRt.Ready && GiConfig.On && GiProbes.Ready && GiAssets.Probes != null
                               && (!_cam.stereoEnabled || GiConfig.VrSupport.Value && GiConfig.RtVr.Value) && _cam.allowHDR && _cam.actualRenderingPath == RenderingPath.DeferredShading;

        // (0.24.95) "Ray-Traced Sun Shadows": Unity takes a light's command buffers when it culls - EFT's ContactShadows puts
        // its own on in OnPreCull and takes them off in OnPreRender for that reason. Put on in OnPreRender (0.24.89-0.24.94)
        // they were never taken: the sun rays were traced and nothing collected them. So the sun's two buffers are made
        // and put on the light here (OnPreRender takes them off, and gives the trace what was decided here).
        private void OnPreCull()
        {
            _sunFrame = false;
            _lampFrame = false;
            _lampEarly = false;
            if (_mat == null || _passSunRays < 0 || _passSunTemporal < 0 || _passSunApply < 0 || !RtOn())
            {
                GiSunWorld.Missed("the ray tracing is not running this frame");
                return;
            }
            // (0.24.142) VR: the light only for now - the sun's and the lamps' rays (their masks, their histories per eye) are
            // flat screen only; the game's own shadows stay
            if (_cam.stereoEnabled)
            {
                GiSunWorld.Missed("VR: the sun's rays are flat screen only for now");
                return;
            }
            Light sunL = GiProbes.Sun;
            int w = _cam.pixelWidth, h = _cam.pixelHeight;
            bool sunOn = GiConfig.RtSunShadows.Value && !GiTest.RtNoSun && !GiTest.RtLate && !GiRaid.NoSunMap
                       && sunL != null && sunL.isActiveAndEnabled && sunL.shadows != LightShadows.None
                       && Shader.GetGlobalVector(IdGiSunDir).w > 0.5f && EnsureSun(w, h);
            // (0.24.116) why not, for RayTracedWorld's log when it gives the game its shadows back
            if (!sunOn)
                GiSunWorld.Missed(!GiConfig.RtSunShadows.Value || GiTest.RtNoSun ? "Ray-Traced Sun Shadows is off"
                    : GiTest.RtLate ? "SixBench traces after the lighting"
                    : GiRaid.NoSunMap ? "this map never has ray-traced sun shadows (Factory, the Lab, the Labyrinth)"
                    : sunL == null ? "no sun light found"
                    : !sunL.isActiveAndEnabled ? "the sun light is switched off"
                    : sunL.shadows == LightShadows.None ? "the game gave the sun light no shadows"
                    : Shader.GetGlobalVector(IdGiSunDir).w <= 0.5f ? "the sun is down or lights nothing"
                    : "its textures could not be made");
            // (0.24.115) the lamps' shadow rays go through the sun's (its textures, its pixel of each 2 x 2, its D3D12 kernel) - at
            // night too, with the sun's part off
            bool lampOn = LampsWanted() && EnsureSun(w, h) && EnsureLamp(w, h);
            if (!sunOn && !lampOn) return;
            int slot = Time.frameCount & 3, sunPhase = Time.frameCount & 3;   // (OnPreRender's slot: the same frame)
            _sunP = new Vector4((sunPhase == 1 || sunPhase == 2) ? 1f : 0f, (sunPhase == 1 || sunPhase == 3) ? 1f : 0f, (!_hasPrev || !_hadSun) ? 1f : 0f, 0f);
            _lampFrame = lampOn;
            if (!sunOn) return;
            int prev = _sunCur;
            _sunCur ^= 1;
            // Before the sun's shadow mask is drawn (the game has drawn its shadow maps meanwhile, the sun rays traced
            // beside that), D3D11 collects it (24 + slot) and RTSUNTEMPORAL accumulates it per pixel; after the mask is
            // drawn, RTSUNAPPLY puts it in (the darker of the two) - and into EFT's distant-shadow mask when it has one.
            if (_cbSunBefore == null) _cbSunBefore = new CommandBuffer { name = "TraceSix RT Sun Collect" };
            if (_cbSunAfter == null) _cbSunAfter = new CommandBuffer { name = "TraceSix RT Sun Into The Mask" };
            CommandBuffer sb = _cbSunBefore, sa = _cbSunAfter;
            sb.Clear();
            if (!GiTest.RtNoTrace) sb.IssuePluginEvent(GiRt.Func, 24 + slot);
            sb.SetGlobalVector(IdSunP, _sunP);
            sb.SetGlobalVector(IdSunGrid, new Vector4(_sunO.width, _sunO.height, 0f, 0f));
            sb.SetGlobalTexture(IdSunRaw, _sunRaw);
            sb.SetGlobalTexture(IdSunHist, _sunHist[prev]);
            bool dn = ShadowDnWanted();
            sb.SetGlobalFloat(IdShDn, dn ? 1f : 0f);
            sb.SetRenderTarget(_sunHist[_sunCur]);
            sb.DrawProcedural(Matrix4x4.identity, _mat, _passSunTemporal, MeshTopology.Triangles, 3);
            // (0.24.151) filtered for the mask; the history stays as accumulated
            if (dn && EnsureDn(_sunDn, "TraceSixRT Sun Shadow Denoised", w, h))
            {
                ShadowDn(sb, _sunHist[_sunCur], null, _sunDn, false);
                sb.SetGlobalTexture(IdSunTex, _sunDn[0]);
            }
            else sb.SetGlobalTexture(IdSunTex, _sunHist[_sunCur]);
            sa.Clear();
            GiSunWorld.Want(GiConfig.RtSunShadowMode.Value == RtSunMode.RayTracedWorld);   // (the sun's buffers go on: they can replace it)
            sa.DrawProcedural(Matrix4x4.identity, _mat, _passSunApply, MeshTopology.Triangles, 3);   // (into the mask: the active target)
            RenderTexture far = DistantMask(w, h);
            if (far != null)
            {
                sa.SetRenderTarget(far);
                // (RayTracedWorld too: EFT draws its distant shadow from a list of the map's meshes made once, so switching
                // their shadows off leaves it - its part goes, the rays' takes it over)
                if (GiConfig.RtSunShadowMode.Value == RtSunMode.RayTracedWorld) sa.ClearRenderTarget(false, true, Color.white);
                sa.DrawProcedural(Matrix4x4.identity, _mat, _passSunApply, MeshTopology.Triangles, 3);
            }
            DetachSun();
            sunL.AddCommandBuffer(LightEvent.BeforeScreenspaceMask, sb);
            sunL.AddCommandBuffer(LightEvent.AfterScreenspaceMask, sa);
            _sunLight = sunL;
            _sunFrame = true;
            if (!_saidSun)
            {
                _saidSun = true;
                Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: sun shadows ray-traced (" + _sunO.width + "x" + _sunO.height + " rays) into the shadow mask of '" + sunL.name + "'"
                    + (far != null ? " and EFT's distant-shadow mask" : " (EFT precomputes no distant-shadow mask here: Unity's only)") + ", put on the light at culling.");
            }
        }

        private void OnPreRender()
        {
            bool on = RtOn();
            if (on && _mat == null)
            {
                _mat = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
                _passRays = _mat.FindPass("RTRAYS");
                _passShade = _mat.FindPass("RTSHADE");
                _passTemporal = _mat.FindPass("RTTEMPORAL");
                _passBlur = _mat.FindPass("RTBLUR");
                _passClamp = _mat.FindPass("RTHISTCLAMP");   // ("Variance-Guided Denoising": without them, the earlier denoiser)
                _passAtrous = _mat.FindPass("RTATROUS");
                _passView = _mat.FindPass("RTVIEW");
                _passApply = _mat.FindPass("RTAPPLY");
                _passOpen = _mat.FindPass("CELLOPEN");   // ("Cell Reads Check Visibility": without it, not checked)
                _passAoRays = _mat.FindPass("RTAORAYS");   // ("Ambient Occlusion" = RayTraced: without them, not traced)
                _passAoTemporal = _mat.FindPass("RTAOTEMPORAL");
                _passAoBlur = _mat.FindPass("RTAOBLUR");
                _passReflShade = _mat.FindPass("RTREFLSHADE");   // (ReflectSix's off-screen reflections: without it, none)
                _passSunRays = _mat.FindPass("RTSUNRAYS");         // ("Ray-Traced Sun Shadows": without them, none)
                _passSunTemporal = _mat.FindPass("RTSUNTEMPORAL");
                _passSunApply = _mat.FindPass("RTSUNAPPLY");
                _passShadowDn = _mat.FindPass("RTSHADOWDN");      // ("Denoise Ray-Traced Shadows": without it, the shadows as accumulated)
                _passLampRis = _mat.FindPass("RTLAMPRIS");        // ("Reuse Lamp Choices": without these, the kernel picks the rest itself)
                _passLampSel = _mat.FindPass("RTLAMPSEL");
                _passLampResFin = _mat.FindPass("RTLAMPRESFIN");
                _passLampRays = _mat.FindPass("RTLAMPRAYS");       // ("Ray-Traced Direct Lamp Shadows": without them, none)
                _passLampTemporal = _mat.FindPass("RTLAMPTEMPORAL");
                _passPre = _mat.FindPass("RTPREBLUR");
                _passStab = _mat.FindPass("RTSTABILIZE");
                _passStereo = _mat.FindPass("RTSTEREO");          // ("Stereo Blend In VR": without it, each eye's own light)
                _mat.SetFloat("_GiSkyTest", 5f);   // (RTAPPLY, as GiCamera's last pass: not the sky's pixels)
                if (_passRays < 0 || _passShade < 0 || _passTemporal < 0 || _passBlur < 0 || _passView < 0 || _passApply < 0)
                    Plugin.MyLog.LogWarning("[TraceSix] Ray tracing: the tracesix bundle lacks one of its passes (RTRAYS / RTSHADE / RTTEMPORAL / RTBLUR / RTVIEW / RTAPPLY). Rebuild the bundle.");
            }
            if (_cam.stereoEnabled && !on && !_saidVrOff && GiConfig.VrSupport.Value && GiConfig.RtVr.Value && GiConfig.RtTest.Value && GiRt.Ready)
            {
                _saidVrOff = true;
                Plugin.MyLog.LogWarning("[TraceSix] Ray tracing in VR: not on the VR camera '" + _cam.name + "' - HDR " + _cam.allowHDR + ", rendering path "
                    + _cam.actualRenderingPath + " (needs HDR, DeferredShading), the light volumes " + (GiProbes.Ready ? "up" : "not up") + ".");
            }
            if (!on || _passRays < 0 || _passShade < 0 || _passTemporal < 0 || _passBlur < 0 || _passView < 0 || _passApply < 0)
            {
                Detach();
                return;
            }
            // (0.24.146) VR: this eye's histories in
            bool stereo = _cam.stereoEnabled;
            var eye = _cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            int eyeIdx = stereo && eye == Camera.StereoscopicEye.Right ? 1 : 0;
            UseEye(eyeIdx);
            // For ReflectSix: the rays run on this camera this frame - it makes its off-screen reflection rays for them
            // (flat screen only: in VR it keeps its voxel march)
            if (!stereo) Shader.SetGlobalFloat(IdRtFrame, Time.frameCount);
            int w = _cam.pixelWidth, h = _cam.pixelHeight, step = Mathf.Clamp(GiConfig.RtStep.Value, 1, 4), rays = GiTest.RtRays > 0 ? GiTest.RtRays : Mathf.Clamp(GiConfig.RtRays.Value, 1, 16);   // (GiTest: SixBench's detail run)
            int gw = (w + step - 1) / step, gh = (h + step - 1) / step;
            if (!AllCreated() || w != _w || h != _h || step != _step || rays != _rays || stereo != _stereo)
            {
                _stereo = stereo;
                Detach();   // the buffers name the textures: made anew below
                FreeTargets();
                _rayO = Target("TraceSixRT Ray Starts", gw, gh * rays, RenderTextureFormat.ARGBHalf);   // camera-relative: 32-bit RGBA will not share with D3D12
                _rayD = Target("TraceSixRT Ray Directions", gw, gh * rays, RenderTextureFormat.ARGBHalf);
                _rayV = Target("TraceSixRT Reuse Ray Ends", gw, gh * rays, RenderTextureFormat.ARGBHalf);
                _hit = Target("TraceSixRT Ray Ends", gw, gh * rays, RenderTextureFormat.ARGBHalf);
                _bounce = Target("TraceSixRT Second Bounce", gw, gh * rays, RenderTextureFormat.ARGBHalf);
                _pick = Target("TraceSixRT Lamp Picks", gw, gh * rays, RenderTextureFormat.ARGBHalf);
                _pick2 = Target("TraceSixRT Second Lamp Picks", gw, gh * rays, RenderTextureFormat.ARGBHalf);
                _cell = Target("TraceSixRT Cell Reads", gw, gh * rays, RenderTextureFormat.ARGBHalf);
                _alb = Target("TraceSixRT Hit Colours", gw, gh * rays * 2, RenderTextureFormat.ARGB32);   // (0.24.155) the lower half: the hits' glow
                _pAlb = _alb.GetNativeTexturePtr();
                _raw = Target("TraceSixRT Raw Light", gw, gh, RenderTextureFormat.ARGBHalf);
                _hitD = Target("TraceSixRT Ray Lengths", gw, gh, RenderTextureFormat.RHalf);
                _blur = Target("TraceSixRT Blurred Once", gw, gh, RenderTextureFormat.ARGBHalf);
                _final = Target("TraceSixRT Light", gw, gh, RenderTextureFormat.ARGBHalf);
                _pre = Target("TraceSixRT Pre-Blurred Light", gw, gh, RenderTextureFormat.ARGBHalf);   // (1.0.0) "Pre-Blur"
                _tmpLight = Target("TraceSixRT Accumulated Unclamped", gw, gh, RenderTextureFormat.ARGBHalf);
                for (int i = 0; i < 2; i++) _var[i] = Target("TraceSixRT Variance " + i, gw, gh, RenderTextureFormat.RHalf);
                // (0.24.145) the histories: each eye's in VR
                for (int e = 0; e < (stereo ? 2 : 1); e++)
                {
                    EyeHist eh = _eyes[e];
                    string tag = stereo ? (e == 0 ? " (left)" : " (right)") : "";
                    for (int i = 0; i < 2; i++)
                    {
                        eh.geoP[i] = Target("TraceSixRT Surface Points " + i + tag, gw, gh, RenderTextureFormat.ARGBFloat);
                        eh.geoN[i] = Target("TraceSixRT Surface Facings " + i + tag, gw, gh, RenderTextureFormat.ARGBHalf);
                        eh.accum[i] = Target("TraceSixRT Accumulated " + i + tag, gw, gh, RenderTextureFormat.ARGBHalf);
                        eh.fast[i] = Target("TraceSixRT Fast History " + i + tag, gw, gh, RenderTextureFormat.ARGBHalf);
                        eh.mom[i] = Target("TraceSixRT Moments " + i + tag, gw, gh, RenderTextureFormat.ARGBHalf);
                        eh.resA[i] = Target("TraceSixRT Kept Sample Points " + i + tag, gw, gh, RenderTextureFormat.ARGBFloat);
                        eh.resB[i] = Target("TraceSixRT Kept Sample Light " + i + tag, gw, gh, RenderTextureFormat.ARGBHalf);
                        eh.resC[i] = Target("TraceSixRT Kept Sample Facings " + i + tag, gw, gh, RenderTextureFormat.ARGBHalf);
                        eh.resD[i] = Target("TraceSixRT Spread Sample Points " + i + tag, gw, gh, RenderTextureFormat.ARGBFloat);
                        eh.resE[i] = Target("TraceSixRT Spread Sample Light " + i + tag, gw, gh, RenderTextureFormat.ARGBHalf);
                        eh.stab[i] = Target("TraceSixRT Stabilized Light " + i + tag, gw, gh, RenderTextureFormat.ARGBHalf);
                    }
                    eh.hasPrev = false;
                }
                _pRayO = _rayO.GetNativeTexturePtr();
                _pRayD = _rayD.GetNativeTexturePtr();
                _pRayV = _rayV.GetNativeTexturePtr();
                _pOut[0] = _hit.GetNativeTexturePtr();
                _pOut[1] = _bounce.GetNativeTexturePtr();
                _pOut[2] = _pick.GetNativeTexturePtr();
                _pOut[3] = _pick2.GetNativeTexturePtr();
                _pOut[4] = _cell.GetNativeTexturePtr();
                _w = w;
                _h = h;
                _step = step;
                _rays = rays;
                _hasPrev = false;
                _said = false;
            }

            Vector3 pos = _cam.transform.position;
            Matrix4x4 proj = _cam.projectionMatrix, w2v = _cam.worldToCameraMatrix;
            // (0.24.142) VR: the eye's own view and projection (an eye's frustum is off-centre), its position from its view
            if (stereo) EyeCamera(eye, out proj, out w2v, out pos);
            float len = GiConfig.RtLength.Value, cover = GiConfig.RtCover.Value;
            bool denoise = GiConfig.RtDenoise.Value && !GiTest.RtNoDenoise;
            // (0.24.92) the albedo view replaces the light rays with the camera's: what was kept from them is no history
            RtViewMode view = GiConfig.RtView.Value;
            bool albView = view == RtViewMode.Albedo || view == RtViewMode.AlbedoVsGame;
            if (albView != _wasAlbView) _hasPrev = false;
            _wasAlbView = albView;
            // The history is used when last frame ran the denoiser with this camera's buffers (else it starts over).
            bool reset = !_hasPrev || !_wasDenoised;
            int prev = _cur;
            _cur ^= 1;

            // This frame's parameters into a slot of their own: the render thread reads them when it gets to the
            // event, which can be a frame after this.
            int slot = stereo ? ((Time.frameCount & 1) << 1) | eyeIdx : Time.frameCount & 3;   // (0.24.145: VR, a slot per eye)
            Vector4 sun = Shader.GetGlobalVector(IdSunDir);
            _c[0] = sun.x; _c[1] = sun.y; _c[2] = sun.z; _c[3] = sun.w;
            _c[4] = gw; _c[5] = gh * rays; _c[6] = Time.frameCount & 0xffff; _c[7] = cover;
            _c[8] = pos.x; _c[9] = pos.y; _c[10] = pos.z; _c[11] = 0f;   // the ray starts are kept relative to this
            bool lampShadows = true, bounce = GiConfig.RtBounce.Value && !GiTest.RtNoBounce;
            int lamps = lampShadows && GiConfig.Lamps.Value ? GiLamps.CopyLamps(_lamps, pos) : 0;
            _c[11] = lamps;
            _c[12] = lampShadows ? 1f : 0f; _c[13] = bounce ? 1f : 0f; _c[14] = len;
            _c[15] = GiConfig.SunPassOwn.Value ? 1f : 0f;   // (0.24.125, a test: CSSun's BlockedSun)
            bool vis = !GiTest.RtNoVis && _passOpen >= 0;
            RenderTexture open = vis ? GiRt.OpenCells() : null;
            bool reuse = GiConfig.RtReuse.Value && !GiTest.RtNoReuse;
            // (0.24.82) half the pixels traced a frame (needs the reuse), shorter sun rays
            bool half = GiConfig.RtHalf.Value && reuse && !GiTest.RtFullRate && _hasPrev;
            _c[16] = vis ? 1f : 0f; _c[17] = reuse ? 1f : 0f; _c[18] = half ? 1f : 0f;
            _c[19] = GiTest.RtVisNoLimit ? 0f : Mathf.Clamp(GiConfig.RtCellVisRays.Value, 0, 20);   // (0.24.141) SeenCell's most check rays
            _c[20] = GiTest.RtSun1000 ? 1000f : GiConfig.RtSunLen.Value;
            // (0.24.86) "Ambient Occlusion": AOSix's (it shortens its reach on _GiRtLitFrame) or the ray-traced one
            RtAoMode aoMode = GiTest.RtAo >= 0 ? (RtAoMode)GiTest.RtAo : GiConfig.RtAo.Value;
            bool rtAo = !stereo && aoMode == RtAoMode.RayTraced && _passAoRays >= 0 && _passAoTemporal >= 0 && _passAoBlur >= 0 && EnsureAo(w, h);
            if (!rtAo && _aoO != null) FreeAo();
            float aoRadius = Mathf.Clamp(GiConfig.RtAoRadius.Value, 0.05f, 1.5f);
            _c[21] = rtAo ? 1f : 0f; _c[22] = aoRadius; _c[23] = Mathf.Clamp(GiConfig.RtAoRays.Value, 1, 8);
            // (0.24.89) materials: a light ray's cone (its bounce is soft: mostly the small atlas), a reflection ray's (about a
            // pixel), the tile size (0 = hits take the voxels' colour) and tiles a row
            bool tex = GiRt.AtlasTile > 0 && GiConfig.RtTextures.Value && !GiTest.RtNoTex;
            _c[24] = albView ? 0.0015f : 0.05f;   // (the albedo view's rays: as sharp as a reflection's)
            _c[25] = 0.0015f; _c[26] = tex ? GiRt.AtlasTile : 0f; _c[27] = GiRt.AtlasCols;
            // (0.24.89) the sun's shadow: traced with the light rays (so they must start before the game's lighting), into the
            // sun's shadow mask - only while the sun is up and the game gives it shadows
            // (0.24.95) decided in OnPreCull, where the sun's buffers went on the light; off it now, culling has taken them (and
            // no other camera's culling takes them)
            bool sunOn = _sunFrame && _sunO != null;
            bool lampOn = _lampFrame && _sunO != null && _lampO != null;   // (0.24.115) decided in OnPreCull with the sun's
            // (0.24.154) "Reuse Lamp Choices": the lamp choice by ReSTIR DI (this frame's rays pass, its kernel bit, the share's decode)
            bool lampRes = lampOn && LampRestirWanted() && EnsureLampRes(_lampO.width / 2, _lampO.height);
            _sunFrame = _lampFrame = false;
            DetachSun();
            if (!sunOn && !lampOn && _sunO != null) FreeSun();
            if (!lampOn && _lampO != null) FreeLamp();
            // (c[29]: what the D3D12 side's sun kernel runs - 1 the sun, 2 the lamps' shadow rays (0.24.115), 3 both)
            _c[28] = Mathf.Clamp(GiConfig.RtSunSoft.Value, 0f, 3f) * Mathf.Deg2Rad; _c[29] = (sunOn ? 1f : 0f) + (lampOn ? 2f : 0f)
                + (lampRes ? 16f : lampOn && GiAssets.LampTop3 ? 8f : 0f);   // (0.24.153) 8: the two lamps that light each point most + one of the rest, each with a shadow ray; (0.24.154) 16: the lamps RTLAMPRIS chose (ReSTIR DI)
            // (0.24.98) RayTracedWorld: what keeps the game's sun shadow, out of the sun's rays - within the game's shadow distance
            _c[30] = GiConfig.RtSunShadowMode.Value == RtSunMode.RayTracedWorld ? 1f : 0f; _c[31] = QualitySettings.shadowDistance;
            bool sunRays = sunOn || lampOn;
            GiRt.SetExtra(slot, _pAlb, sunRays ? _pSunO : IntPtr.Zero, sunRays ? _pSunRaw : IntPtr.Zero, lampOn ? _pLampO : IntPtr.Zero, IntPtr.Zero);
            Vector4 sunP = _sunP;
            GiRt.SetAo(slot, rtAo ? _pAoO : IntPtr.Zero, rtAo ? _pAoN : IntPtr.Zero, rtAo ? _pAoRaw : IntPtr.Zero);
            // (0.24.87) ReflectSix's off-screen reflection rays, when it made them for this frame or the last (its pass runs
            // at AfterReflections, before the trace is started): traced with these, their ends lit by RTREFLSHADE
            var reflO = Shader.GetGlobalTexture(IdSsrRayO) as RenderTexture;
            var reflD = Shader.GetGlobalTexture(IdSsrRayD) as RenderTexture;
            float reflAt = Shader.GetGlobalFloat(IdSsrRayFrame);
            bool refl = !stereo && !GiTest.RtNoRefl && _passReflShade >= 0 && reflAt > 0f && Time.frameCount - reflAt <= 1f
                        && Created(reflO) && Created(reflD) && reflO.width == reflD.width && reflO.height == reflD.height && EnsureRefl(reflO, reflD);
            if (!refl && _reflOut != null) FreeRefl();
            GiRt.SetRefl(slot, refl ? _pReflO : IntPtr.Zero, refl ? _pReflD : IntPtr.Zero, refl ? _pReflHit : IntPtr.Zero,
                refl ? _pReflPick : IntPtr.Zero, refl ? _pReflCell : IntPtr.Zero, refl ? _pReflAlb : IntPtr.Zero);
            if (refl) Shader.SetGlobalFloat(IdReflFrame, Time.frameCount);
            GiRt.SetFrame(slot, _pRayO, _pRayD, reuse ? _pRayV : IntPtr.Zero, open != null ? GiRt.OpenPtr : IntPtr.Zero, _pOut, _c, _lamps);

            bool apply = GiConfig.RtApply.Value;
            if (_cbTrace == null) _cbTrace = new CommandBuffer { name = "TraceSix RT" };
            if (_cbRays == null) _cbRays = new CommandBuffer { name = "TraceSix RT Rays" };
            CommandBuffer cb = _cbTrace;
            cb.Clear();
            _cbRays.Clear();
            // "Trace During Lighting": the rays (they need only depth and normals) made and the trace started before
            // the game lights the frame, so the GPU traces on its compute queue while it lights it; collected after
            // lighting, where the light is needed. Off: both after lighting, back to back, as before.
            bool early = !GiTest.RtLate;
            CommandBuffer rc = early ? _cbRays : cb;
            var grid = new Vector4(gw, gh, step, rays);
            var camPos = new Vector4(pos.x, pos.y, pos.z, 0f);
            var parms = new Vector4(len, Time.frameCount & 0xffff, reset ? 1f : 0f, GiConfig.RtView.Value == RtViewMode.Split ? 1f : 0f);
            // ("Reuse Ray Samples": the last frame's kept samples, its surface points and its camera are read by RTRAYS as
            // well as RTSHADE; w = 1: no last frame to take them from)
            var reuseV = new Vector4(reuse ? 1f : 0f, Mathf.Clamp(GiConfig.RtReuseHistory.Value, 1, 32), ReuseRadius, _hasPrev ? 0f : 1f);
            // (0.24.85) x half-rate; y "Steadier New Pixels"; z "Reuse Brightness Limit" (RTRAYS picks the same
            // neighbours as RTSHADE, so both buffers get it)
            // w (0.24.104) "Spread Ray Samples": the most frames a spread sample stands for - twice Reuse History (0 = off)
            // (0.24.105) "Variance-Guided Denoising" (NVIDIA ReLAX's structure): needs the bundle's two passes; with it, the
            // most W a kept sample may stand for is 32 (NVIDIA: an energy increase of no more than 20 - 30 x), else 160
            bool relax = denoise && GiConfig.RtRelax.Value && !GiTest.RtOldDenoise && _passClamp >= 0 && _passAtrous >= 0;
            var fast = new Vector4(half ? 1f : 0f, 1f, Mathf.Clamp(GiConfig.RtReuseLimit.Value, 1.5f, 10f),
                                   GiConfig.RtSpread.Value && !GiTest.RtNoSpread ? 2f * Mathf.Clamp(GiConfig.RtReuseHistory.Value, 1, 32) : 0f);
            // (0.24.109) "Keep Bounce Shadows" (the earlier denoiser's blur): its strength, and the guide's reach - the guide is
            // kept while the setting is on or ShadowGuide shows it
            float keep = denoise && !relax && !GiTest.RtNoShadowGuide ? Mathf.Clamp01(GiConfig.RtShadowKeep.Value) : 0f;
            var shadowG = new Vector4(keep, keep > 0f || view == RtViewMode.ShadowGuide ? Mathf.Min(1.5f, len) : 0f, 0f, 0f);
            // (0.24.110) at objects' edges: the most frames of a neighbour's history (8 = as before), y unused (the speck cut by
            // surface, removed in 0.24.113), the guide's tolerance from its grain
            var edge = new Vector4(!GiTest.RtEdge8 ? 1000f : 8f, 0f,
                                   !GiTest.RtGuideFrames ? 1f : 0f, 0f);
            foreach (CommandBuffer b in early ? new[] { _cbRays, cb } : new[] { cb })
            {
                b.SetGlobalVector(IdGrid, grid);
                b.SetGlobalVector(IdShadowG, shadowG);
                b.SetGlobalVector(IdEdge, edge);
                b.SetGlobalVector(IdCamPos, camPos);
                b.SetGlobalVector(IdParams, parms);
                b.SetGlobalVector(IdReuse, reuseV);
                b.SetGlobalVector(IdFast, fast);
                b.SetGlobalFloat(IdWMax, relax ? 32f : 160f);
                b.SetGlobalFloat(IdGuide, GiConfig.RtGuide.Value && !GiTest.RtUnguided ? 0.5f : 0f);   // (0.24.106: half the rays still cosine)
                b.SetGlobalFloat(IdDebug, view == RtViewMode.AlbedoVsGame ? 2f : albView ? 1f : 0f);
                b.SetGlobalVector(IdPrevUV, _prevUV);
                b.SetGlobalMatrix(IdPrevW2V, _prevW2V);
                b.SetGlobalTexture(IdGeoPPrev, _geoP[prev]);
                b.SetGlobalTexture(IdGeoNPrev, _geoN[prev]);
                b.SetGlobalTexture(IdResA, _resA[prev]);
                b.SetGlobalTexture(IdResB, _resB[prev]);
                b.SetGlobalTexture(IdResC, _resC[prev]);
                b.SetGlobalTexture(IdResD, _resD[prev]);
                b.SetGlobalTexture(IdResE, _resE[prev]);
                if (stereo) EyeGlobals(b, w, h, proj, w2v, pos);
            }
            // a traced pixel's size per metre of distance: the picture's height at 1 m / its height in traced pixels
            cb.SetGlobalVector(IdDen, new Vector4(Mathf.Clamp(GiConfig.RtHistory.Value, 4, 64), 2f / proj.m11 / gh, 0f, 1f));
            cb.SetGlobalVector(IdPrevUV, _prevUV);
            cb.SetGlobalMatrix(IdPrevW2V, _prevW2V);
            // the rays, and the trace started
            rc.SetRenderTarget(new RenderTargetIdentifier[] { _rayO, _rayD, _rayV }, _rayO);
            rc.DrawProcedural(Matrix4x4.identity, _mat, _passRays, MeshTopology.Triangles, 3);
            if (open != null)
            {
                rc.SetRenderTarget(open);   // which cells hold light: those the picture reads
                rc.DrawProcedural(Matrix4x4.identity, _mat, _passOpen, MeshTopology.Triangles, 3);
            }
            if (rtAo)
            {
                rc.SetRenderTarget(new RenderTargetIdentifier[] { _aoO, _aoN }, _aoO);   // the AO rays, one per picture pixel
                rc.DrawProcedural(Matrix4x4.identity, _mat, _passAoRays, MeshTopology.Triangles, 3);
            }
            if (sunOn)
            {
                rc.SetGlobalVector(IdSunP, sunP);
                rc.SetGlobalFloat(IdSunSkipAway, GiConfig.RtSunSkipAway.Value || GiTest.RtSunSkip ? 1f : 0f);   // (0.24.140)
                rc.SetRenderTarget(_sunO);                                                 // the sun's rays, one per 2 x 2
                rc.DrawProcedural(Matrix4x4.identity, _mat, _passSunRays, MeshTopology.Triangles, 3);
            }
            if (lampOn)
            {
                int lw = _lampO.width / 2, lh = _lampO.height;
                rc.SetGlobalVector(IdSunP, sunP);
                rc.SetRenderTarget(_lampO);                                                // (0.24.115) the lamps' shadow rays, the same pixel of each 2 x 2
                rc.SetViewport(new Rect(0f, 0f, lw, lh));                                 // (0.24.154: its left half)
                rc.DrawProcedural(Matrix4x4.identity, _mat, _passLampRays, MeshTopology.Triangles, 3);
                if (lampRes)
                {
                    // (0.24.154) ReSTIR DI: each traced pixel's reservoir (fresh + last frame's round where it was), then the three
                    // lamps it chose into the right half for the kernel
                    rc.SetGlobalFloat(IdLampTotal, lamps);
                    rc.SetGlobalFloatArray(IdLampRemap, GiLamps.Remap);
                    rc.SetGlobalVector(IdLampResP, new Vector4(_hadLampRes && _hasPrev ? 1f : 0f, _lampResP.x, _lampResP.y, 0f));
                    rc.SetGlobalVector(IdSunGrid, new Vector4(lw, lh, 0f, 0f));
                    rc.SetGlobalTexture(IdLampResHist, _lampResHist[_lampResIdx]);
                    rc.SetRenderTarget(new RenderTargetIdentifier[] { _lampResCur, _lampInfo }, _lampResCur);
                    rc.DrawProcedural(Matrix4x4.identity, _mat, _passLampRis, MeshTopology.Triangles, 3);
                    rc.SetGlobalTexture(IdLampResCur, _lampResCur);
                    rc.SetGlobalTexture(IdLampInfo, _lampInfo);
                    rc.SetRenderTarget(_lampO);
                    rc.SetViewport(new Rect(lw, 0f, lw, lh));
                    rc.DrawProcedural(Matrix4x4.identity, _mat, _passLampSel, MeshTopology.Triangles, 3);
                }
            }
            rc.SetRenderTarget(_raw);   // (the ray textures unbound before the native side copies them)
            if (!GiTest.RtNoTrace) rc.IssuePluginEvent(GiRt.Func, 16 + slot);
            // (0.24.156) the lamps' shadow share made now, before the game lights the frame: the sun's and the lamps' rays (one
            // list, signalled before the light rays') collected here - D3D11 waits for them - so every lamp the game draws gets
            // THIS frame's. To 0.24.155 it was made after the lighting for the next frame, read where each point was then, and
            // what was not on screen then got no shadow: a strip along the edge on every turn ("the shadow doesn't keep up with
            // my camera"). Made at the sun's mask first (0.24.156 test): no change in game - Unity draws the lamps before the
            // sun's mask (INFERRED from that). The sun's own collect at its mask then finds its rays already in.
            _lampEarly = false;
            if (lampOn && early && !GiTest.RtNoTrace)
            {
                rc.IssuePluginEvent(GiRt.Func, 24 + slot);
                LampShare(rc, w, h, lampRes, sunP, w2v, proj);
                _lampEarly = true;
            }
            if (early) rc.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);   // (the camera's own target again for the game's lighting, as AOSix leaves it)
            // ... and collected: D3D11 waits for it here
            if (!GiTest.RtNoTrace) cb.IssuePluginEvent(GiRt.Func, 20 + slot);
            // (0.24.115) "Ray-Traced Direct Lamp Shadows": the lamps' picks into the share of the lamps' light that gets through, per
            // pixel (LampShare) - made before the lighting with the rays (above, 0.24.156), else (SixBench's "traced after
            // lighting") here, after the lighting, for the next frame's light pass
            if (lampOn)
            {
                if (!_lampEarly) LampShare(cb, w, h, lampRes, sunP, w2v, proj);
                ApplyLampState();
                if (!_saidLamp)
                {
                    _saidLamp = true;
                    Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: direct lamp shadows ray-traced (" + (_lampO.width / 2) + "x" + _lampO.height + (lampRes ? ", lamps chosen by ReSTIR DI" : "")
                        + " rays: the two lamps that light each point most traced exactly, one ray for the rest), laid into the lamps' light by TraceSix's copy of the light pass - "
                        + (_lampEarly ? "this frame's (made before the game lights the frame)" : "the frame after") + ".");
                }
            }
            _lampEarly = false;
            _hadLamp = lampOn;
            // their ends lit; this frame's surface points and facings
            cb.SetGlobalTexture(IdRayO, _rayO);
            cb.SetGlobalTexture(IdRayD, _rayD);
            cb.SetGlobalTexture(IdHit, _hit);
            cb.SetGlobalTexture(IdBounce, _bounce);
            cb.SetGlobalTexture(IdPick, _pick);
            cb.SetGlobalTexture(IdPick2, _pick2);
            cb.SetGlobalTexture(IdCell, _cell);
            cb.SetGlobalTexture(IdAlb, _alb);
            cb.SetGlobalVector(IdOpt, new Vector4(lampShadows ? 1f : 0f, bounce ? 1f : 0f, len, 0f));
            bool instant = GiConfig.RtInstantSwitch.Value;
            cb.SetGlobalVectorArray(IdSwitch, GiLamps.SwitchesNow(instant));
            cb.SetGlobalVector(IdSwitchP, new Vector4(instant ? 1f : 0f, SwitchChange, SwitchKeep, SwitchM));
            cb.SetRenderTarget(new RenderTargetIdentifier[] { _raw, _geoP[_cur], _geoN[_cur], _resA[_cur], _resB[_cur], _resC[_cur], _resD[_cur], _resE[_cur] }, _raw);
            if (!GiTest.RtNoShade) cb.DrawProcedural(Matrix4x4.identity, _mat, _passShade, MeshTopology.Triangles, 3);
            cb.SetGlobalTexture(IdRaw, _raw);
            cb.SetGlobalTexture(IdGeoP, _geoP[_cur]);
            cb.SetGlobalTexture(IdGeoN, _geoN[_cur]);
            // (0.24.157) the light the picture takes, as the denoiser leaves it, and its variance (ReLAX's only)
            RenderTexture lightTex = _raw, stereoVar = null;
            bool stab = false;
            if (denoise)
            {
                // carried over from the frames before
                cb.SetGlobalTexture(IdGeoPPrev, _geoP[prev]);
                cb.SetGlobalTexture(IdGeoNPrev, _geoN[prev]);
                cb.SetGlobalTexture(IdHist, _accum[prev]);
                cb.SetGlobalTexture(IdFastHist, _fast[prev]);
                cb.SetGlobalTexture(IdMomHist, _mom[prev]);
                cb.SetGlobalVector(IdRelax, new Vector4(relax ? 1f : 0f, 6f, 2f, 2f));   // (ReLAX's defaults: 6 fast frames, 2 sigma, phi 2)
                cb.SetGlobalVector(IdVoxNew, new Vector4(GiConfig.RtVoxNew.Value, 0f, 0f, 0f));
                // (1.0.0) "Pre-Blur", NVIDIA ReBLUR's pre-pass (RTPREBLUR): this frame's light blurred along its surfaces before
                // the temporal pass takes it in - RTTEMPORAL then reads it as the frame's light, its 3 x 3 too
                if (GiConfig.RtPreBlur.Value && _passPre >= 0)
                {
                    cb.SetRenderTarget(_pre);
                    cb.DrawProcedural(Matrix4x4.identity, _mat, _passPre, MeshTopology.Triangles, 3);
                    cb.SetGlobalTexture(IdRaw, _pre);
                }
                if (relax)
                {
                    // (0.24.105) ReLAX: the temporal pass (long + fast history, moments), the long one held to the fast one's colour
                    // box (+ history fix, variance) - which is what next frame takes over - then the a-trous filter, 5 runs
                    cb.SetRenderTarget(new RenderTargetIdentifier[] { _tmpLight, _hitD, _fast[_cur], _mom[_cur] }, _tmpLight);
                    cb.DrawProcedural(Matrix4x4.identity, _mat, _passTemporal, MeshTopology.Triangles, 3);
                    cb.SetGlobalTexture(IdHitD, _hitD);
                    cb.SetGlobalTexture(IdSrc, _tmpLight);
                    cb.SetGlobalTexture(IdFastCur, _fast[_cur]);
                    cb.SetGlobalTexture(IdMomCur, _mom[_cur]);
                    cb.SetRenderTarget(new RenderTargetIdentifier[] { _accum[_cur], _var[0] }, _accum[_cur]);
                    cb.DrawProcedural(Matrix4x4.identity, _mat, _passClamp, MeshTopology.Triangles, 3);
                    RenderTexture inL = _accum[_cur], inV = _var[0];
                    for (int it = 0; it < 5; it++)
                    {
                        RenderTexture outL = (it & 1) == 0 ? _blur : _final, outV = (it & 1) == 0 ? _var[1] : _var[0];
                        cb.SetGlobalTexture(IdSrc, inL);
                        cb.SetGlobalTexture(IdVar, inV);
                        cb.SetGlobalVector(IdAtrous, new Vector4(1 << it, it, 0f, 0f));
                        cb.SetRenderTarget(new RenderTargetIdentifier[] { outL, outV }, outL);
                        cb.DrawProcedural(Matrix4x4.identity, _mat, _passAtrous, MeshTopology.Triangles, 3);
                        inL = outL;
                        inV = outV;
                    }
                    cb.SetGlobalTexture(IdLight, inL);
                    lightTex = inL;
                    stereoVar = inV;
                }
                else
                {
                    cb.SetRenderTarget(new RenderTargetIdentifier[] { _accum[_cur], _hitD, _fast[_cur], _mom[_cur] }, _accum[_cur]);
                    cb.DrawProcedural(Matrix4x4.identity, _mat, _passTemporal, MeshTopology.Triangles, 3);
                    cb.SetGlobalTexture(IdHitD, _hitD);
                    cb.SetGlobalTexture(IdMomCur, _mom[_cur]);   // (0.24.109) the bounce-shadow guide (z), for RTBLUR and ShadowGuide
                    cb.SetGlobalTexture(IdFastCur, _fast[_cur]);   // (1.0.0) its a = the real frames, RTBLUR's history fix
                    // blurred along its surface, twice
                    cb.SetGlobalTexture(IdSrc, _accum[_cur]);
                    cb.SetGlobalVector(IdBlur, new Vector4(1f, 0f, 0f, 0f));
                    cb.SetRenderTarget(_blur);
                    cb.DrawProcedural(Matrix4x4.identity, _mat, _passBlur, MeshTopology.Triangles, 3);
                    cb.SetGlobalTexture(IdSrc, _blur);
                    cb.SetGlobalVector(IdBlur, new Vector4(0.5f, 1f, 0f, 0f));
                    cb.SetRenderTarget(_final);
                    cb.DrawProcedural(Matrix4x4.identity, _mat, _passBlur, MeshTopology.Triangles, 3);
                    cb.SetGlobalTexture(IdLight, _final);
                    lightTex = _final;
                }
                // (1.0.0) "Stabilize Output", ReBLUR's temporal stabilization (RTSTABILIZE): the denoised light averaged over the
                // frames before in a history of its own, held to within the grain of this frame's - for the picture only
                stab = GiConfig.RtStabilize.Value && _passStab >= 0;
                if (stab)
                {
                    cb.SetGlobalTexture(IdSrc, lightTex);
                    cb.SetGlobalTexture(IdStabRef, _accum[_cur]);
                    cb.SetGlobalTexture(IdStabHist, _stab[prev]);
                    cb.SetGlobalVector(IdStab, new Vector4(StabFrames, StabBox, !reset && _wasStab ? 1f : 0f, StabFloor));
                    cb.SetRenderTarget(_stab[_cur]);
                    cb.DrawProcedural(Matrix4x4.identity, _mat, _passStab, MeshTopology.Triangles, 3);
                    cb.SetGlobalTexture(IdLight, _stab[_cur]);
                    lightTex = _stab[_cur];
                }
            }
            else cb.SetGlobalTexture(IdLight, _raw);
            // (0.24.157) "Stereo Blend In VR": Philippi, Frisvad & Jensen 2023 (EGSR) - this eye's denoised light blended with the
            // other eye's, reprojected, by their Eq. 4 (RTSTEREO), after the denoiser and for display only (the history stays
            // this eye's own). The left eye is drawn first: it takes the right eye's light of the frame before, the right eye
            // the left's of this frame; what changed in between counts as a difference, which the weight turns down by itself.
            if (stereo)
            {
                EyeHist me = _eyes[_eye], other = _eyes[_eye ^ 1];
                bool sb = GiConfig.RtStereo.Value && !GiTest.RtNoStereo && _passStereo >= 0 && EnsureStereo(me, gw, gh);
                if (sb)
                {
                    cb.CopyTexture(lightTex, me.light);
                    if (Created(other.light) && other.lightGeoP != null && other.lightGeoN != null && other.lightFrame >= Time.frameCount - 1)
                    {
                        cb.SetGlobalTexture(IdStereoSrc, lightTex);
                        cb.SetGlobalTexture(IdStereoVar, stereoVar != null ? stereoVar : _var[0]);
                        cb.SetGlobalVector(IdStereo, new Vector4(stereoVar != null ? 1f : 0f, 0.5f, 0f, 0f));
                        cb.SetGlobalTexture(IdOtherLight, other.light);
                        cb.SetGlobalTexture(IdOtherGeoP, other.lightGeoP);
                        cb.SetGlobalTexture(IdOtherGeoN, other.lightGeoN);
                        cb.SetGlobalMatrix(IdOtherW2V, other.lightW2V);
                        cb.SetGlobalVector(IdOtherUV, other.lightUV);
                        cb.SetRenderTarget(_stereoOut);
                        cb.DrawProcedural(Matrix4x4.identity, _mat, _passStereo, MeshTopology.Triangles, 3);
                        cb.SetGlobalTexture(IdLight, _stereoOut);
                    }
                    me.lightGeoP = _geoP[_cur];
                    me.lightGeoN = _geoN[_cur];
                    me.lightW2V = w2v;
                    me.lightUV = UVToView(proj);
                    me.lightFrame = Time.frameCount;
                }
                else me.lightFrame = -10;
            }
            // the AO laid on it: x = none / AOSix's / ray-traced; the ray-traced one kept over frames and blurred
            // (VR: no AOSix there - none)
            cb.SetGlobalVector(IdAo, new Vector4(rtAo ? 2f : aoMode == RtAoMode.ScreenSpace && !stereo ? 1f : 0f, aoRadius,
                rtAo && (!_hasPrev || !_hadAo) ? 1f : 0f, GiConfig.RtView.Value == RtViewMode.AmbientOcclusion ? 1f : 0f));
            if (rtAo)
            {
                cb.SetGlobalTexture(IdAoRaw, _aoRaw);
                cb.SetGlobalTexture(IdAoHist, _aoHist[prev]);
                cb.SetRenderTarget(_aoHist[_cur]);
                cb.DrawProcedural(Matrix4x4.identity, _mat, _passAoTemporal, MeshTopology.Triangles, 3);
                cb.SetGlobalTexture(IdAoHist, _aoHist[_cur]);
                cb.SetRenderTarget(_aoFinal);
                cb.DrawProcedural(Matrix4x4.identity, _mat, _passAoBlur, MeshTopology.Triangles, 3);
                cb.SetGlobalTexture(IdAoTex, _aoFinal);
            }
            _hadAo = rtAo;
            // (the sun's two buffers: made in OnPreCull)
            _hadSun = sunOn;
            // what ReflectSix's off-screen reflection rays show, for its world pass to read (_GiRtRefl)
            if (refl)
            {
                cb.SetGlobalTexture(IdReflO, reflO);
                cb.SetGlobalTexture(IdReflD, reflD);
                cb.SetGlobalTexture(IdReflHit, _reflHit);
                cb.SetGlobalTexture(IdReflPick, _reflPick);
                cb.SetGlobalTexture(IdReflCell, _reflCell);
                cb.SetGlobalTexture(IdReflAlb, _reflAlb);
                cb.SetRenderTarget(_reflOut);
                cb.DrawProcedural(Matrix4x4.identity, _mat, _passReflShade, MeshTopology.Triangles, 3);
                cb.SetGlobalTexture(IdRefl, _reflOut);
            }
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            // into the picture, in place of the voxel light's last pass (GiCamera leaves it out: GiRt.Applying)
            if (apply && !GiTest.RtNoApply) cb.DrawProcedural(Matrix4x4.identity, _mat, _passApply, MeshTopology.Triangles, 3);
            // (0.24.142) VR: GiCamera, which takes the game's own ambient out of the picture in flat screen, does not run there -
            // so here, for this eye's render (back in OnPostRender)
            if (stereo && apply && !GiTest.RtNoApply)
            {
                GameAmbient.Zero();
                _ambientZeroed = true;
            }
            GiRt.Applying = apply;
            // For AOSix: these rays light the picture this frame - its AO then reaches only Radius Under Ray-Traced GI
            if (apply) Shader.SetGlobalFloat(IdRtLitFrame, Time.frameCount);

            // the view, where this mod's other debug views go
            if (_cbLate == null) _cbLate = new CommandBuffer { name = "TraceSix RT View" };
            CommandBuffer late = _cbLate;
            late.Clear();
            late.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            if (stereo) EyeGlobals(late, w, h, proj, w2v, pos);
            // (0.24.107) the diagnostics: Sources / Guided
            late.SetGlobalFloat(IdViewDbg, GiConfig.RtView.Value == RtViewMode.Sources ? 1f : GiConfig.RtView.Value == RtViewMode.Guided ? 2f
                : GiConfig.RtView.Value == RtViewMode.ShadowGuide ? 3f : GiConfig.RtView.Value == RtViewMode.History ? 4f : 0f);
            if (GiConfig.RtView.Value != RtViewMode.Off) late.DrawProcedural(Matrix4x4.identity, _mat, _passView, MeshTopology.Triangles, 3);
            if (!_traceOn)
            {
                _cam.AddCommandBuffer(RaysEvt, _cbRays);
                _cam.AddCommandBuffer(TraceEvt, _cbTrace);
                _cam.AddCommandBuffer(LateEvt, _cbLate);
                _traceOn = true;
                if (!_said)
                    Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: tracing from '" + _cam.name + "' (" + w + "x" + h + ", one pixel in " + step + " each way: " + gw + "x" + gh
                        + " traced, " + rays + " ray" + (rays == 1 ? "" : "s") + " each; denoiser " + (denoise ? "on" : "off") + "; "
                        + (apply ? "lighting the picture" : "not lighting the picture (the voxel light does)") + "; traced "
                        + (early ? "while the game lights the frame" : "after the game's lighting") + ").");
                _said = true;
            }
            _prevW2V = w2v;
            _prevUV = UVToView(proj);
            _hasPrev = true;
            _wasDenoised = denoise;
            _wasStab = stab;
            if (stereo && !_saidVr)
            {
                _saidVr = true;
                Plugin.MyLog.LogInfo("[TraceSix] Ray tracing in VR: each eye of '" + _cam.name + "' traced on its own (" + w + "x" + h + " an eye, " + gw + "x" + gh
                    + " traced). The light only: sun / lamp shadows, ray-traced AO and ReflectSix's ray-traced reflections stay flat screen.");
            }
            KeepEye();
        }

        // (0.24.142) VR: an eye's own camera - its view and projection from the stereo getters, its position from its view
        private void EyeCamera(Camera.StereoscopicEye eye, out Matrix4x4 proj, out Matrix4x4 w2v, out Vector3 pos)
        {
            proj = _cam.GetStereoProjectionMatrix(eye);
            w2v = _cam.GetStereoViewMatrix(eye);
            pos = w2v.inverse.MultiplyPoint3x4(Vector3.zero);
        }

        // (0.24.142) VR: what the passes read of a camera - GiCamera sets it in flat screen (Shader globals from the mono camera);
        // in VR it does not run, so each eye's own values go into its buffers (as GiRtScope's)
        private static readonly int IdScreen = Shader.PropertyToID("_GiScreen"), IdUVToView = Shader.PropertyToID("_GiUVToView"),
            IdViewToWorld = Shader.PropertyToID("_GiViewToWorld"), IdGiCamPos = Shader.PropertyToID("_GiCamPos"),
            IdFacingFit = Shader.PropertyToID("_GiFacingFit"), IdApply = Shader.PropertyToID("_GiApply"), IdSight = Shader.PropertyToID("_GiSight"),
            IdWayOut = Shader.PropertyToID("_GiWayOut");

        private static void EyeGlobals(CommandBuffer b, int w, int h, Matrix4x4 proj, Matrix4x4 w2v, Vector3 pos)
        {
            b.SetGlobalVector(IdScreen, new Vector4(1f / w, 1f / h, 0f, 0f));
            b.SetGlobalVector(IdUVToView, UVToView(proj));
            b.SetGlobalMatrix(IdViewToWorld, w2v.inverse);
            b.SetGlobalVector(IdGiCamPos, new Vector4(pos.x, pos.y, pos.z, 0f));
            b.SetGlobalFloat(IdFacingFit, !GiTest.NearerFacing ? 1f : 0f);
            b.SetGlobalVector(IdApply, new Vector4(1f, GiConfig.Strength.Value, 0f, 0f));
            b.SetGlobalFloat(IdSight, 1f);
            b.SetGlobalFloat(IdWayOut, 1f);
        }
    }
}
