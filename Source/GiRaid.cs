using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TraceSix.Source
{
    // The raid's lifecycle, every frame: waits for a raid's sky and camera, reads its world once (SetUpRaid),
    // puts the light passes on its cameras, ticks the voxel world / lamps / probes, and lets the map go when the
    // raid is over. Lives beside the Plugin on BepInEx's object.
    internal class GiRaid : MonoBehaviour
    {
        private Coroutine _pending, _lateSetUp;
        private bool _setUp;   // this raid's world has been read (done at raid start if the mod is on, else when it is switched on)
        private int _sources = -1;  // the settings that choose what is voxelised, as the map was last read with
        private static int Sources => (GiConfig.CoarseFull.Value ? 1 : 0) | (GiConfig.ColliderLayer.Value ? 2 : 0) | (GiConfig.CullLayer.Value ? 4 : 0) | (GiConfig.LampFixtures.Value ? 16 : 0);
        private int _raidSky;
        private int _leftAlone;   // the sky object of a raid on a map in "Maps Without Indirect Light": said once
        private Object _sky;   // the raid's sky object: gone with its scenes = the raid is over
        private Camera _cam, _scopeCam;
        private float _nextScopeFind;
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        // (0.24.143) The raid's camera: the game's 'FPS Camera'. Camera.main is that on the flat screen, but in VR (SPT-VR) the
        // main menu's camera is tagged MainCamera too and can still be the main one when the raid is set up - 0.24.142 took
        // 'MainMenuCamera' there: the light volumes centred out in the menu scene (empty), the rays traced for a camera no one
        // sees (his "ray tracing doesn't seem to be working at all in VR"). Camera.main only when it is no stereo camera.
        internal static Camera Cam;

        // (0.24.159) Factory, the Lab and the Labyrinth never get the ray-traced sun shadows (his call: they break those
        // maps); the lamps' shadow rays go on as they do at night, with the sun's part off.
        internal static bool NoSunMap;
        private static bool IsNoSunMap(string map) =>
            map.StartsWith("factory", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(map, "laboratory", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(map, "labyrinth", System.StringComparison.OrdinalIgnoreCase);

        private static Camera FindRaidCamera()
        {
            foreach (Camera c in Camera.allCameras)
                if (c != null && c.name == "FPS Camera") return c;
            Camera m = Camera.main;
            return m != null && !m.stereoEnabled ? m : null;
        }

        private static Camera FindScopeCamera()
        {
            foreach (EFT.CameraControl.OpticComponentUpdater comp in Resources.FindObjectsOfTypeAll<EFT.CameraControl.OpticComponentUpdater>())
            {
                if (comp == null || !comp.gameObject.scene.IsValid()) continue;
                Camera cam = comp.GetComponent<Camera>();
                if (cam != null) return cam;
            }
            return null;
        }
        private float _typicalMs, _prevOwnMs, _lastSpikeLog;
        private string _prevStep;

        private void Awake()
        {
            SceneManager.sceneLoaded += (s, m) =>
            {
                if (_pending != null) StopCoroutine(_pending);
                _pending = StartCoroutine(WaitForRaid());
            };
        }

        // Asked of the input itself: KeyboardShortcut.IsDown() is false while any other key is held (W).
        private static bool ToggleDown()
        {
            KeyboardShortcut k = GiConfig.ToggleKey.Value;
            if (k.MainKey == KeyCode.None || !UnityInput.Current.GetKeyDown(k.MainKey)) return false;
            foreach (KeyCode m in k.Modifiers) if (!UnityInput.Current.GetKey(m)) return false;
            return true;
        }

        private bool _warmed;
        private float _dropWarmAt;

        private void Update()
        {
            GiStartup.Frame();
            // The shaders made ready once, a few seconds after the game has started (the menu): see GiAssets.WarmUp.
            if (!_warmed && Time.unscaledTime > 5f && _raidSky == 0)
            {
                _warmed = true;
                GiAssets.WarmUp();
                _dropWarmAt = Time.unscaledTime + 2f;
            }
            if (_dropWarmAt > 0f && Time.unscaledTime > _dropWarmAt)
            {
                _dropWarmAt = 0f;
                GiAssets.DropWarmUp();
            }
            if (!GiTest.Active && ToggleDown())
            {
                GiConfig.Enabled.Value = !GiConfig.Enabled.Value;
                Plugin.MyLog.LogInfo("[TraceSix] Toggle key: indirect light " + (GiConfig.Enabled.Value ? "on" : "off") + ".");
            }
            GameAmbient.Sync();
            GiRt.Tick(this, _raidSky, _setUp && _pending == null && _lateSetUp == null);
            GiLightPass.Tick();
            GiGlass.Tick();
            if (_raidSky == 0) return;
            if (_sky == null)
            {
                EndRaid();
                return;
            }
            if (_cam == null)
            {
                _cam = FindRaidCamera();
                Cam = _cam;
                if (_cam == null) return;
                if (_cam.GetComponent<GiDebugView>() == null) _cam.gameObject.AddComponent<GiDebugView>();
                if (_cam.GetComponent<GiCamera>() == null) _cam.gameObject.AddComponent<GiCamera>();
                if (_cam.GetComponent<GiRtCamera>() == null) _cam.gameObject.AddComponent<GiRtCamera>();
                if (_cam.GetComponent<GiGlassBlur>() == null) _cam.gameObject.AddComponent<GiGlassBlur>();   // (0.24.124)
                if (_cam.GetComponent<GiGlassSun>() == null) _cam.gameObject.AddComponent<GiGlassSun>();   // (0.24.125)
            }
            // The scope's camera gets the same light pass (GiCamera.Scope). It is the game's, made with the
            // raid's cameras; looked for now and then until found, by the component the game puts on it.
            // (0.24.98) Sun Shadow Mode = RayTracedWorld needs it too: while the scope renders, the game's shadows come back.
            bool rtWorld = GiConfig.RtSunShadowMode.Value == RtSunMode.RayTracedWorld;
            // (0.24.120) "Ray-Traced Sun Shadows In Scopes": the scope traces its own sun rays (GiRtScope)
            bool scopeSun = GiConfig.RtSunScopes.Value && GiConfig.RtTest.Value && GiConfig.RtSunShadows.Value && !NoSunMap;
            if ((GiConfig.Scopes.Value || rtWorld || scopeSun || GiConfig.GlassSeeThrough.Value) && _scopeCam == null && Time.unscaledTime >= _nextScopeFind)
            {
                _nextScopeFind = Time.unscaledTime + 3f;
                _scopeCam = FindScopeCamera();
                if (_scopeCam != null && _scopeCam.GetComponent<GiScopeStamp>() == null) _scopeCam.gameObject.AddComponent<GiScopeStamp>();
            }
            if (_scopeCam != null && GiConfig.Scopes.Value && _scopeCam.GetComponent<GiCamera>() == null) _scopeCam.gameObject.AddComponent<GiCamera>().Scope = true;
            if (_scopeCam != null && scopeSun && _scopeCam.GetComponent<GiRtScope>() == null) _scopeCam.gameObject.AddComponent<GiRtScope>();
            if (_scopeCam != null && _scopeCam.GetComponent<GiGlassBlur>() == null) _scopeCam.gameObject.AddComponent<GiGlassBlur>();   // (0.24.124)
            if (_scopeCam != null && _scopeCam.GetComponent<GiGlassSun>() == null) _scopeCam.gameObject.AddComponent<GiGlassSun>();   // (0.24.125)
            GiSunWorld.Tick(this);
            if (GiConfig.AheadNow.Value)
            {
                GiConfig.AheadNow.Value = false;
                VoxelScene.Ahead(_cam);
            }
            if (GiConfig.OverheadNow.Value)
            {
                GiConfig.OverheadNow.Value = false;
                VoxelScene.Overhead(_cam);
            }
            if (GiConfig.LampsNow.Value)
            {
                GiConfig.LampsNow.Value = false;
                GiLamps.Near(_cam);
            }
            if (GiConfig.LampCensusNow.Value)
            {
                GiConfig.LampCensusNow.Value = false;
                GiLightPass.Census(_cam);
            }
            if (GiConfig.RtSurfaceNow.Value)
            {
                GiConfig.RtSurfaceNow.Value = false;
                GiRt.Surface(_cam);
            }
            if (GiConfig.RtCharNow.Value)
            {
                GiConfig.RtCharNow.Value = false;
                StartCoroutine(GiCharProbe.Run());
            }
            // Switched on during a raid entered with it off: read the world now.
            if (!_setUp && GiConfig.Enabled.Value && _pending == null && _lateSetUp == null)
                _lateSetUp = StartCoroutine(SetUpLate("Switched on during the raid: reading the map now."));
            // What is voxelised has been changed: read the map again with it, and let the light fill again from
            // nothing (what the other voxel world lit would linger for many redos).
            if (_setUp && GiConfig.Enabled.Value && Sources != _sources && _pending == null && _lateSetUp == null)
            {
                VoxelScene.Reset();
                GiLamps.Reset();
                GiProbes.Refill();
                _lateSetUp = StartCoroutine(SetUpLate("What is voxelised has been changed (full detail for the coarse voxels: " + (GiConfig.CoarseFull.Value ? "on" : "off")
                    + ", collider-layer geometry: " + (GiConfig.ColliderLayer.Value ? "on" : "off") + ", lamp fixtures: " + (GiConfig.LampFixtures.Value ? "in" : "left out") + "): reading the map again. The indirect light drops out for some seconds and comes back."));
            }
            GiProbes.SetGrid(GiConfig.Reach.Value * 4);
            GiProbes.SetSlab(GiConfig.Layers.Value);
            if (GiConfig.UnderNow.Value)
            {
                GiConfig.UnderNow.Value = false;
                GiProbes.Under(_cam);
            }
            if (GiConfig.SpotNow.Value)
            {
                GiConfig.SpotNow.Value = false;
                GiProbes.Spot(_cam);
            }
            bool watch = GiConfig.DebugLog.Value;
            if (watch) Watch();
            _sw.Restart();
            VoxelScene.Tick(_cam);
            GiLamps.Tick(_cam);
            GiProbes.Tick(_cam);
            if (VoxelScene.StepLevel >= 0)
                GiStartup.Note("voxel volume " + VoxelScene.StepLevel + (VoxelScene.StepKind == 1 ? " started" : VoxelScene.StepKind == 2 ? " drawn into" : " finished"));
            if (watch)
            {
                _prevOwnMs = (float)_sw.Elapsed.TotalMilliseconds;
                _prevStep = VoxelScene.LastStep;
            }
        }

        // Debug Log: when a frame takes far longer than the recent ones, say what this mod did in it. The
        // frame time Unity reports now is the frame before this one, so it goes with what the last Update
        // recorded. Main-thread time only: what the GPU spent on this mod's draws is not in it.
        private void Watch()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            if (_typicalMs > 0f && ms > Mathf.Max(2f * _typicalMs, _typicalMs + 12f) && Time.unscaledTime - _lastSpikeLog > 0.25f)
            {
                _lastSpikeLog = Time.unscaledTime;
                Plugin.MyLog.LogInfo("[TraceSix] Frame spike: " + ms.ToString("F0") + " ms (typical " + _typicalMs.ToString("F0") + "). This mod in that frame: "
                    + _prevOwnMs.ToString("F1") + " ms on the main thread, " + (_prevStep ?? "probes only") + GiRt.SpikeNote() + ".");
            }
            _typicalMs = _typicalMs <= 0f ? ms : Mathf.Lerp(_typicalMs, Mathf.Min(ms, 2f * _typicalMs), 0.05f);
        }

        // Raid maps load as many additive scenes; this restarts on each one, then waits for the sky and the
        // player camera before taking the list of meshes to voxelise.
        // (0.24.156) Without end: to 0.24.155 it gave up after 150 looks (5 min), and a long loading screen (the map's sky
        // loaded, the player's camera not yet) left the whole raid without the mod. After 5 min it looks every 5 s.
        // (1.0.1) The game world first, a static read: the sky search walks every object in the scene, and in the menu and
        // the hideout - where this waits forever - it ran every 2 s, on or off ("tiny stutters around every 2 seconds").
        private IEnumerator WaitForRaid()
        {
            yield return new WaitForSeconds(5f);
            for (int i = 0; ; i++)
            {
                Object sky = RaidReady() ? FindSky() : null;
                if (sky != null && Camera.main != null)
                {
                    if (sky.GetInstanceID() == _raidSky || sky.GetInstanceID() == _leftAlone) break;
                    yield return new WaitForSeconds(6f);   // late-streaming map pieces
                    if (sky == null || Camera.main == null || !RaidReady()) continue;
                    // A map the mod is told to keep off: nothing is set up, and the game lights it as it does
                    // without the mod.
                    string map = MapId();
                    if (LeftAlone(map))
                    {
                        _leftAlone = sky.GetInstanceID();
                        Plugin.MyLog.LogInfo("[TraceSix] Map '" + map + "' is in \"Maps Without Indirect Light\": the mod stays off for this raid.");
                        break;
                    }
                    NoSunMap = IsNoSunMap(map);
                    Plugin.MyLog.LogInfo("[TraceSix] Map '" + map + "'." + (NoSunMap ? " No ray-traced sun shadows on this map: the game's own stay." : ""));
                    _raidSky = sky.GetInstanceID();
                    _sky = sky;
                    _cam = null;
                    _scopeCam = null;
                    _setUp = false;
                    _rereads = 0;
                    VoxelScene.Reset();
                    GiProbes.Reset();
                    GiLamps.Reset();
                    if (sky is EFT.Weather.TODSkySimple)
                        Plugin.MyLog.LogInfo("[TraceSix] This map has the game's simple sky (no TOD_Sky): its ambient is taken as the sky light, as on the open maps.");
                    GiProbes.FindSun(sky);
                    if (GiConfig.Enabled.Value) yield return StartCoroutine(SetUpRaid());
                    break;
                }
                yield return new WaitForSeconds(i < 150 ? 2f : 5f);
            }
            _pending = null;
        }

        // The raid is over: let go of its map. The list of what is voxelised holds every mesh of the map and
        // every material on them (and through those their textures), and the voxel materials hold the textures
        // too. The game frees a map with Resources.UnloadUnusedAssets when it returns to the menu, and that
        // leaves alone whatever a static field still reaches: kept until the next raid's set-up (as up to
        // 0.23.0), the whole map stayed in memory in the menu, and beside the next map while that one loaded.
        private void EndRaid()
        {
            _raidSky = 0;
            _sky = null;
            _cam = null;
            Cam = null;
            NoSunMap = false;
            _scopeCam = null;
            _setUp = false;
            if (_lateSetUp != null) { StopCoroutine(_lateSetUp); _lateSetUp = null; }
            if (_reread != null) { StopCoroutine(_reread); _reread = null; }
            VoxelScene.Reset();
            GiProbes.Reset();
            GiLamps.Reset();
            GiEmission.Reset();
            GiGlass.Restore(true);
            GiRt.EndRaid();
            Plugin.MyLog.LogInfo("[TraceSix] The raid is over: the map's meshes, materials and lamps are let go of.");
            StartCoroutine(FreeUnused());
        }

        // The game's own pass over unused assets may have run before the lists were dropped: once more, after
        // its tear-down has had a moment.
        private IEnumerator FreeUnused()
        {
            yield return new WaitForSecondsRealtime(3f);
            if (_raidSky == 0) yield return Resources.UnloadUnusedAssets();
        }

        // The raid's world, read once: the game's ambient, its culling lists, the openings, the interior
        // volumes, the lamps, the meshes. Each scene search in a frame of its own (they ran in one frame to
        // 0.24.59: one long hitch eleven seconds into the raid), timed for the start-up report (GiStartup).
        private IEnumerator SetUpRaid()
        {
            _setUp = true;
            _sources = Sources;
            GiStartup.Begin();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!GiAssets.Load()) yield break;
            GiStartup.Step("the shaders", sw);
            yield return null;
            sw.Restart();
            GameAmbient.Locate();
            GiStartup.Step("the game's ambient", sw);
            yield return null;
            sw.Restart();
            GiCulling.Collect();     // first: the rest asks it what is only culled for now
            GiStartup.Step("the culling lists", sw);
            yield return null;
            sw.Restart();
            VoxelScene.CollectHoles();
            GiStartup.Step("the door and window markers", sw);
            yield return null;
            sw.Restart();
            GiProbes.CollectIndoor();
            GiStartup.Step("the interior volumes", sw);
            yield return null;
            sw.Restart();
            GiLamps.Collect();
            GiEmission.Begin();      // (0.24.155) the lights' places, for which glowing surfaces give light
            GiStartup.Step("the lamps", sw);
            yield return null;
            sw.Restart();
            GiGlass.Apply();         // (0.24.117) before the meshes: the glass goes into the ray tracing see-through
            GiStartup.Step("the glass", sw);
            yield return null;
            sw.Restart();
            yield return StartCoroutine(VoxelScene.Collect());
            GiStartup.Step("the meshes (over many frames)", sw);
            GiEmission.Log();
            if (VoxelScene.SourceCount < FewMeshes && _rereads < MostRereads && _reread == null)
            {
                _rereads++;
                _reread = StartCoroutine(ReadAgain(VoxelScene.SourceCount));
            }
        }

        // The raid's map as the game names it (GameWorld.LocationId: "bigmap", "laboratory", "Labyrinth" ...).
        private static string MapId()
        {
            try
            {
                EFT.GameWorld world = World();
                return world != null ? world.LocationId ?? "" : "";
            }
            catch (System.Exception) { return ""; }
        }

        // (1.0.1) The game world the game registers (Singleton<GameWorld>): a static read, no scene search. Null in the menu.
        private static EFT.GameWorld World() => Singleton<EFT.GameWorld>.Instantiated ? Singleton<EFT.GameWorld>.Instance : null;

        // (1.0.0) The raid is really there: its game world, with the map's name and the player in it. The sky and a camera
        // were taken as the sign to 1.0.0 - in VR the headset camera is there all through the loading, and a Fika client's
        // map loads late: his player's Customs raid was read 40 log lines before the game said "Location: Customs" - ONE
        // mesh, the whole raid lit with an empty voxel world (light through every wall).
        private static bool RaidReady()
        {
            try
            {
                EFT.GameWorld world = World();
                return world != null && !(world is HideoutGameWorld) && !string.IsNullOrEmpty(world.LocationId) && world.MainPlayer != null;
            }
            catch (System.Exception) { return false; }
        }

        // ... and if a map is read and holds almost no meshes all the same (still streaming in), it is read again a little
        // later, a few times, instead of lighting the raid with an empty voxel world.
        private const int FewMeshes = 100, MostRereads = 5;
        private int _rereads;
        private Coroutine _reread;

        private IEnumerator ReadAgain(int found)
        {
            Plugin.MyLog.LogWarning("[TraceSix] Only " + found + " meshes found on this map: it is probably still loading. Reading it again in 10 s ("
                + _rereads + " of " + MostRereads + ").");
            int raid = _raidSky;
            yield return new WaitForSecondsRealtime(10f);
            _reread = null;                                  // (cleared first: the set-up below may start the next one)
            if (raid == _raidSky && _setUp && GiConfig.Enabled.Value)
            {
                VoxelScene.Reset();
                GiLamps.Reset();
                GiProbes.Refill();
                yield return StartCoroutine(SetUpRaid());
            }
        }

        private static bool LeftAlone(string map)
        {
            if (string.IsNullOrEmpty(map)) return false;
            foreach (string s in GiConfig.SkipMaps.Value.Split(','))
                if (string.Equals(s.Trim(), map, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The game's sky object: TOD_Sky on the open maps, EFT.Weather.TODSkySimple on the ones without it (Factory,
        // the Lab). The simple one feeds the same ambient (AmbientLight.SetSH, every frame) and has a directional
        // light of its own, so nothing else differs. It is taken only in a raid's game world, in case the hideout
        // carries one.
        private static Object FindSky()
        {
            Object sky = FindObjectOfType<TOD_Sky>();
            if (sky != null) return sky;
            sky = FindObjectOfType<EFT.Weather.TODSkySimple>();
            if (sky == null) return null;
            EFT.GameWorld world = World();
            return world != null && !(world is HideoutGameWorld) ? sky : null;
        }

        private IEnumerator SetUpLate(string why)
        {
            Plugin.MyLog.LogInfo("[TraceSix] " + why);
            yield return StartCoroutine(SetUpRaid());
            _lateSetUp = null;
        }
    }
}
