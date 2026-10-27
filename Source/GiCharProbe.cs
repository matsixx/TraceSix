using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // (0.24.103) "Log Character Meshes": the facts the characters-in-the-ray-tracing work stands on, measured in the game
    // before it is built. Your character's renderers (what is drawn, what only casts a shadow, skinned or rigid, whether
    // the gun is under the player), and for a few of its skinned meshes Unity's skinned vertex buffer
    // (SkinnedMeshRenderer.GetVertexBuffer): its size and layout, and which space its positions are in - its first
    // vertices read back and set against the same vertices baked on the CPU (BakeMesh: the renderer's own space). Then
    // whether the D3D12 side can share a buffer with D3D11 on this driver (textures do; RGBA32F ones did not).
    internal static class GiCharProbe
    {
        internal static IEnumerator Run()
        {
            var sb = new StringBuilder("[TraceSix] Character meshes:");
            Component player = FindPlayer(sb);
            if (player == null) { Plugin.MyLog.LogInfo(sb.ToString()); yield break; }
            Transform root = player.transform;
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
            int skinned = 0, rigid = 0, drawn = 0, shadowOnly = 0, off = 0;
            sb.Append("\n  player '").Append(root.name).Append("' at ").Append(root.position.ToString("F1")).Append(": ").Append(rs.Length).Append(" renderers");
            foreach (Renderer r in rs)
            {
                if (r is SkinnedMeshRenderer) skinned++; else if (r is MeshRenderer) rigid++;
                bool on = r.enabled && r.gameObject.activeInHierarchy;
                if (!on) off++;
                else if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) shadowOnly++;
                else drawn++;
            }
            sb.Append(" (").Append(skinned).Append(" skinned, ").Append(rigid).Append(" rigid; switched on: ").Append(drawn).Append(" drawn, ")
              .Append(shadowOnly).Append(" shadow-only; ").Append(off).Append(" switched off)");
            int lines = 0;
            foreach (Renderer r in rs)
            {
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                if (++lines > 90) { sb.Append("\n    ..."); break; }
                Mesh mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                Material m0 = r.sharedMaterial;
                sb.Append("\n    ").Append(r is SkinnedMeshRenderer ? "SKIN " : "RIGID").Append(" '").Append(PathUnder(r.transform, root)).Append("' layer ")
                  .Append(LayerMask.LayerToName(r.gameObject.layer)).Append(r.enabled && r.gameObject.activeInHierarchy ? ", on" : ", OFF")
                  .Append(", ").Append(r.shadowCastingMode).Append(r.isVisible ? ", visible" : "")
                  .Append(mesh != null ? ", mesh '" + mesh.name + "' " + mesh.vertexCount + " v / " + mesh.subMeshCount + " sub" : ", no mesh")
                  .Append(m0 != null ? ", '" + m0.shader.name + "'" : "");
                if (r is SkinnedMeshRenderer sk)
                    sb.Append(", root bone '").Append(sk.rootBone != null ? sk.rootBone.name : "none").Append("', ").Append(sk.bones != null ? sk.bones.Length : 0)
                      .Append(" bones, offscreen ").Append(sk.updateWhenOffscreen);
            }
            Plugin.MyLog.LogInfo(sb.ToString());

            // Unity's skinned vertex buffers, for up to 3 skinned meshes that are switched on (drawn ones first)
            var picks = new List<SkinnedMeshRenderer>();
            foreach (Renderer r in rs)
                if (r is SkinnedMeshRenderer s && s.enabled && s.gameObject.activeInHierarchy && s.sharedMesh != null && s.shadowCastingMode != ShadowCastingMode.ShadowsOnly && picks.Count < 2) picks.Add(s);
            foreach (Renderer r in rs)
                if (r is SkinnedMeshRenderer s && s.enabled && s.gameObject.activeInHierarchy && s.sharedMesh != null && s.shadowCastingMode == ShadowCastingMode.ShadowsOnly && picks.Count < 3) picks.Add(s);
            var was = new GraphicsBuffer.Target[picks.Count];
            for (int i = 0; i < picks.Count; i++) { was[i] = picks[i].vertexBufferTarget; picks[i].vertexBufferTarget |= GraphicsBuffer.Target.Raw; }
            for (int f = 0; f < 3; f++) yield return null;   // (skinned again with the new target)
            for (int i = 0; i < picks.Count; i++)
            {
                SkinnedMeshRenderer s = picks[i];
                var o = new StringBuilder("[TraceSix] Character meshes: skinned buffer of '" + PathUnder(s.transform, root) + "' (" + s.shadowCastingMode + "): ");
                GraphicsBuffer gb = null;
                try { gb = s.GetVertexBuffer(); }
                catch (Exception e) { o.Append("GetVertexBuffer threw ").Append(e.GetType().Name).Append(": ").Append(e.Message); }
                if (gb == null) { Plugin.MyLog.LogInfo(o.Append(gb == null ? " none (not skinned this frame?)" : "").ToString()); continue; }
                Mesh mesh = s.sharedMesh;
                o.Append(gb.count).Append(" x ").Append(gb.stride).Append(" bytes, target ").Append(gb.target).Append("; the mesh: ").Append(mesh.vertexCount)
                 .Append(" vertices, position ").Append(mesh.GetVertexAttributeFormat(VertexAttribute.Position)).Append(" in stream ").Append(mesh.GetVertexAttributeStream(VertexAttribute.Position))
                 .Append(" at ").Append(mesh.GetVertexAttributeOffset(VertexAttribute.Position)).Append(", normal ").Append(mesh.HasVertexAttribute(VertexAttribute.Normal))
                 .Append(", tangent ").Append(mesh.HasVertexAttribute(VertexAttribute.Tangent)).Append(", native buffer ").Append(gb.GetNativeBufferPtr() != IntPtr.Zero ? "yes" : "NO");
                // its first 4 vertices: the GPU's and the CPU's (renderer space; also the world and the root bone's space)
                var baked = new Mesh();
                s.BakeMesh(baked, true);
                Vector3[] bv = baked.vertices;
                UnityEngine.Object.Destroy(baked);
                AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(gb, Mathf.Min(gb.count, 4) * gb.stride, 0);
                while (!req.done) yield return null;
                if (req.hasError) o.Append("; read back: FAILED");
                else
                {
                    var data = req.GetData<float>();
                    int fpv = gb.stride / 4;
                    for (int v = 0; v < 4 && v < bv.Length && (v + 1) * fpv <= data.Length; v++)
                    {
                        var gpu = new Vector3(data[v * fpv], data[v * fpv + 1], data[v * fpv + 2]);
                        Vector3 world = s.transform.TransformPoint(bv[v]);
                        Vector3 inRoot = s.rootBone != null ? s.rootBone.InverseTransformPoint(world) : Vector3.zero;
                        o.Append("\n    v").Append(v).Append(" gpu ").Append(gpu.ToString("F3")).Append(" | baked (renderer space) ").Append(bv[v].ToString("F3"))
                         .Append(" d ").Append((gpu - bv[v]).magnitude.ToString("F3")).Append(" | world d ").Append((gpu - world).magnitude.ToString("F3"))
                         .Append(" | root-bone space d ").Append((gpu - inRoot).magnitude.ToString("F3"));
                    }
                }
                gb.Dispose();
                Plugin.MyLog.LogInfo(o.ToString());
            }
            for (int i = 0; i < picks.Count; i++) if (picks[i] != null) picks[i].vertexBufferTarget = was[i];
            // the D3D12 side: a buffer shared with D3D11
            GiRt.ProbeShareBuffer();
        }

        private static Component FindPlayer(StringBuilder sb)
        {
            foreach (EFT.Player p in UnityEngine.Object.FindObjectsOfType<EFT.Player>())
                if (p.IsYourPlayer) return p;
            sb.Append(" no player of yours in this scene.");
            return null;
        }

        private static string PathUnder(Transform t, Transform root)
        {
            string s = t.name;
            for (int i = 0; i < 6 && t.parent != null && t.parent != root; i++) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
