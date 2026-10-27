using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // (0.24.124) "See-Through Glass Windows": what the glass shows behind it - the camera's picture just before the transparents,
    // copied at half size with its mips (HDRP's colour pyramid) and released after them; the glass reads it at the mip about as
    // wide as its blur (TraceSixProbes GLASS: 4 taps). Made only while one of the panes is in this camera's view
    // (GiGlass.PaneInView). It replaces the glass shader's GrabPass: a full-size copy read 12 times per glass pixel. On the main
    // camera and the scope's; any other camera drawing the glass finds _GiGlassBlurOn 0 and shows its own look only.
    // (0.24.138) The mips made by GLASSDOWN - the 13-tap downsample of Call of Duty: Advanced Warfare's bloom, bright specks damped
    // on the first level (a partial Karis average) - each level drawn into a texture of its own and copied into its mip. Unity's
    // GenerateMips averaged 2 x 2 boxes: as the view moved, bright specks hopped between coarse texels and the blur shimmered (his
    // "the blur of what's behind the glass flickers as I move"). Without the pass (an older bundle): GenerateMips as before.
    internal class GiGlassBlur : MonoBehaviour
    {
        private const int Levels = 6;   // (the glass reads up to about mip 4.4: a 3 % blur of a 1440-line picture)
        private Camera _cam;
        private CommandBuffer _before, _after;
        private Material _mat;
        private int _pass = -2;
        private bool _on;
        private int _w, _h;
        private static readonly int IdBlur = Shader.PropertyToID("_GiGlassBlur"), IdOn = Shader.PropertyToID("_GiGlassBlurOn"),
            IdSrc = Shader.PropertyToID("_GiGlassDownSrc"), IdKaris = Shader.PropertyToID("_GiGlassDownKaris");
        private static readonly int[] IdLevel = new int[Levels];
        private const CameraEvent BeforeEvt = CameraEvent.BeforeForwardAlpha, AfterEvt = CameraEvent.AfterForwardAlpha;

        static GiGlassBlur()
        {
            for (int k = 0; k < Levels; k++) IdLevel[k] = Shader.PropertyToID("_GiGlassBlurLevel" + (k + 1));
        }

        private void Awake() => _cam = GetComponent<Camera>();
        private void OnDisable() => Detach();

        private void OnDestroy()
        {
            Detach();
            _before?.Release();
            _after?.Release();
            _before = _after = null;
            if (_mat != null) Destroy(_mat);
            _mat = null;
        }

        private void Detach()
        {
            if (_on && _cam != null)
            {
                _cam.RemoveCommandBuffer(BeforeEvt, _before);
                _cam.RemoveCommandBuffer(AfterEvt, _after);
            }
            _on = false;
        }

        private void OnPreCull()
        {
            // (1.0.0) VR too (VR Support on): multi-pass draws each eye on its own, this camera's command buffers run once an eye
            // (each eye's own copy), and the glass reads it with the camera's own per-eye matrices and screen size
            bool want = GiGlass.Active && (!_cam.stereoEnabled || GiConfig.VrSupport.Value) && !GiTest.GlassNoCopy && GiGlass.PaneInView(_cam);
            if (!want)
            {
                if (_on) Detach();
                return;
            }
            int w = Mathf.Max(1, _cam.pixelWidth / 2), h = Mathf.Max(1, _cam.pixelHeight / 2);
            if (_on && w == _w && h == _h) return;
            Detach();
            if (_pass == -2 && GiAssets.Probes != null)
            {
                _mat = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
                _pass = _mat.FindPass("GLASSDOWN");
            }
            if (_before == null) _before = new CommandBuffer { name = "TraceSix Glass: The Picture Behind (half size, mips)" };
            if (_after == null) _after = new CommandBuffer { name = "TraceSix Glass: Release" };
            int levels = 0;
            while (levels < Levels && (w >> (levels + 1)) >= 2 && (h >> (levels + 1)) >= 2) levels++;
            bool own = _pass >= 0 && levels > 0;
            _before.Clear();
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.DefaultHDR, 0) { useMipMap = true, autoGenerateMips = false, msaaSamples = 1 };
            if (own) desc.mipCount = levels + 1;
            _before.GetTemporaryRT(IdBlur, desc, FilterMode.Trilinear);   // (a temporary is also set as the global of its name)
            _before.Blit(BuiltinRenderTextureType.CameraTarget, IdBlur);
            if (own)
            {
                // each level from the one above, into a texture of its own (a mip of the texture being read cannot be drawn into),
                // then copied into its mip
                var ld = new RenderTextureDescriptor(w, h, RenderTextureFormat.DefaultHDR, 0) { msaaSamples = 1 };
                for (int k = 1; k <= levels; k++)
                {
                    ld.width = Mathf.Max(1, w >> k);
                    ld.height = Mathf.Max(1, h >> k);
                    _before.GetTemporaryRT(IdLevel[k - 1], ld, FilterMode.Bilinear);
                    _before.SetGlobalTexture(IdSrc, k == 1 ? IdBlur : IdLevel[k - 2]);
                    _before.SetGlobalFloat(IdKaris, k == 1 ? 1f : 0f);
                    _before.SetRenderTarget(IdLevel[k - 1]);
                    _before.DrawProcedural(Matrix4x4.identity, _mat, _pass, MeshTopology.Triangles, 3);
                    _before.CopyTexture(IdLevel[k - 1], 0, 0, IdBlur, 0, k);
                }
                for (int k = 1; k <= levels; k++) _before.ReleaseTemporaryRT(IdLevel[k - 1]);
            }
            else _before.GenerateMips(IdBlur);
            _before.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);   // (the camera's own target again for the transparents)
            _before.SetGlobalFloat(IdOn, 1f);
            _after.Clear();
            _after.ReleaseTemporaryRT(IdBlur);
            _after.SetGlobalFloat(IdOn, 0f);
            _cam.AddCommandBuffer(BeforeEvt, _before);
            _cam.AddCommandBuffer(AfterEvt, _after);
            _on = true;
            _w = _pass == -2 ? 0 : w;   // (the bundle not loaded yet: made again next frame, with the pass once it is)
            _h = h;
        }
    }
}
