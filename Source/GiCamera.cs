using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // Puts the probe light into the main camera's picture: surface colour x probe light is added to the lit
    // scene at AfterLighting, the same place and the same way the game adds its own ambient, whose sky term is
    // switched off for this camera while this runs (GameAmbient). Lamps, the sun, fog, transparents and
    // post-processing are untouched.
    // (0.24.147) VR (Voxel Light In VR): OnPreRender runs once an eye, and each eye's own view, projection and
    // position go into _cbEye, which runs just before the light pass. The pass keeps nothing between frames, so
    // the eyes share everything else; the moving lights' bounce points (no view in them) are made in the first eye.
    //
    // One of these sits on the scope's camera too (Scope): without it a scope shows the game's flat ambient,
    // so what is seen through it is lit differently from what is around it. The light volumes are the same
    // ones - only this per-picture part runs again, at the scope picture's size. Everything the pass needs
    // is set per camera just before that camera renders, and the game's ambient material is one for all of
    // its cameras (AmbientLight.ManualOnRenderObject), so nothing here is special to the main camera but:
    // no debug views in a scope, no fade-in each time it is raised, no moving lights (the heavy part).
    internal class GiCamera : MonoBehaviour
    {
        public bool Scope;
        private bool _said;
        private Camera _cam;
        private CommandBuffer _cbLight, _cbView, _cbEye;
        private Material _mat;
        private bool _lightOn, _stereo, _saidVr;
        private int _view;   // 0 none, else the debug pass shown
        private float _since = -1f;
        private const CameraEvent LightEvt = CameraEvent.AfterLighting;
        private const CameraEvent ViewEvt = CameraEvent.BeforeImageEffectsOpaque;   // see GiDebugView for why this event
        private const int PassApply = 3, PassLightOnly = 4, PassGrids = 5;
        // Smoothing (TraceSixProbes.shader): the light on its own, a quarter-size copy, the filter, the picture.
        private const int PassGi = 6, PassDown = 7, PassBlur = 8, PassApplySmooth = 9, PassLightOnlySmooth = 10;
        private static readonly int IdRaw = Shader.PropertyToID("_GiRaw"), IdQ0 = Shader.PropertyToID("_GiQuarter0"), IdQ1 = Shader.PropertyToID("_GiQuarter1"),
            IdGuide = Shader.PropertyToID("_GiGuide"), IdSrc = Shader.PropertyToID("_GiSmoothSrc"), IdDir = Shader.PropertyToID("_GiBlurDir"),
            IdXp = Shader.PropertyToID("_GiXp"), IdXn = Shader.PropertyToID("_GiXn"), IdYp = Shader.PropertyToID("_GiYp"),
            IdYn = Shader.PropertyToID("_GiYn"), IdZp = Shader.PropertyToID("_GiZp"), IdZn = Shader.PropertyToID("_GiZn");
        private bool _smooth, _fast, _flash, _borrowAll;
        private int _rt;   // the ray-traced light lights the picture: 1 = this pass list still made (the split view shows it), 2 = none of it
        private int _w, _h, _twice, _passQuarter, _passFlashPts, _passFlashSum, _passApplyEdge = -1;
        // SixBench's detail run (GiTest.Twice): a stage's second run goes into buffers of its own.
        private static readonly int[] IdSpare = { Shader.PropertyToID("_GiSpare0"), Shader.PropertyToID("_GiSpare1"), Shader.PropertyToID("_GiSpare2"),
            Shader.PropertyToID("_GiSpare3"), Shader.PropertyToID("_GiSpare4"), Shader.PropertyToID("_GiSpare5"), Shader.PropertyToID("_GiSpare6"), Shader.PropertyToID("_GiSpare7") };
        // Moving lights (GiLamps.Flash, the shader's FlashLight): their bounce points, a row per light, and a
        // summary of each row.
        private const int FlashRays = 256;      // the shader's FLASH_GROUPS x FLASH_EACH
        private RenderTexture _flashA, _flashB, _flashSum;

        private void Awake() => _cam = GetComponent<Camera>();
        private void OnDisable() => Detach();

        private void OnDestroy()
        {
            Detach();
            _cbLight?.Release();
            _cbView?.Release();
            _cbEye?.Release();
            _cbLight = _cbView = _cbEye = null;
            foreach (RenderTexture rt in new[] { _flashA, _flashB, _flashSum })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _flashA = _flashB = _flashSum = null;
            if (_mat != null) Destroy(_mat);
            _mat = null;
        }

        private static RenderTexture FlashTarget(string name, int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            rt.Create();
            Shader.SetGlobalTexture(name, rt);
            return rt;
        }

        private void Detach()
        {
            if (_lightOn && _cam != null && _cbEye != null) _cam.RemoveCommandBuffer(LightEvt, _cbEye);
            if (_lightOn && _cam != null && _cbLight != null) _cam.RemoveCommandBuffer(LightEvt, _cbLight);
            if (_view != 0 && _cam != null && _cbView != null) _cam.RemoveCommandBuffer(ViewEvt, _cbView);
            _lightOn = false;
            _view = 0;
        }

        private void OnPreRender()
        {
            bool wanted = GiConfig.On && GiProbes.Ready && GiAssets.Probes != null && (!Scope || GiConfig.Scopes.Value);
            bool stereo = _cam.stereoEnabled;
            bool on = wanted && (!stereo || (!Scope && GiConfig.VrSupport.Value)) && _cam.allowHDR && _cam.actualRenderingPath == RenderingPath.DeferredShading;
            if (!on)
            {
                // A scope that cannot take the pass: say why, once (the main camera is known to).
                if (Scope && wanted && !_said)
                {
                    _said = true;
                    Plugin.MyLog.LogWarning("[TraceSix] The scope camera '" + _cam.name + "' does not get the indirect light: HDR " + _cam.allowHDR
                        + ", rendering path " + _cam.actualRenderingPath + ", stereo " + _cam.stereoEnabled + " (needs HDR, DeferredShading, no stereo).");
                }
                Detach();
                _since = -1f;
                return;
            }
            if (_mat == null)
            {
                _mat = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
                _passQuarter = _mat.FindPass("GIQUARTER");
                _passFlashPts = _mat.FindPass("FLASHPTS");
                _passFlashSum = _mat.FindPass("FLASHSUM");
                _passApplyEdge = _mat.FindPass("APPLYEDGE");
            }
            bool smooth = GiConfig.Smoothed, fast = GiConfig.Quartered, flash = !Scope && GiConfig.Flashing;
            // borrowAll: no pixel does a lookup of its own (Edge Pixels Always Borrow) - the final pass without one
            // (APPLYEDGE); else those lookups are done inside the final pass (APPLYSMOOTH).
            bool borrowAll = fast && _passApplyEdge >= 0 && !GiTest.EdgeOwn;
            if (flash && (_flashA == null || !_flashA.IsCreated() || !_flashB.IsCreated() || !_flashSum.IsCreated()))
            {
                foreach (RenderTexture rt in new[] { _flashA, _flashB, _flashSum })
                    if (rt != null) { rt.Release(); Destroy(rt); }
                _flashA = FlashTarget("_GiFlashA", FlashRays, GiLamps.MaxFlash);
                _flashB = FlashTarget("_GiFlashB", FlashRays, GiLamps.MaxFlash);
                _flashSum = FlashTarget("_GiFlashSum", GiLamps.MaxFlash, 2);
                _cbLight?.Clear();
                _flash = false;                 // the pass list names the targets: made anew below
            }
            // (0.24.147) VR: which eye this is (0 = the first drawn, and flat screen)
            int eyeIdx = stereo && _cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;
            // The main camera says which moving lights are followed, every frame; a scope's picture goes without.
            // (VR: the first eye; the second reads the same bounce points)
            if (Scope) Shader.SetGlobalFloat("_GiFlashCount", 0f);
            else if (eyeIdx == 0) GiLamps.Flash(_cam, flash);
            int w = _cam.pixelWidth, h = _cam.pixelHeight, qw = Mathf.Max(1, w / 4), qh = Mathf.Max(1, h / 4);
            int twice = Scope ? 0 : GiTest.Twice;
            // The ray-traced light lights the picture (GiRt.Applying, last frame's word): the last pass is its
            // (RTAPPLY); the rest is made only for "Show Ray Traced Light"'s split, whose other half is this light.
            int rtMode = Scope || !GiRt.Applying ? 0 : GiConfig.RtView.Value == RtViewMode.Split ? 1 : 2;
            if (_cbLight == null || smooth != _smooth || fast != _fast || flash != _flash || borrowAll != _borrowAll || twice != _twice || w != _w || h != _h || rtMode != _rt
                || stereo != _stereo)
            {
                if (_cbLight == null) _cbLight = new CommandBuffer { name = "TraceSix Light" };
                // (VR: the moving lights' passes go in _cbEye, the first eye's only)
                BuildLight(smooth, fast, flash && !stereo, borrowAll, twice, w, h, qw, qh, rtMode);
                _stereo = stereo;
                _rt = rtMode;
                _twice = twice;
                _smooth = smooth;
                _fast = fast;
                _flash = flash;
                _borrowAll = borrowAll;
                _w = w;
                _h = h;
            }
            if (_cbEye == null) _cbEye = new CommandBuffer { name = "TraceSix Light Eye" };
            if (!_lightOn)
            {
                _cam.AddCommandBuffer(LightEvt, _cbEye);   // (first: the same event's buffers run in the order added)
                _cam.AddCommandBuffer(LightEvt, _cbLight);
                _lightOn = true;
                _since = Time.unscaledTime;
                if (Scope) GiStartup.Note("the scope's light pass switched on");
                else GiStartup.LightOn();
                // A scope's camera is switched on and off with every aim: said the first time only.
                if (!Scope || !_said)
                    Plugin.MyLog.LogInfo("[TraceSix] Probe light attached to '" + _cam.name + "'" + (Scope ? " (the scope)" : "") + " at " + LightEvt + " (" + w + "x" + h
                        + (smooth ? ", smoothed at " + qw + "x" + qh + (fast ? ", looked up at that size" : ", looked up for every pixel") : ", not smoothed") + ").");
                _said = true;
            }
            if (stereo && !_saidVr)
            {
                _saidVr = true;
                Plugin.MyLog.LogInfo("[TraceSix] Probe light in VR: each eye of '" + _cam.name + "' lit on its own (" + w + "x" + h + " an eye"
                    + (rtMode == 2 ? "; the ray-traced light lights the picture for now" : "") + ").");
            }
            bool edges = !Scope && GiConfig.EdgeDebug.Value && smooth;
            int view = Scope ? 0 : GiConfig.GridDebug.Value ? PassGrids : (GiConfig.LightDebug.Value || edges ? (smooth ? PassLightOnlySmooth : PassLightOnly) : 0);
            if (view != _view)
            {
                if (_cbView == null) _cbView = new CommandBuffer { name = "TraceSix Light View" };
                if (_view != 0) _cam.RemoveCommandBuffer(ViewEvt, _cbView);
                if (view != 0)
                {
                    _cbView.Clear();
                    _cbView.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    _cbView.DrawProcedural(Matrix4x4.identity, _mat, view, MeshTopology.Triangles, 3);
                    _cam.AddCommandBuffer(ViewEvt, _cbView);
                }
                _view = view;
            }

            Matrix4x4 proj = _cam.projectionMatrix, toWorld = _cam.cameraToWorldMatrix;
            Vector3 camPos = _cam.transform.position;
            if (stereo)
            {
                // (0.24.147) the eye's own: its frustum is off-centre, its position its view's origin
                var eye = eyeIdx == 1 ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                proj = _cam.GetStereoProjectionMatrix(eye);
                toWorld = _cam.GetStereoViewMatrix(eye).inverse;
                camPos = toWorld.MultiplyPoint3x4(Vector3.zero);
            }
            var uvToView = new Vector4(2f / proj.m00, 2f / proj.m11, (proj.m02 - 1f) / proj.m00, (proj.m12 - 1f) / proj.m11);
            Shader.SetGlobalVector("_GiUVToView", uvToView);
            Shader.SetGlobalMatrix("_GiViewToWorld", toWorld);
            Shader.SetGlobalVector("_GiCamPos", camPos);
            Shader.SetGlobalFloat("_GiSight", 1f);
            Shader.SetGlobalFloat("_GiWayOut", 1f);
            Shader.SetGlobalFloat("_GiFast", fast ? 1f : 0f);
            // the last pass's depth test: 5 = "farther than what is there", so the sky's pixels are not shaded
            _mat.SetFloat("_GiSkyTest", 5f);
            Shader.SetGlobalFloat("_GiEdge", GiTest.Edge >= 0 ? GiTest.Edge : 1f);
            Shader.SetGlobalFloat("_GiEdgeShow", edges ? 1f : 0f);
            Shader.SetGlobalFloat("_GiEdgeWide", 1f);
            Shader.SetGlobalFloat("_GiEdgeAll", borrowAll ? 1f : 0f);
            Shader.SetGlobalFloat("_GiFacingFit", !GiTest.NearerFacing ? 1f : 0f);   // (0.24.111)
            Shader.SetGlobalVector("_GiScreen", new Vector4(1f / _cam.pixelWidth, 1f / _cam.pixelHeight, 0f, 0f));
            var quarter = new Vector4(w / (float)qw, h / (float)qh, 0.5f * qh * proj.m11, 0f);
            Shader.SetGlobalVector("_GiQuarter", quarter);
            // The probes take over from the plain sky ambient over a second and a half, not in one frame (a
            // scope's picture at once: its camera is switched on anew with every aim).
            float taken = Scope ? 1f : Mathf.Clamp01((Time.unscaledTime - _since) / 1.5f);
            var apply = new Vector4(taken, GiConfig.Strength.Value, 0f, 0f);
            Shader.SetGlobalVector("_GiApply", apply);
            // (0.24.147) VR: the eye's values in the eye's own buffer too, just before the light pass (as the RT's
            // per-eye buffers carry theirs); then the first eye's moving-light passes
            _cbEye.Clear();
            if (stereo && rtMode != 2)
            {
                _cbEye.SetGlobalVector("_GiUVToView", uvToView);
                _cbEye.SetGlobalMatrix("_GiViewToWorld", toWorld);
                _cbEye.SetGlobalVector("_GiCamPos", camPos);
                _cbEye.SetGlobalVector("_GiScreen", new Vector4(1f / w, 1f / h, 0f, 0f));
                _cbEye.SetGlobalVector("_GiQuarter", quarter);
                _cbEye.SetGlobalVector("_GiApply", apply);
                if (flash && eyeIdx == 0)
                {
                    _cbEye.SetRenderTarget(new RenderTargetIdentifier[] { _flashA, _flashB }, _flashA);
                    _cbEye.DrawProcedural(Matrix4x4.identity, _mat, _passFlashPts, MeshTopology.Triangles, 3);
                    _cbEye.SetRenderTarget(_flashSum);
                    _cbEye.DrawProcedural(Matrix4x4.identity, _mat, _passFlashSum, MeshTopology.Triangles, 3);
                }
            }
            // For other mods: the frame in which this light is in the picture. AOSix's bounce light then reaches
            // only as far as its AO does - beyond that, the surfaces it would gather from are in this light already.
            Shader.SetGlobalFloat("_GiLitFrame", Time.frameCount);
            GameAmbient.Zero();
        }

        // The light pass. Smoothed: the light into a buffer of its own, a quarter-size copy of it with the
        // surfaces it lies on, the filter across and down twice over, then the picture. The buffers are the
        // camera's temporary ones: they live until it has finished, so the debug view later in the frame can
        // still read them.
        // fast: the lookup is done at quarter size in the first place (the smoothing reads no more than that),
        // and keeps the light for each way along each axis for the picture pass to work out every pixel's own
        // detail from.
        // flash: the moving lights' bounce points first, for the quarter-size lookup to gather from.
        // borrowAll: no pixel does a lookup of its own, so the last pass is the one without (APPLYEDGE).
        // twice (SixBench's detail run): one stage is done a second time into spare buffers, for what it costs.
        private void BuildLight(bool smooth, bool fast, bool flash, bool borrowAll, int twice, int w, int h, int qw, int qh, int rt)
        {
            const RenderTextureFormat F = RenderTextureFormat.ARGBHalf;
            _cbLight.Clear();
            if (rt == 2) return;
            if (flash)
            {
                _cbLight.SetRenderTarget(new RenderTargetIdentifier[] { _flashA, _flashB }, _flashA);
                _cbLight.DrawProcedural(Matrix4x4.identity, _mat, _passFlashPts, MeshTopology.Triangles, 3);
                _cbLight.SetRenderTarget(_flashSum);
                _cbLight.DrawProcedural(Matrix4x4.identity, _mat, _passFlashSum, MeshTopology.Triangles, 3);
            }
            if (smooth)
            {
                _cbLight.GetTemporaryRT(IdQ0, qw, qh, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                _cbLight.GetTemporaryRT(IdQ1, qw, qh, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                _cbLight.GetTemporaryRT(IdGuide, qw, qh, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                if (fast)
                {
                    var targets = new RenderTargetIdentifier[] { IdQ0, IdGuide, IdXp, IdXn, IdYp, IdYn, IdZp, IdZn };
                    foreach (int id in new[] { IdXp, IdXn, IdYp, IdYn, IdZp, IdZn })
                        _cbLight.GetTemporaryRT(id, qw, qh, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                    _cbLight.SetRenderTarget(targets, IdQ0);
                    _cbLight.DrawProcedural(Matrix4x4.identity, _mat, _passQuarter, MeshTopology.Triangles, 3);
                    if (twice == 1)
                    {
                        var spare = new RenderTargetIdentifier[8];
                        for (int i = 0; i < 8; i++)
                        {
                            _cbLight.GetTemporaryRT(IdSpare[i], qw, qh, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                            spare[i] = IdSpare[i];
                        }
                        _cbLight.SetRenderTarget(spare, IdSpare[0]);
                        _cbLight.DrawProcedural(Matrix4x4.identity, _mat, _passQuarter, MeshTopology.Triangles, 3);
                    }
                }
                else
                {
                    _cbLight.GetTemporaryRT(IdRaw, w, h, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                    _cbLight.SetRenderTarget(IdRaw);
                    _cbLight.DrawProcedural(Matrix4x4.identity, _mat, PassGi, MeshTopology.Triangles, 3);
                    _cbLight.SetRenderTarget(new RenderTargetIdentifier[] { IdQ0, IdGuide }, IdQ0);
                    _cbLight.DrawProcedural(Matrix4x4.identity, _mat, PassDown, MeshTopology.Triangles, 3);
                    if (twice == 1)
                    {
                        _cbLight.GetTemporaryRT(IdSpare[0], w, h, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                        _cbLight.SetRenderTarget(IdSpare[0]);
                        _cbLight.DrawProcedural(Matrix4x4.identity, _mat, PassGi, MeshTopology.Triangles, 3);
                    }
                }
                for (int i = 0; i < 2; i++)
                {
                    _cbLight.SetGlobalTexture(IdSrc, IdQ0);
                    _cbLight.SetGlobalVector(IdDir, new Vector4(1f, 0f, 0f, 0f));
                    _cbLight.SetRenderTarget(IdQ1);
                    _cbLight.DrawProcedural(Matrix4x4.identity, _mat, PassBlur, MeshTopology.Triangles, 3);
                    _cbLight.SetGlobalTexture(IdSrc, IdQ1);
                    _cbLight.SetGlobalVector(IdDir, new Vector4(0f, 1f, 0f, 0f));
                    _cbLight.SetRenderTarget(IdQ0);
                    _cbLight.DrawProcedural(Matrix4x4.identity, _mat, PassBlur, MeshTopology.Triangles, 3);
                }
                if (twice == 2)
                {
                    // the same four passes again, across and down twice over, between two spare buffers
                    _cbLight.GetTemporaryRT(IdSpare[0], qw, qh, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                    _cbLight.GetTemporaryRT(IdSpare[1], qw, qh, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                    for (int i = 0; i < 4; i++)
                    {
                        _cbLight.SetGlobalTexture(IdSrc, i == 0 ? IdQ0 : IdSpare[(i + 1) & 1]);
                        _cbLight.SetGlobalVector(IdDir, (i & 1) == 0 ? new Vector4(1f, 0f, 0f, 0f) : new Vector4(0f, 1f, 0f, 0f));
                        _cbLight.SetRenderTarget(IdSpare[i & 1]);
                        _cbLight.DrawProcedural(Matrix4x4.identity, _mat, PassBlur, MeshTopology.Triangles, 3);
                    }
                }
                _cbLight.SetGlobalTexture(IdSrc, IdQ0);
            }
            int last = !smooth ? PassApply : borrowAll ? _passApplyEdge : PassApplySmooth;
            if (twice == 3)
            {
                _cbLight.GetTemporaryRT(IdSpare[0], w, h, 0, FilterMode.Point, F, RenderTextureReadWrite.Linear);
                _cbLight.SetRenderTarget(IdSpare[0]);
                _cbLight.DrawProcedural(Matrix4x4.identity, _mat, last, MeshTopology.Triangles, 3);
            }
            if (rt != 0) return;
            _cbLight.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _cbLight.DrawProcedural(Matrix4x4.identity, _mat, last, MeshTopology.Triangles, 3);
        }

        private void OnPostRender() => GameAmbient.Restore();
    }
}
