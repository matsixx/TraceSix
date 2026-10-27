using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // (0.24.117) "See-Through Glass Windows": the map's glass that BSG turned into opaque walls - a material named glass on one
    // of the game's solid 'p0/Reflective/...' shaders (the glass-block walls, 'Factory_glass_tiles_*') - drawn with TraceSix's glass
    // (TraceSixGlass.shader; 0.24.122: the game's own look for them, lit as its light pass lights it, see-through and blurred by
    // "Glass Transparency" / "Glass Frost" - TraceSixProbes' GLASS pass) from the raid's
    // set-up, the game's shader and queue put back when it is switched off and when the raid ends. Originals kept by instance id,
    // not by Material (a static reference would keep a finished raid's map loaded). The same glass goes into the ray-traced scene
    // as see-through, tinted geometry (VoxelScene.Add -> GiRt.Place; "Sunlight Through Glass Is Tinted").
    internal static class GiGlass
    {
        private struct Orig { public Shader shader; public int queue; }
        private static readonly Dictionary<int, Orig> _orig = new Dictionary<int, Orig>();
        private static readonly int IdTint = Shader.PropertyToID("_GiGlassTint"), IdFrost = Shader.PropertyToID("_GiGlassFrost"),
            IdAlpha = Shader.PropertyToID("_GiGlassAlpha"), IdGi = Shader.PropertyToID("_GiGlassGi"), IdOneTap = Shader.PropertyToID("_GiGlassOneTap"),
            IdView = Shader.PropertyToID("_GiGlassView"), IdNoSsr = Shader.PropertyToID("_GiGlassNoSsr"),
            IdSkyMap = Shader.PropertyToID("_SsrSkyReflMap"), IdSkyOn = Shader.PropertyToID("_GiSkyReflOn"), IdSkyMaxLod = Shader.PropertyToID("_GiSkyReflMaxLod");
        private static bool _wasOn, _inRaid;
        // the setting, or SixBench's detail run measuring the game's own glass against it (GiTest.GlassOff)
        private static bool Wanted => GiConfig.GlassSeeThrough.Value && !GiTest.GlassOff;
        // (0.24.124) the panes drawn with TraceSix's glass - their renderer and the bounds of their part of its mesh - so that
        // GiGlassBlur makes its copy of the picture only while one is in a camera's view. Scene objects (and plain bounds):
        // dropped when the glass goes back or the raid ends.
        private struct Pane { public Renderer r; public int sub; public Bounds b; }
        private static readonly List<Pane> _panes = new List<Pane>();
        private static readonly Plane[] _planes = new Plane[6];
        internal static bool Active => _inRaid && _orig.Count > 0 && GiAssets.Glass != null;
        // (0.24.132) The panes in view, worked out ONCE per camera and frame (GiGlassBlur's OnPreCull and GiGlassSun's OnPreRender
        // asked separately) and with the frustum test in plain C#: it was GeometryUtility.TestPlanesAABB and Renderer.isVisible -
        // two calls into the engine - for every pane of the map, twice per camera per frame (621 panes: ~2,500 engine calls a frame
        // on a CPU-bound game). Now the planes are one engine call and isVisible is asked only of panes inside them.
        private static int _inViewCam, _inViewFrame = -1, _inView;

        private static void Cull(Camera cam)
        {
            int id = cam.GetInstanceID();
            if (_inViewFrame == Time.frameCount && _inViewCam == id) return;
            _inViewFrame = Time.frameCount;
            _inViewCam = id;
            _inView = 0;
            if (_panes.Count == 0) return;
            GeometryUtility.CalculateFrustumPlanes(cam, _planes);
            for (int i = 0; i < _panes.Count; i++)
            {
                Pane p = _panes[i];
                Vector3 c = p.b.center, e = p.b.extents;
                bool inside = true;
                for (int k = 0; k < 6 && inside; k++)
                {
                    Vector3 n = _planes[k].normal;
                    float d = n.x * c.x + n.y * c.y + n.z * c.z + _planes[k].distance;
                    float r = Mathf.Abs(n.x) * e.x + Mathf.Abs(n.y) * e.y + Mathf.Abs(n.z) * e.z;
                    inside = d + r >= 0f;
                }
                if (!inside || p.r == null || !p.r.isVisible) continue;
                _inView = 1;
                return;   // (one is enough: GiGlassSun draws all the glass at once, GiGlassBlur only needs to know)
            }
        }

        internal static bool PaneInView(Camera cam)
        {
            Cull(cam);
            return _inView > 0;
        }

        // (0.24.134) Every pane's triangles in ONE GPU buffer, in the world, three corners each - made once at the raid's set-up
        // (the panes are the map's: they do not move). GiGlassSun works out the glass's sun shadow with a single draw of it a frame
        // (GLASSSUN), the GPU throwing away what is out of view. 0.24.125 - 0.24.132 drew each pane in view again (a draw call per
        // pane per frame); 0.24.133 had the glass pass write its own rays through random-write targets, bound over all the
        // transparents - D3D11 then finishes each transparent draw before it starts the next, which cost more than it saved.
        internal static GraphicsBuffer Tris;
        internal static int TriVerts;

        private sealed class MeshRead { public Vector3[] v; public int[] idx; public string why; }

        // A mesh's positions, and its index buffer as the GPU holds it. The game's map meshes are not readable: read back from
        // the GPU (as GiProbes reads them for the rooms' boxes).
        private static MeshRead ReadMesh(Mesh mesh)
        {
            var mr = new MeshRead();
            if (mesh.isReadable) { mr.v = mesh.vertices; return mr; }   // (its indices: GetIndices, submesh by submesh)
            if (!mesh.HasVertexAttribute(VertexAttribute.Position)) { mr.why = "no positions"; return mr; }
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position), off = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            VertexAttributeFormat fmt = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
            bool half = fmt == VertexAttributeFormat.Float16;
            if ((fmt != VertexAttributeFormat.Float32 && !half) || mesh.GetVertexAttributeDimension(VertexAttribute.Position) < 3) { mr.why = "positions kept as " + fmt; return mr; }
            int nv = mesh.vertexCount, stride;
            byte[] raw;
            GraphicsBuffer vb = mesh.GetVertexBuffer(stream);
            if (vb == null) { mr.why = "no vertex buffer on the GPU"; return mr; }
            try
            {
                stride = vb.stride;
                raw = new byte[vb.count * stride];
                vb.GetData(raw);
            }
            finally { vb.Dispose(); }
            if (stride < off + (half ? 6 : 12) || raw.Length < nv * stride) { mr.why = "a vertex buffer smaller than its mesh says"; return mr; }
            var v = new Vector3[nv];
            for (int i = 0; i < nv; i++)
            {
                int at = i * stride + off;
                v[i] = half
                    ? new Vector3(Mathf.HalfToFloat(System.BitConverter.ToUInt16(raw, at)), Mathf.HalfToFloat(System.BitConverter.ToUInt16(raw, at + 2)), Mathf.HalfToFloat(System.BitConverter.ToUInt16(raw, at + 4)))
                    : new Vector3(System.BitConverter.ToSingle(raw, at), System.BitConverter.ToSingle(raw, at + 4), System.BitConverter.ToSingle(raw, at + 8));
            }
            GraphicsBuffer ib = mesh.GetIndexBuffer();
            if (ib == null) { mr.why = "no index buffer on the GPU"; return mr; }
            try
            {
                if (mesh.indexFormat == IndexFormat.UInt16)
                {
                    var small = new ushort[ib.count];
                    ib.GetData(small);
                    mr.idx = new int[small.Length];
                    for (int i = 0; i < small.Length; i++) mr.idx[i] = small[i];
                }
                else
                {
                    mr.idx = new int[ib.count];
                    ib.GetData(mr.idx);
                }
            }
            finally { ib.Dispose(); }
            mr.v = v;
            return mr;
        }

        private static void BuildTris()
        {
            ReleaseTris();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pts = new List<Vector3>();
            var reads = new Dictionary<Mesh, MeshRead>();
            var sub = new List<int>();
            int used = 0, unread = 0, outside = 0, batchedN = 0;
            string why = null;
            foreach (Pane p in _panes)
            {
                var r = p.r as MeshRenderer;
                MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) { unread++; continue; }
                // a static batch's mesh is the combined one, the renderer's part of it starting at subMeshStartIndex
                bool batched = r.isPartOfStaticBatch;
                int k = batched ? r.subMeshStartIndex + p.sub : p.sub;
                if (k < 0 || k >= mesh.subMeshCount) { unread++; why = why ?? "a submesh past its mesh's"; continue; }
                SubMeshDescriptor sd = mesh.GetSubMesh(k);
                if (sd.topology != MeshTopology.Triangles) { unread++; why = why ?? "not triangles"; continue; }
                if (!reads.TryGetValue(mesh, out MeshRead mr)) { mr = ReadMesh(mesh); reads[mesh] = mr; }
                if (mr.v == null) { unread++; why = why ?? mr.why; continue; }
                sub.Clear();
                if (mesh.isReadable) sub.AddRange(mesh.GetIndices(k, true));
                else
                {
                    int end = sd.indexStart + sd.indexCount;
                    if (mr.idx == null || end > mr.idx.Length) { unread++; why = why ?? "an index buffer smaller than its submeshes"; continue; }
                    for (int i = sd.indexStart; i < end; i++) sub.Add(mr.idx[i] + sd.baseVertex);
                }
                // into the world: a static batch's vertices are there already (the map's batches are made when it is built; one
                // combined at run time, relative to a root, fails the bounds check below), a pane of its own goes through its
                // transform - mirrored, its triangles turned back round (as Unity does when it draws it)
                Matrix4x4 m = batched ? Matrix4x4.identity : r.localToWorldMatrix;
                bool flip = !batched && m.determinant < 0f;
                Bounds b = p.b;
                b.Expand(0.2f);
                int start = pts.Count, nt = 0, miss = 0, nvx = mr.v.Length;
                for (int i = 0; i + 2 < sub.Count; i += 3)
                {
                    int a = sub[i], c = sub[i + 1], e = sub[i + 2];
                    nt++;
                    if ((uint)a >= (uint)nvx || (uint)c >= (uint)nvx || (uint)e >= (uint)nvx) { miss++; continue; }
                    Vector3 pa = m.MultiplyPoint3x4(mr.v[a]), pc = m.MultiplyPoint3x4(mr.v[c]), pe = m.MultiplyPoint3x4(mr.v[e]);
                    if (!b.Contains(pa) || !b.Contains(pc) || !b.Contains(pe)) miss++;
                    pts.Add(pa);
                    if (flip) { pts.Add(pe); pts.Add(pc); } else { pts.Add(pc); pts.Add(pe); }
                }
                // triangles that are not where the renderer is: the mesh read wrong - left out, not drawn somewhere else
                if (nt == 0 || miss * 10 > nt)
                {
                    pts.RemoveRange(start, pts.Count - start);
                    outside++;
                    continue;
                }
                used++;
                if (batched) batchedN++;
            }
            if (pts.Count > 0)
            {
                Tris = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pts.Count, 12);
                Tris.SetData(pts);
                TriVerts = pts.Count;
            }
            Plugin.MyLog.LogInfo("[TraceSix] See-Through Glass Windows: " + used + " of " + _panes.Count + " panes (" + batchedN + " static-batched) in one GPU buffer, "
                + pts.Count / 3 + " triangles (" + pts.Count * 12 / 1024 + " KB, " + reads.Count + " meshes read in " + sw.ElapsedMilliseconds + " ms)"
                + (unread > 0 ? "; " + unread + " not readable" + (why != null ? " (" + why + ")" : "") : "")
                + (outside > 0 ? "; " + outside + " left out: their triangles are not where their renderer is" : "") + ".");
        }

        private static void ReleaseTris()
        {
            Tris?.Release();
            Tris = null;
            TriVerts = 0;
        }

        // The world bounds of submesh j of a renderer (the whole renderer's when that is not to be had: static batches)
        private static Bounds PaneBounds(MeshRenderer r, int j)
        {
            MeshFilter mf = r.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || r.isPartOfStaticBatch || j >= mesh.subMeshCount) return r.bounds;
            Bounds lb = mesh.GetSubMesh(j).bounds;
            Matrix4x4 m = r.localToWorldMatrix;
            var wb = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
            for (int k = 0; k < 8; k++)
                wb.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((k & 1) != 0 ? 1f : -1f, (k & 2) != 0 ? 1f : -1f, (k & 4) != 0 ? 1f : -1f))));
            return wb;
        }

        // Glass the game draws as a solid wall: named glass, on one of its solid reflective shaders, with a colour texture -
        // or already drawn with ours.
        // (0.24.132) The glass-block walls only ('glass_tiles', 'Factory_glass_tiles_04': named glass_tile): the glass's look is
        // built for their texture (the mortar from its alpha). Lamp covers ('Old_Bunker_Light_Glass_Materials'), 'glass_dust' and
        // eyewear frames on the map were swapped too (by the name "glass"): more panes to cull and draw, and not windows.
        internal static bool IsGlass(Material m)
        {
            if (m == null || m.shader == null) return false;
            if (GiAssets.Glass != null && m.shader == GiAssets.Glass) return true;
            if (m.name.IndexOf("glass_tile", System.StringComparison.OrdinalIgnoreCase) < 0) return false;
            string sn = m.shader.name;
            if (!sn.StartsWith("p0/Reflective/") || sn.Contains("Transparent")) return false;
            if (m.renderQueue >= 2500) return false;
            return m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null;
        }

        // GiRaid.SetUpRaid (before the meshes are read)
        internal static void Apply()
        {
            _inRaid = true;
            _wasOn = Wanted;
            if (!_wasOn || !GiAssets.Load() || GiAssets.Glass == null) return;
            var mats = new List<Material>();
            var names = new Dictionary<string, int>();
            int swapped = 0;
            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                // (0.24.121) the map's own meshes only: gear and loot keep the game's look (0.24.117 also took eyewear
                // - 'Glasses_Duduma', 'glasses_6B34_static' - and a magazine's window)
                if (r == null || !VoxelScene.IsMapGeometry(r.gameObject)) continue;
                r.GetSharedMaterials(mats);
                for (int j = 0; j < mats.Count; j++)
                {
                    Material m = mats[j];
                    if (m == null || !IsGlass(m)) continue;
                    if (m.shader != GiAssets.Glass)
                    {
                        int id = m.GetInstanceID();
                        if (_orig.ContainsKey(id)) continue;
                        _orig[id] = new Orig { shader = m.shader, queue = m.renderQueue };
                        m.shader = GiAssets.Glass;
                        m.renderQueue = 3000;
                        swapped++;
                        names[m.name] = names.TryGetValue(m.name, out int k) ? k + 1 : 1;
                    }
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    int subs = mf != null && mf.sharedMesh != null ? mf.sharedMesh.subMeshCount : 1;
                    _panes.Add(new Pane { r = r, sub = Mathf.Min(j, Mathf.Max(0, subs - 1)), b = PaneBounds(r, j) });
                }
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in names) sb.Append(sb.Length > 0 ? ", " : "").Append(kv.Key);
            Plugin.MyLog.LogInfo("[TraceSix] See-Through Glass Windows: " + swapped + " glass materials the game draws as solid walls are drawn as glass"
                + (swapped > 0 ? " (" + sb + ")" : "") + ", " + _panes.Count + " panes.");
            BuildTris();
        }

        // GiRaid.EndRaid, or the setting switched off
        internal static void Restore(bool raidOver)
        {
            if (raidOver) _inRaid = false;
            _panes.Clear();
            ReleaseTris();
            _inView = 0;
            _inViewFrame = -1;
            if (_orig.Count == 0) return;
            int back = 0;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || !_orig.TryGetValue(m.GetInstanceID(), out Orig o)) continue;
                if (m.shader == GiAssets.Glass && o.shader != null) { m.shader = o.shader; m.renderQueue = o.queue; back++; }
            }
            _orig.Clear();
            Plugin.MyLog.LogInfo("[TraceSix] See-Through Glass Windows: " + back + " glass materials back on the game's own shader.");
        }

        // GiRaid.Update: the setting switched during a raid (the ray-traced scene keeps what it was built with until the next raid)
        internal static void Tick()
        {
            Shader.SetGlobalFloat(IdTint, Mathf.Clamp01(GiConfig.GlassTint.Value));
            Shader.SetGlobalFloat(IdFrost, Mathf.Clamp01(GiConfig.GlassFrost.Value));
            // (0.24.122) the glass's own look giving way to what is behind; TraceSix's light on it while its volumes are up
            Shader.SetGlobalFloat(IdAlpha, Mathf.Clamp01(GiConfig.GlassAlpha.Value));
            Shader.SetGlobalFloat(IdGi, GiConfig.On && GiProbes.Ready && !GiTest.GlassNoLight ? 1f : 0f);
            Shader.SetGlobalFloat(IdOneTap, GiTest.GlassOneTap ? 1f : 0f);   // (0.24.123, GiTest)
            Shader.SetGlobalFloat(IdNoSsr, GiTest.GlassNoSsr ? 1f : 0f);     // (0.24.135, GiTest)
            // (0.24.129; 0.24.138) CloudSix's sky map (published as a global while its sky runs): what the glass reflects where its
            // mirror direction is open to the sky
            Texture sky = Shader.GetGlobalTexture(IdSkyMap);
            Shader.SetGlobalFloat(IdSkyOn, sky != null ? 1f : 0f);
            Shader.SetGlobalFloat(IdSkyMaxLod, sky != null ? Mathf.Max(0, sky.mipmapCount - 1) : 0f);
            Shader.SetGlobalFloat(IdView, (float)GiConfig.GlassView.Value);   // (0.24.126, Debug)
            bool on = Wanted;
            if (on == _wasOn) return;
            _wasOn = on;
            if (!_inRaid) return;
            if (on) Apply(); else Restore(false);
        }
    }
}
