using System.Collections.Generic;
using UnityEngine;

namespace TraceSix.Source
{
    // What the game's own culling has switched off FOR NOW, as against what is not part of the level.
    // The game culls by zone (DisablerCullingObject and what derives from it: the player is inside a trigger or
    // is not) and by distance (CullingObject). Either one holds a list of components it disables (renderers:
    // those never mattered here, the voxel world does not ask whether a renderer is on) and a list of whole
    // GameObjects it deactivates. The voxel world, the window markers and the lamps are collected ONCE, as the
    // raid loads, so what a zone the player is not standing in has deactivated at that moment must still be
    // taken: Present() tells switched off for now from not part of the level.
    internal static class GiCulling
    {
        private static readonly HashSet<int> _objects = new HashSet<int>();      // GameObjects a culling list deactivates
        private static readonly HashSet<int> _components = new HashSet<int>();   // components a culling list disables
        public static int Cullers;

        public static void Collect()
        {
            _objects.Clear();
            _components.Clear();
            Cullers = 0;
            try
            {
                foreach (CullingObject o in Resources.FindObjectsOfTypeAll<CullingObject>())     // derived types too
                {
                    if (o == null || !o.gameObject.scene.IsValid()) continue;
                    Cullers++;
                    Take(o._gameObjectsToTurnOff, o._componentsToTurnOff, null);
                }
                foreach (DisablerCullingObject o in Resources.FindObjectsOfTypeAll<DisablerCullingObject>())
                {
                    if (o == null || !o.gameObject.scene.IsValid()) continue;
                    Cullers++;
                    Take(o._gameObjectsToTurnOff, o._componentsToTurnOff, o._compsToTurnOffWhoIgnoreInversedColliders);
                }
            }
            catch (System.Exception e) { Plugin.MyLog.LogWarning("[TraceSix] Culling lists failed: " + e.GetType().Name + ": " + e.Message); }
            Plugin.MyLog.LogInfo("[TraceSix] Culling: " + Cullers + " of the game's culling zones and distance cullers found; between them they deactivate "
                + _objects.Count + " objects and disable " + _components.Count + " components when the player is elsewhere.");
        }

        private static void Take(List<GameObject> gos, List<Component> cs, List<Component> cs2)
        {
            if (gos != null) foreach (GameObject g in gos) if (g != null) _objects.Add(g.GetInstanceID());
            if (cs != null) foreach (Component c in cs) if (c != null) _components.Add(c.GetInstanceID());
            if (cs2 != null) foreach (Component c in cs2) if (c != null) _components.Add(c.GetInstanceID());
        }

        // Is this object part of the level as it stands: active, or inactive only because every inactive object
        // on the way up from it is one a culling list deactivates?
        public static bool Present(GameObject go)
        {
            if (go.activeInHierarchy) return true;
            for (Transform t = go.transform; t != null; t = t.parent)
                if (!t.gameObject.activeSelf && !_objects.Contains(t.gameObject.GetInstanceID())) return false;
            return true;
        }

        // Is this component one a culling list disables (so "disabled" does not mean "off")?
        public static bool Culls(Component c) => _components.Contains(c.GetInstanceID());
    }
}
