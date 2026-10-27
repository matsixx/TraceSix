using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // (0.24.125) The glass's sun shadow. Unity gives a transparent none, so the glass's sun term (TraceSixProbes GLASS) lit the
    // panes straight through the building. Each frame a pane is in view, before the transparents, all the glass is drawn into a
    // half-size target (GLASSSUN: one sample per 2 x 2, the nearest pane; 0.24.134: ONE draw of GiGlass.Tris, every pane's
    // triangles in the world, made at the raid's set-up) with how much sun reaches each sample through the voxel world (SunReach);
    // the glass reads it (_GiGlassSun*).
    // (0.24.137) No ray tracing for the glass, his call: its ray-traced sun rays (0.24.125) and reflections (0.24.128) needed
    // hand-overs to the D3D12 side - 0.70 ms of the glass's 1.04 in his SixBench run, most of it the hand-overs (the sun rays:
    // 0.013 ms of GPU work, 0.16 ms of frame time); riding the main trace's (0.24.136) was not tried.
    // (0.24.138) The same draw also marches each sample's mirror direction through the voxel world: how open it is to the sky (y),
    // for the glass's reflection of CloudSix's sky. So it runs by night too (the sun's part then: no shadow known, as before).
    internal class GiGlassSun : MonoBehaviour
    {
        private Camera _cam;
        private Material _mat;
        private int _pass = -2;
        private CommandBuffer _cb, _cbOff;
        private bool _on, _said;
        private float _nextViewLog;   // (0.24.126) with Glass View on: what is drawn, every 5 s
        private RenderTexture _o;
        private const CameraEvent DrawEvt = CameraEvent.BeforeForwardAlpha, OffEvt = CameraEvent.AfterForwardAlpha;
        private static readonly int IdFull = Shader.PropertyToID("_GiGlassFull"), IdSunO = Shader.PropertyToID("_GiGlassSunO"),
            IdSunVis = Shader.PropertyToID("_GiGlassSunVis"), IdSunOn = Shader.PropertyToID("_GiGlassSunOn"),
            IdSunSize = Shader.PropertyToID("_GiGlassSunSize"), IdSunDir = Shader.PropertyToID("_GiSunDir"), IdTris = Shader.PropertyToID("_GiGlassTris");

        private void Awake() => _cam = GetComponent<Camera>();
        private void OnDisable() => Detach();

        private void OnDestroy()
        {
            Detach();
            _cb?.Release();
            _cbOff?.Release();
            _cb = _cbOff = null;
            FreeTarget();
            if (_mat != null) Destroy(_mat);
            _mat = null;
        }

        private void Detach()
        {
            if (_on && _cam != null)
            {
                _cam.RemoveCommandBuffer(DrawEvt, _cb);
                _cam.RemoveCommandBuffer(OffEvt, _cbOff);
            }
            _on = false;
        }

        private void FreeTarget()
        {
            if (_o != null) { _o.Release(); Destroy(_o); }
            _o = null;
        }

        private void OnPreRender()
        {
            if (_pass == -2 && GiAssets.Probes != null)
            {
                _mat = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
                _pass = _mat.FindPass("GLASSSUN");
                if (_pass < 0) Plugin.MyLog.LogWarning("[TraceSix] See-Through Glass Windows: the tracesix bundle has no GLASSSUN pass - the glass's sun goes unshadowed. Rebuild the bundle.");
            }
            // the voxel world (TraceSix's light volumes up)
            bool want = GiConfig.On && GiProbes.Ready && !GiTest.GlassNoSun;
            // (1.0.0) VR too (VR Support on): GLASSSUN draws with Unity's per-eye matrices, once an eye (as GiGlassBlur)
            if (!want || _pass < 0 || !GiGlass.Active || GiGlass.Tris == null || (_cam.stereoEnabled && !GiConfig.VrSupport.Value) || !GiGlass.PaneInView(_cam))
            {
                Detach();
                return;
            }
            int fw = _cam.pixelWidth, fh = _cam.pixelHeight, w = Mathf.Max(1, (fw + 1) / 2), h = Mathf.Max(1, (fh + 1) / 2);
            if (_o == null || !_o.IsCreated() || _o.width != w || _o.height != h)
            {
                Detach();   // the buffers name the texture: made anew
                FreeTarget();
                // (a depth of its own, so the nearest pane wins)
                _o = new RenderTexture(w, h, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                    { name = "TraceSix Glass Sun", filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };
                _o.Create();
            }
            if (GiConfig.GlassView.Value != GlassViewMode.Off && Time.unscaledTime >= _nextViewLog)
            {
                _nextViewLog = Time.unscaledTime + 5f;
                Plugin.MyLog.LogInfo("[TraceSix] Glass View: '" + _cam.name + "' draws all the glass (" + GiGlass.TriVerts / 3 + " triangles, one draw call) into "
                    + w + "x" + h + " for its sun shadow and its reflection's sky from the voxel world.");
            }
            if (_cb == null)
            {
                _cb = new CommandBuffer { name = "TraceSix Glass: Its Sun Shadow" };
                _cbOff = new CommandBuffer { name = "TraceSix Glass: Off" };
            }
            _cb.Clear();
            _cb.SetRenderTarget(_o);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.SetGlobalVector(IdFull, new Vector4(fw, fh, 0f, 0f));
            _cb.SetGlobalBuffer(IdTris, GiGlass.Tris);
            _cb.DrawProcedural(Matrix4x4.identity, _mat, _pass, MeshTopology.Triangles, GiGlass.TriVerts);
            _cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);   // (the camera's own target again for the transparents)
            _cb.SetGlobalTexture(IdSunO, _o);
            _cb.SetGlobalTexture(IdSunVis, _o);
            _cb.SetGlobalVector(IdSunSize, new Vector4(w, h, 0f, 0f));
            _cb.SetGlobalFloat(IdSunOn, 1f);
            _cbOff.Clear();
            _cbOff.SetGlobalFloat(IdSunOn, 0f);
            if (!_on)
            {
                _cam.AddCommandBuffer(DrawEvt, _cb);
                _cam.AddCommandBuffer(OffEvt, _cbOff);
                _on = true;
                if (!_said)
                {
                    _said = true;
                    Plugin.MyLog.LogInfo("[TraceSix] See-Through Glass Windows: the glass's sun shadow and its reflection's sky on '" + _cam.name
                        + "' from the voxel world (" + w + "x" + h + " at most, one sample per 2 x 2 of glass; no ray tracing).");
                }
            }
        }
    }
}
