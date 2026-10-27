using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // (0.24.114) "Own Light Pass": TraceSix's copy of the game's deferred light pass (TraceSixLight.shader, decoded from the game's
    // Hidden/Internal-DeferredShadingEFT - the same math in all its variants) swapped in as Unity's deferred shading shader, the
    // step before ray-traced lamp visibility is multiplied into the lamps' direct light. On its own it must change nothing.
    // Also: the lamp census (what the game draws and how many shadow maps that takes) and the detail run's lamp-shadow switch.
    internal static class GiLightPass
    {
        private static readonly int IdMark = Shader.PropertyToID("_GiLightPassMark"), IdVisOn = Shader.PropertyToID("_GiLampVisOn");
        private static bool _visWarned;
        private static Shader _game;
        private static BuiltinShaderMode _gameMode;
        private static bool _gameRead, _on, _warned;
        private static readonly List<KeyValuePair<Light, LightShadows>> _shadowsTaken = new List<KeyValuePair<Light, LightShadows>>();

        public static bool Active => _on;

        // GiRaid.Update, every frame (menus too: the swap is the game's for every camera)
        public static void Tick()
        {
            // ("Ray-Traced Direct Lamp Shadows" needs the copy: it is where the share is laid into the lamps' light)
            bool own = GiTest.LightPass >= 0 ? GiTest.LightPass == 1 : GiConfig.RtDirectLamps.Value;
            bool want = own && GiConfig.Enabled.Value && GiAssets.Load() && GiAssets.Light != null;
            if (own && GiConfig.Enabled.Value && GiAssets.Light == null && !_warned)
            {
                _warned = true;
                Plugin.MyLog.LogWarning("[TraceSix] Ray-Traced Direct Lamp Shadows: the tracesix bundle has no copy of the light pass (it is older than this DLL). Rebuild the bundle; the game's own is used.");
            }
            if (want != _on) Swap(want);
            Shader.SetGlobalFloat(IdMark, _on && GiConfig.LightPassMark.Value ? 1f : 0f);
            // (0.24.115) the share made last frame (GiRtCamera), laid into the lamps' light this frame
            Shader.SetGlobalFloat(IdVisOn, _on && GiAssets.LightVis && GiRt.LampVisFrame >= Time.frameCount - 1 ? 1f : 0f);
            if (_on && GiConfig.RtDirectLamps.Value && GiAssets.Light != null && !GiAssets.LightVis && !_visWarned)
            {
                _visWarned = true;
                Plugin.MyLog.LogWarning("[TraceSix] Ray-Traced Direct Lamp Shadows: the tracesix bundle's copy of the light pass is older than this DLL (no V2). Rebuild the bundle.");
            }
            LampShadowsTest(GiTest.NoLampShadows);
        }

        private static void Swap(bool own)
        {
            if (!_gameRead)
            {
                _gameRead = true;
                _game = GraphicsSettings.GetCustomShader(BuiltinShaderType.DeferredShading);
                _gameMode = GraphicsSettings.GetShaderMode(BuiltinShaderType.DeferredShading);
                Plugin.MyLog.LogInfo("[TraceSix] The game's deferred light pass: '" + (_game != null ? _game.name : "none") + "' (" + _gameMode + ").");
            }
            if (own)
            {
                GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredShading, BuiltinShaderMode.UseCustom);
                GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredShading, GiAssets.Light);
            }
            else
            {
                GraphicsSettings.SetCustomShader(BuiltinShaderType.DeferredShading, _game);
                GraphicsSettings.SetShaderMode(BuiltinShaderType.DeferredShading, _gameMode);
            }
            _on = own;
            Plugin.MyLog.LogInfo("[TraceSix] Light pass: " + (own ? "the lamps and the sun are drawn by TraceSix's copy of the game's light pass (Ray-Traced Direct Lamp Shadows)." : "the game's own draws them."));
        }

        // The detail run's "lamps: no shadow maps": every point and spot light's shadow map off while it is measured (what the
        // game spends on lamp shadows - the most ray-traced lamp visibility could save), put back right after.
        private static void LampShadowsTest(bool off)
        {
            if (off == (_shadowsTaken.Count > 0)) return;
            if (off)
            {
                foreach (Light l in Object.FindObjectsOfType<Light>())
                {
                    if (l == null || l.type == LightType.Directional || l.shadows == LightShadows.None) continue;
                    _shadowsTaken.Add(new KeyValuePair<Light, LightShadows>(l, l.shadows));
                    l.shadows = LightShadows.None;
                }
                if (_shadowsTaken.Count == 0) _shadowsTaken.Add(new KeyValuePair<Light, LightShadows>(null, LightShadows.None));   // none to take: marks it done
            }
            else
            {
                foreach (var kv in _shadowsTaken) if (kv.Key != null) kv.Key.shadows = kv.Value;
                _shadowsTaken.Clear();
            }
        }

        // "Log Lamp Census": the lamps of the map, and the ones the game draws for this camera right now - lit (its fade by
        // distance leaves the far ones at 0) and touching the view - by kind, with how many render a shadow map (a point
        // light's is the scene drawn six times, a spot's once) and how many have a cookie.
        public static void Census(Camera cam)
        {
            if (cam == null) return;
            Plane[] frustum = GeometryUtility.CalculateFrustumPlanes(cam);
            Vector3 eye = cam.transform.position;
            int[] all = new int[2], allShadow = new int[2], allCookie = new int[2];
            int[] drawn = new int[2], drawnShadow = new int[2], drawnSoft = new int[2], drawnCookie = new int[2], drawnInside = new int[2];
            int ir = 0, directional = 0;
            var near = new List<KeyValuePair<float, string>>();
            foreach (Light l in Object.FindObjectsOfType<Light>())
            {
                if (l == null) continue;
                if (l.type == LightType.Directional) { if (l.isActiveAndEnabled && l.intensity > 0f) directional++; continue; }
                if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                int k = l.type == LightType.Spot ? 1 : 0;
                all[k]++;
                if (l.shadows != LightShadows.None) allShadow[k]++;
                if (l.cookie != null) allCookie[k]++;
                if (!l.isActiveAndEnabled || l.intensity <= 0.01f) continue;
                Color c = l.color;
                if (c.r > 0f && c.g <= 0f && c.b <= 0f) { ir++; continue; }   // the game's infrared lights (night vision only)
                var b = new Bounds(l.transform.position, Vector3.one * (2f * l.range));
                if (!GeometryUtility.TestPlanesAABB(frustum, b)) continue;
                drawn[k]++;
                float d = Vector3.Distance(eye, l.transform.position);
                if (d < l.range) drawnInside[k]++;
                if (l.cookie != null) drawnCookie[k]++;
                if (l.shadows != LightShadows.None)
                {
                    drawnShadow[k]++;
                    if (l.shadows == LightShadows.Soft) drawnSoft[k]++;
                    near.Add(new KeyValuePair<float, string>(d, "'" + l.name + "' (" + l.type + ", " + d.ToString("F1") + " m, range " + l.range.ToString("F1")
                        + ", " + l.shadows + ", resolution " + (l.shadowCustomResolution > 0 ? l.shadowCustomResolution.ToString() : l.shadowResolution.ToString()) + ")"));
                }
            }
            near.Sort((a, b2) => a.Key.CompareTo(b2.Key));
            var sb = new System.Text.StringBuilder();
            sb.Append("[TraceSix] Lamp census: in the map ").Append(all[0]).Append(" point lights (").Append(allShadow[0]).Append(" with shadows, ").Append(allCookie[0])
              .Append(" with a cookie) and ").Append(all[1]).Append(" spot lights (").Append(allShadow[1]).Append(" with shadows, ").Append(allCookie[1]).Append(" with a cookie). ")
              .Append("Drawn for this view now (lit and touching the view): ").Append(drawn[0]).Append(" point (").Append(drawnShadow[0]).Append(" with a shadow map, ")
              .Append(drawnSoft[0]).Append(" soft; ").Append(drawnCookie[0]).Append(" with a cookie; ").Append(drawnInside[0]).Append(" with you inside their range) and ")
              .Append(drawn[1]).Append(" spot (").Append(drawnShadow[1]).Append(" with a shadow map, ").Append(drawnSoft[1]).Append(" soft; ").Append(drawnCookie[1])
              .Append(" with a cookie; ").Append(drawnInside[1]).Append(" with you inside their range) - shadow-map renders of the scene this frame: about ")
              .Append(drawnShadow[0] * 6 + drawnShadow[1]).Append(" (a point light's six faces, a spot's one). ").Append(directional).Append(" sun / moon light(s); ")
              .Append(ir).Append(" infrared lights left out (night vision only). TraceSix's copy of the light pass: ").Append(_on ? "in use" : "off").Append(".");
            Plugin.MyLog.LogInfo(sb.ToString());
            for (int i = 0; i < near.Count && i < 12; i++) Plugin.MyLog.LogInfo("[TraceSix]   shadowed lamp drawn now: " + near[i].Value);
        }
    }
}
