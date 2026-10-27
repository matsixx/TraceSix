using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // "Show Voxel World": draws the voxel volumes over the lit opaque scene on the main camera.
    // At BeforeImageEffectsOpaque, NOT BeforeImageEffects: under render scaling the game's post-processing
    // buffer (added at BeforeImageEffects before ours) takes the picture into its own full-size targets, and
    // anything drawn into the camera target after it never reaches the screen (first build: nothing showed).
    // Here the camera target is still the live picture (AOSix captures its bounce source at this event); fog,
    // transparents and post-processing then run on top of the view.
    internal class GiDebugView : MonoBehaviour
    {
        private Camera _cam;
        private CommandBuffer _cb;
        private Material _mat;
        private bool _attached;
        private const CameraEvent Evt = CameraEvent.BeforeImageEffectsOpaque;

        private void Awake() => _cam = GetComponent<Camera>();
        private void OnDisable() => Detach();

        private void OnDestroy()
        {
            Detach();
            _cb?.Release();
            _cb = null;
            if (_mat != null) Destroy(_mat);
            _mat = null;
        }

        private void Detach()
        {
            if (_attached && _cam != null && _cb != null) _cam.RemoveCommandBuffer(Evt, _cb);
            _attached = false;
        }

        private void OnPreRender()
        {
            if (!GiConfig.On || !GiConfig.VoxelDebug.Value || !VoxelScene.Ready || GiAssets.Debug == null || !GiAssets.Debug.isSupported)
            {
                Detach();
                return;
            }
            if (_mat == null) _mat = new Material(GiAssets.Debug) { hideFlags = HideFlags.DontSave };
            if (!_attached)
            {
                if (_cb == null) _cb = new CommandBuffer { name = "TraceSix Voxel View" };
                _cb.Clear();
                _cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                _cb.DrawProcedural(Matrix4x4.identity, _mat, 0, MeshTopology.Triangles, 3);
                _cam.AddCommandBuffer(Evt, _cb);
                _attached = true;
                Plugin.MyLog.LogInfo("[TraceSix] Voxel view attached to '" + _cam.name + "' at " + Evt + " (" + _cam.pixelWidth + "x" + _cam.pixelHeight + ").");
            }
            // Same view reconstruction as AOSix: uv -> view ray, then to world.
            Matrix4x4 proj = _cam.projectionMatrix;
            Shader.SetGlobalVector("_GiUVToView", new Vector4(2f / proj.m00, 2f / proj.m11, (proj.m02 - 1f) / proj.m00, (proj.m12 - 1f) / proj.m11));
            Shader.SetGlobalMatrix("_GiViewToWorld", _cam.cameraToWorldMatrix);
            Shader.SetGlobalVector("_GiCamPos", _cam.transform.position);
            Shader.SetGlobalVector("_GiScreen", new Vector4(1f / _cam.pixelWidth, 1f / _cam.pixelHeight, 0f, 0f));
        }
    }
}
