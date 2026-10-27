using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // (0.24.120) "Ray-Traced Sun Shadows In Scopes": the scope's camera traces sun rays of its own, the main camera's sun path at
    // the scope picture's size - RTSUNRAYS (one per 2 x 2) at its BeforeLighting with the D3D12 side's CSSun started on a list of
    // its own (event 28 + slot), collected before its sun's shadow mask (32 + slot) and kept over frames per pixel (RTSUNTEMPORAL,
    // its own history), put into its mask after (RTSUNAPPLY) - and into EFT's distant-shadow mask for the scope
    // (DistantShadow._shadowMaskFullOptic) when it has one. Everything the passes read of a camera is set in these buffers (the
    // scope may have no GiCamera of its own). While it traces, GiSunWorld keeps the map out of the game's sun shadow
    // (RayTracedWorld) instead of handing it back for the scope. The sun light's buffers go on in OnPreCull and come off in
    // OnPreRender, as GiRtCamera's: Unity takes a light's buffers when a camera culls.
    internal class GiRtScope : MonoBehaviour
    {
        private Camera _cam;
        private Material _mat;
        private int _passSunRays = -1, _passSunTemporal = -1, _passSunApply = -1;
        private CommandBuffer _cbRays, _cbSunBefore, _cbSunAfter;
        private bool _camOn, _hadSun, _hasPrev, _said, _saidOff;
        private Light _sunLight;
        private RenderTexture _sunO, _sunRaw;
        private readonly RenderTexture[] _sunHist = new RenderTexture[2];
        private IntPtr _pSunO, _pSunRaw;
        private int _cur;
        private Matrix4x4 _prevW2V;
        private Vector4 _prevUV;
        private readonly float[] _c = new float[32];   // (the native side reads 32: kConst)
        private const CameraEvent RaysEvt = CameraEvent.BeforeLighting;
        // EFT's distant-shadow mask for the scope's picture (it keeps one per camera: the main one is GiRtCamera's)
        private DistantShadow _ds;
        private float _dsNextFind;
        private static readonly int IdScreen = Shader.PropertyToID("_GiScreen"), IdUVToView = Shader.PropertyToID("_GiUVToView"),
            IdViewToWorld = Shader.PropertyToID("_GiViewToWorld"), IdGiCamPos = Shader.PropertyToID("_GiCamPos"),
            IdRtCamPos = Shader.PropertyToID("_GiRtCamPos"), IdFacingFit = Shader.PropertyToID("_GiFacingFit"),
            IdSunDir = Shader.PropertyToID("_GiSunDir"), IdSunP = Shader.PropertyToID("_GiRtSunP"), IdSunGrid = Shader.PropertyToID("_GiRtSunGrid"),
            IdSunRaw = Shader.PropertyToID("_GiRtSunRaw"), IdSunHist = Shader.PropertyToID("_GiRtSunHist"), IdSunTex = Shader.PropertyToID("_GiRtSunTex"),
            IdSunSkipAway = Shader.PropertyToID("_GiRtSunSkipAway"),
            IdPrevUV = Shader.PropertyToID("_GiRtPrevUV"), IdPrevW2V = Shader.PropertyToID("_GiRtPrevW2V");

        private void Awake() => _cam = GetComponent<Camera>();
        // (a scope's camera is switched off between aims: its history starts over with the next)
        private void OnDisable() => Detach();

        private void OnDestroy()
        {
            Detach();
            foreach (CommandBuffer b in new[] { _cbRays, _cbSunBefore, _cbSunAfter }) b?.Release();
            _cbRays = _cbSunBefore = _cbSunAfter = null;
            FreeSun();
            if (_mat != null) Destroy(_mat);
            _mat = null;
        }

        private void OnPreRender() => DetachSun();
        private void OnPostRender() => DetachSun();

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
            if (_camOn && _cam != null)
            {
                _cam.RemoveCommandBuffer(RaysEvt, _cbRays);
            }
            _camOn = false;
            _hasPrev = false;
        }

        private void FreeSun()
        {
            foreach (RenderTexture rt in new[] { _sunO, _sunRaw, _sunHist[0], _sunHist[1] })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _sunO = _sunRaw = _sunHist[0] = _sunHist[1] = null;
            _pSunO = _pSunRaw = IntPtr.Zero;
            _hadSun = false;
            _hasPrev = false;
        }

        // GiRtCamera.EnsureSun's, at the scope picture's size
        private bool EnsureSun(int w, int h)
        {
            int sw = (w + 1) / 2, sh = (h + 1) / 2;
            bool ok = GiRtCamera.Created(_sunO) && GiRtCamera.Created(_sunRaw) && GiRtCamera.Created(_sunHist[0]) && GiRtCamera.Created(_sunHist[1])
                      && _sunO.width == sw && _sunO.height == sh && _sunHist[0].width == w && _sunHist[0].height == h;
            if (ok) return true;
            FreeSun();
            _sunO = GiRtCamera.Target("TraceSixRT Scope Sun Ray Starts", sw, sh, RenderTextureFormat.ARGBHalf);
            _sunRaw = GiRtCamera.Target("TraceSixRT Scope Sun Traced", sw, sh, RenderTextureFormat.ARGBHalf);
            for (int i = 0; i < 2; i++) _sunHist[i] = GiRtCamera.Target("TraceSixRT Scope Sun Shadow " + i, w, h, RenderTextureFormat.ARGBHalf);
            _pSunO = _sunO.GetNativeTexturePtr();
            _pSunRaw = _sunRaw.GetNativeTexturePtr();
            return true;
        }

        // EFT's distant-shadow mask of the scope's picture, when it precomputes one at its size (else null)
        private RenderTexture DistantMask(int w, int h)
        {
            if (_ds == null && Time.unscaledTime >= _dsNextFind)
            {
                _dsNextFind = Time.unscaledTime + 5f;
                _ds = UnityEngine.Object.FindObjectOfType<DistantShadow>();
            }
            if (_ds == null || !_ds.PreComputeMask) return null;
            RenderTexture rt = _ds._shadowMaskFullOptic;
            return rt != null && rt.IsCreated() && rt.width == w && rt.height == h ? rt : null;
        }

        // The main camera's conditions (GiRtCamera.RtOn + its sun's), and the setting.
        private bool Wanted(Light sunL) =>
            GiConfig.RtSunScopes.Value && GiConfig.RtTest.Value && !GiTest.RtOff && GiRt.Ready && GiConfig.On && GiProbes.Ready && GiAssets.Probes != null
            && !_cam.stereoEnabled && _cam.allowHDR && _cam.actualRenderingPath == RenderingPath.DeferredShading
            && GiConfig.RtSunShadows.Value && !GiTest.RtNoSun && !GiTest.RtLate && !GiRaid.NoSunMap
            && sunL != null && sunL.isActiveAndEnabled && sunL.shadows != LightShadows.None && Shader.GetGlobalVector(IdSunDir).w > 0.5f;

        private void OnPreCull()
        {
            DetachSun();
            Light sunL = GiProbes.Sun;
            if (!Wanted(sunL))
            {
                if (_camOn) Detach();
                if (_sunO != null) FreeSun();
                return;
            }
            if (_mat == null)
            {
                _mat = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
                _passSunRays = _mat.FindPass("RTSUNRAYS");
                _passSunTemporal = _mat.FindPass("RTSUNTEMPORAL");
                _passSunApply = _mat.FindPass("RTSUNAPPLY");
            }
            int w = _cam.pixelWidth, h = _cam.pixelHeight;
            if (_passSunRays < 0 || _passSunTemporal < 0 || _passSunApply < 0 || w < 4 || h < 4 || !EnsureSun(w, h))
            {
                if (!_saidOff)
                {
                    _saidOff = true;
                    Plugin.MyLog.LogWarning("[TraceSix] Ray tracing: the scope's sun rays cannot run (" + (_passSunRays < 0 || _passSunTemporal < 0 || _passSunApply < 0
                        ? "the bundle lacks RTSUNRAYS / RTSUNTEMPORAL / RTSUNAPPLY" : "its picture is " + w + "x" + h) + "): the scope keeps the game's shadows.");
                }
                if (_camOn) Detach();
                return;
            }

            Vector3 pos = _cam.transform.position;
            Matrix4x4 proj = _cam.projectionMatrix, w2v = _cam.worldToCameraMatrix;
            int slot = Time.frameCount & 3, phase = Time.frameCount & 3;
            var sunP = new Vector4((phase == 1 || phase == 2) ? 1f : 0f, (phase == 1 || phase == 3) ? 1f : 0f, (!_hasPrev || !_hadSun) ? 1f : 0f, 0f);
            bool rtWorld = GiConfig.RtSunShadowMode.Value == RtSunMode.RayTracedWorld;

            // CSSun's constants (GiRtCamera's: the sun, the seed, Cover, the camera, the ray length, the materials, the sun's
            // size, what runs - the sun, no lamps in a scope - and RayTracedWorld's keep-out)
            Vector4 sun = Shader.GetGlobalVector(IdSunDir);
            Array.Clear(_c, 0, _c.Length);
            _c[0] = sun.x; _c[1] = sun.y; _c[2] = sun.z; _c[3] = sun.w;
            _c[4] = _sunO.width; _c[5] = _sunO.height; _c[6] = Time.frameCount & 0xffff; _c[7] = GiConfig.RtCover.Value;
            _c[8] = pos.x; _c[9] = pos.y; _c[10] = pos.z;
            _c[14] = GiConfig.RtLength.Value;
            _c[15] = GiConfig.SunPassOwn.Value ? 1f : 0f;   // (0.24.125, a test: CSSun's BlockedSun)
            _c[20] = GiTest.RtSun1000 ? 1000f : GiConfig.RtSunLen.Value;
            bool tex = GiRt.AtlasTile > 0 && GiConfig.RtTextures.Value && !GiTest.RtNoTex;
            _c[24] = 0.05f; _c[25] = 0.0015f; _c[26] = tex ? GiRt.AtlasTile : 0f; _c[27] = GiRt.AtlasCols;
            _c[28] = Mathf.Clamp(GiConfig.RtSunSoft.Value, 0f, 3f) * Mathf.Deg2Rad; _c[29] = 1f;
            _c[30] = rtWorld ? 1f : 0f; _c[31] = QualitySettings.shadowDistance;
            GiRt.SetScope(slot, _pSunO, _pSunRaw, IntPtr.Zero, _c);

            // what the passes read of a camera: this one's
            var screen = new Vector4(1f / w, 1f / h, 0f, 0f);
            Vector4 uvToView = GiRtCamera.UVToView(proj);
            Matrix4x4 v2w = _cam.cameraToWorldMatrix;
            var camPos = new Vector4(pos.x, pos.y, pos.z, 0f);
            float facingFit = !GiTest.NearerFacing ? 1f : 0f;
            if (_cbRays == null) _cbRays = new CommandBuffer { name = "TraceSix RT Scope Sun Rays" };
            if (_cbSunBefore == null) _cbSunBefore = new CommandBuffer { name = "TraceSix RT Scope Sun Collect" };
            if (_cbSunAfter == null) _cbSunAfter = new CommandBuffer { name = "TraceSix RT Scope Sun Into The Mask" };
            CommandBuffer rc = _cbRays, sb = _cbSunBefore, sa = _cbSunAfter;
            foreach (CommandBuffer b in new[] { rc, sb, sa })
            {
                b.Clear();
                b.SetGlobalVector(IdScreen, screen);
                b.SetGlobalVector(IdUVToView, uvToView);
                b.SetGlobalMatrix(IdViewToWorld, v2w);
                b.SetGlobalVector(IdGiCamPos, camPos);
                b.SetGlobalVector(IdRtCamPos, camPos);
                b.SetGlobalFloat(IdFacingFit, facingFit);
                b.SetGlobalVector(IdSunP, sunP);
            }
            // the rays (before the game lights the scope's picture), and the trace started
            rc.SetGlobalFloat(IdSunSkipAway, GiConfig.RtSunSkipAway.Value || GiTest.RtSunSkip ? 1f : 0f);   // (0.24.140)
            rc.SetRenderTarget(_sunO);
            rc.DrawProcedural(Matrix4x4.identity, _mat, _passSunRays, MeshTopology.Triangles, 3);
            rc.SetRenderTarget(_sunRaw);   // (the ray texture unbound before the native side copies it)
            if (!GiTest.RtNoTrace) rc.IssuePluginEvent(GiRt.Func, 28 + slot);
            rc.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);

            // collected before the sun's shadow mask, kept per pixel over frames; into the mask after
            int prev = _cur;
            _cur ^= 1;
            if (!GiTest.RtNoTrace) sb.IssuePluginEvent(GiRt.Func, 32 + slot);
            sb.SetGlobalVector(IdSunGrid, new Vector4(_sunO.width, _sunO.height, 0f, 0f));
            sb.SetGlobalTexture(IdSunRaw, _sunRaw);
            sb.SetGlobalTexture(IdSunHist, _sunHist[prev]);
            sb.SetGlobalVector(IdPrevUV, _prevUV);
            sb.SetGlobalMatrix(IdPrevW2V, _prevW2V);
            sb.SetRenderTarget(_sunHist[_cur]);
            sb.DrawProcedural(Matrix4x4.identity, _mat, _passSunTemporal, MeshTopology.Triangles, 3);
            sb.SetGlobalTexture(IdSunTex, _sunHist[_cur]);
            sa.DrawProcedural(Matrix4x4.identity, _mat, _passSunApply, MeshTopology.Triangles, 3);   // (into the mask: the active target)
            RenderTexture far = DistantMask(w, h);
            if (far != null)
            {
                sa.SetRenderTarget(far);
                if (rtWorld) sa.ClearRenderTarget(false, true, Color.white);
                sa.DrawProcedural(Matrix4x4.identity, _mat, _passSunApply, MeshTopology.Triangles, 3);
            }
            sunL.AddCommandBuffer(LightEvent.BeforeScreenspaceMask, sb);
            sunL.AddCommandBuffer(LightEvent.AfterScreenspaceMask, sa);
            _sunLight = sunL;
            if (!_camOn)
            {
                _cam.AddCommandBuffer(RaysEvt, _cbRays);
                _camOn = true;
            }
            GiSunWorld.Want(rtWorld);
            GiSunWorld.ScopeTraced();
            if (!_said)
            {
                _said = true;
                Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: the scope '" + _cam.name + "' traces its own sun rays (" + _sunO.width + "x" + _sunO.height
                    + " rays for its " + w + "x" + h + " picture) into its shadow mask" + (far != null ? " and EFT's distant-shadow mask for it." : "."));
            }
            _hadSun = true;
            _prevW2V = w2v;
            _prevUV = uvToView;
            _hasPrev = true;
        }
    }
}
