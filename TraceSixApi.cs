using TraceSix.Source;
using UnityEngine;

namespace TraceSix
{
    /// <summary>
    /// For other mods that render with a camera of their own and want TraceSix's indirect light in that picture
    /// (a light meter, a minimap, a capture). Reference TraceSix.dll and call this from a class that is only touched when
    /// the "com.matsix.tracesix" plugin is loaded (BepInDependency SoftDependency), so a missing TraceSix never fails to load.
    /// The camera gets the voxel light pass as a scope's does: no ray-traced light (the rays are traced for the player's
    /// camera only), no moving-light bounce, no debug views, no fade-in. It needs HDR and the deferred rendering path;
    /// otherwise the pass stays off (a warning names the camera, once).
    /// Not given: the ray-traced sun shadows (Sun Shadow Mode = RayTracedWorld removes the map from the game's own sun shadow,
    /// and only the player's camera and the scope get the rays' one).
    /// </summary>
    public static class TraceSixApi
    {
        /// <summary>Bumped when a member here changes shape. A property, not a const: a const would be baked into the caller at compile time.</summary>
        public static int Version => 1;

        /// <summary>Is TraceSix lighting the raid now (switched on, light volumes built)?</summary>
        public static bool IsActive => GiConfig.On && GiProbes.Ready;

        /// <summary>
        /// Puts the indirect-light pass on this camera. Safe to call again for the same camera. The pass itself decides
        /// frame by frame whether it can run (TraceSix on, light ready, camera HDR + deferred), so attaching early is fine.
        /// </summary>
        public static bool AttachTo(Camera cam)
        {
            if (cam == null) return false;
            GiCamera pass = cam.GetComponent<GiCamera>();
            if (pass == null) pass = cam.gameObject.AddComponent<GiCamera>();
            pass.Scope = true;       // the scope's pass: voxel light only, see GiCamera
            pass.External = true;    // and not tied to "Indirect Light In Scopes"
            return true;
        }

        /// <summary>Takes the pass off this camera again (its command buffers go with it). No-op when it has none.</summary>
        public static void Detach(Camera cam)
        {
            if (cam == null) return;
            GiCamera pass = cam.GetComponent<GiCamera>();
            if (pass != null && pass.External) Object.Destroy(pass);
        }

        /// <summary>Is the pass on this camera?</summary>
        public static bool IsAttached(Camera cam)
        {
            if (cam == null) return false;
            GiCamera pass = cam.GetComponent<GiCamera>();
            return pass != null && pass.External;
        }
    }
}
