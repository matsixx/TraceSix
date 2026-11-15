using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // The game's interior mask (AmbientLight's _StencilShadow: each interior volume marks the pixels whose depth lies inside
    // it) cuts straight across the gun and the hands in a doorway - the volume's side passes half a metre from the eyes,
    // where nothing of the world is. Read by the game's own shaders on the gun, so it showed in vanilla too. Appended to the
    // mask's set-up (GiNearInteriorPatch): the near pixels get the near plane in the mask's depth (GISixProbes NEARMASK),
    // and the game's own test then answers for where the camera is - the gun goes in and out of a room as a whole.
    internal static class GiNearInterior
    {
        public static bool enabled = true;
        // the gun and the hands; a wall nearer than this takes the camera's inside / outside too
        public static float nearMetres = 1.0f;

        private static readonly int IdMetres = Shader.PropertyToID("_GiNearMaskMetres");
        private static Material _mat;
        private static int _pass = -2;

        internal static void Append(CommandBuffer cb)
        {
            if (!enabled || cb == null || !GiConfig.Enabled.Value || GiAssets.Probes == null) return;
            if (_mat == null || _mat.shader != GiAssets.Probes)
            {
                _mat = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
                _pass = _mat.FindPass("NEARMASK");
                if (_pass < 0) Plugin.MyLog.LogWarning("[TraceSix] Interior mask: this TraceSix bundle has no NEARMASK pass (rebuild it) - the game's interior edge can still cut across the gun.");
            }
            if (_pass < 0) return;
            _mat.SetFloat(IdMetres, nearMetres);
            cb.DrawProcedural(Matrix4x4.identity, _mat, _pass, MeshTopology.Triangles, 3);
        }
    }
}
