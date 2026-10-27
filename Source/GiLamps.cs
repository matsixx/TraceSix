using System.Collections.Generic;
using UnityEngine;

namespace TraceSix.Source
{
    // The map's lamps, for the light volumes' rays: where a ray ends on a surface, that surface is also lit by
    // the lamps in reach of it (TraceSixProbes: LampLight), so their light bounces as the sun's does.
    // The reference (Yudintsev 2019) gets lamps into the voxel scene by lighting G-buffer samples with the
    // engine's own light list. A mod has the lit frame, not that list - and the frame carries the sun, the
    // highlights and only what is on screen. So the list is kept here, and what a lamp reaches is tested through
    // the voxels: the game's lamps cast no shadows (3 of ~610 on Customs).
    // No scanning after the raid has loaded (a weapon's lights are announced: GiWeaponSpawnPatch). Reading a light is an
    // engine call, so every light's values are cached and a slice of the map's lights is re-read each frame
    // (FogSix's cost model: 6,862 lights on Streets). The picture does not wait on this: the light volumes take
    // far longer to settle anyway.
    internal static class GiLamps
    {
        public const int Max = 64;                 // the shader's arrays (LAMPS)
        // Lights re-read per frame. At 64 the lamp tick took 0.09 ms of main-thread time a frame (his cost test,
        // 2026-10-08: 0.147 ms with lamps, 0.057 without); a lamp that is switched changes the light volumes
        // over many seconds anyway, so a few seconds until it is noticed costs nothing.
        private const int ReadPerFrame = 16;
        private const float MovedSq = 0.25f;       // a light seen more than 0.5 m from where it was is not a lamp

        private sealed class Lamp
        {
            public Light light;
            public CullingLightObject fader;       // the game's CullingLightObject on it, if any
            public bool spot, on, enabled, moves, gone, read;
            public bool rtLit;                     // (1.0.0) a moving light: lit as PickMoving last saw it ("Instant Light Switches")
            public bool ir;                        // an infrared light (the game's IkLight): there only through night vision
            public Vector3 pos, fwd;
            public float range, cosHalf, power;    // power = its intensity, not faded by distance
            public float start;                    // a spot's: how far along its axis its light starts (StartOf)
            public Color col;                      // colour x intensity, as the game's light pass takes it
            public Vector3 up;                     // a spot's, for its cookie
            public Texture2D cookie;               // a spot's (the game's light pass shapes its beam by the alpha)
            public bool fill;                      // by its name the artists' stand-in for bounced light (IsFill)
            public Lamp[] mates;                   // a fill light's: the other lights of its lamp (same parent), the real ones
            public bool cellsOn;                   // on or off as the light cells last learned it (Switched)
            public float flipAt;                   // when it was first seen the other way (0 = not)
            public string name;                    // (for the log: a destroyed Light has none to ask)
            public bool saidOff;                   // its LampController switched it off (Announced): off whatever the Light says
            public float hotUntil;                 // read every frame until then (Announced: it is fading in)
            public bool flickers;                  // its brightness changes from read to read: read every frame (Flickers)
            public float rawWas, readAt, steadyAt; // its brightness as last read, when, and when it last changed
        }

        // A fill light is bounced (Bounce Fill Lights off) only while it is all that is lit of its lamp: the game
        // switches some lamps' real lights off (his Customs log, 2026-10-16, morning: 'Plafond_lamp_05_source_on'
        // - its MainSpot and Cookie objects inactive, only the PointGI on) and the glow the player sees around the
        // bulb is then the fill's alone; skipped, the lamp bounced nothing. Next to a lit real light it stays out
        // (that light's bounce is worked out here; the fill stands for the same light). A fill with no other
        // light beside it (a stand-in placed on its own) stays out too.
        private static bool StandsIn(Lamp k)
        {
            if (k.mates == null || k.mates.Length == 0) return false;
            foreach (Lamp m in k.mates) if (m.on && !m.gone) return false;
            return true;
        }

        // The game's lamps come in pairs or threes: the lamp's real light ('MainSpot', 'Cookie': a spot with a
        // cookie) and a shadowless point light named ...GI ('BaseGI', 'PointGI', 'GI', '..._source_GI') that
        // stands for what the lamp would bounce around the room (Customs, his lamp logs). Counted as a lamp
        // here, that stand-in is bounced a second time on top of the bounce this mod works out.
        private static readonly System.Text.RegularExpressions.Regex FillName = new System.Text.RegularExpressions.Regex("(?<![A-Z])GI(?![A-Za-z])");
        private static bool IsFill(Light l) => l.type == LightType.Point && FillName.IsMatch(l.name);

        // The spots' cookies for the shader: one slice each of a small array, copied in the first time a lamp
        // with that cookie is in use (they are compressed, single-channel or not readable: the GPU converts).
        private const int CookieSize = 64, CookieSlices = 48;
        private static Texture2DArray _cookies;
        private static readonly Dictionary<int, int> _cookieSlice = new Dictionary<int, int>();
        private static bool _cookiesFull, _cookiesFailed;
        private static readonly Vector4[] _up = new Vector4[Max];
        private static readonly int IdUp = Shader.PropertyToID("_GiLampUp"), IdCookies = Shader.PropertyToID("_GiLampCookies");

        // The slice a cookie is in, -1 = none (no room left, or the copy failed: the lamp is taken as a plain cone).
        private static int CookieSlice(Texture2D cookie)
        {
            if (cookie == null || _cookiesFailed) return -1;
            if (_cookieSlice.TryGetValue(cookie.GetInstanceID(), out int slice)) return slice;
            if (_cookies == null)
            {
                _cookies = new Texture2DArray(CookieSize, CookieSize, CookieSlices, TextureFormat.RGBA32, false, true)
                    { name = "TraceSix Lamp Cookies", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                Shader.SetGlobalTexture(IdCookies, _cookies);
            }
            slice = _cookieSlice.Count;
            if (slice >= CookieSlices)
            {
                if (!_cookiesFull) Plugin.MyLog.LogWarning("[TraceSix] Lamps: more than " + CookieSlices + " different cookies among the lamps in use; the rest are taken as plain cones.");
                _cookiesFull = true;
                return -1;
            }
            bool ok;
            try { ok = Graphics.ConvertTexture(cookie, 0, _cookies, slice); }
            catch (System.Exception) { ok = false; }
            if (!ok)
            {
                _cookiesFailed = true;
                Plugin.MyLog.LogWarning("[TraceSix] Lamps: a cookie ('" + cookie.name + "', " + cookie.graphicsFormat + ") could not be copied for the shader; spots are taken as plain cones.");
                return -1;
            }
            _cookieSlice[cookie.GetInstanceID()] = slice;
            return slice;
        }

        private static readonly List<Lamp> _all = new List<Lamp>();
        private static readonly Vector4[] _pos = new Vector4[Max];   // xyz = where, w = 1 / range^2
        private static readonly Vector4[] _col = new Vector4[Max];
        private static readonly Vector4[] _dir = new Vector4[Max];   // spot: axis, cos of half its angle; point: w = -2
        // The still lamps take the first MaxStill slots; the last MaxFlash are kept for the moving lights the ray tracing
        // lights with (CopyLamps, 0.24.81) - left out of _GiLampCount, so the light volumes never take them in.
        private const int MaxStill = Max - MaxFlash;
        private static readonly Lamp[] _best = new Lamp[Max];
        private static readonly float[] _bestKey = new float[Max];
        private static int _cursor, _count, _logged, _loggedCount = -1;

        // GiRt (Ray-Traced Lamp Shadows): the lamps in use as the shader has them, 16 floats each (_GiLampPos,
        // _GiLampDir, _GiLampCol, _GiLampUp), for the D3D12 side to pick from. Returns how many.
        // (0.24.81) After the still ones: the lit moving spots nearest the camera (a flashlight on a gun, other players'),
        // as they are this frame - written into the shader's arrays at the same places too (RTSHADE's LampAt reads the
        // lamp the D3D12 side picked by its index), but not counted in _GiLampCount.
        internal static int CopyLamps(float[] dst, Vector3 cam)
        {
            int n = Mathf.Min(_count, MaxStill);
            int m = PickMoving(cam);
            Renumber(n, m);
            for (int j = 0; j < m; j++)
            {
                Lamp k = _rtMove[j];
                int i = n + j;
                _pos[i] = new Vector4(k.pos.x, k.pos.y, k.pos.z, 1f / (k.range * k.range));
                _col[i] = new Vector4(k.col.r, k.col.g, k.col.b, 0f);
                _dir[i] = new Vector4(k.fwd.x, k.fwd.y, k.fwd.z, k.cosHalf);
                int slice = CookieSlice(k.cookie);
                _up[i] = slice < 0 ? Vector4.zero : new Vector4(k.up.x, k.up.y, k.up.z, InAlpha(k.cookie) ? slice + 1 : -(slice + 1));
            }
            if (m > 0 || _rtMoveShown > 0)
            {
                Shader.SetGlobalVectorArray(IdPos, _pos);
                Shader.SetGlobalVectorArray(IdCol, _col);
                Shader.SetGlobalVectorArray(IdDir, _dir);
                Shader.SetGlobalVectorArray(IdUp, _up);
            }
            _rtMoveShown = m;
            for (int i = 0; i < n + m; i++)
            {
                Vector4 p = _pos[i], d = _dir[i], c = _col[i], u = _up[i];
                int o = i * 16;
                dst[o] = p.x; dst[o + 1] = p.y; dst[o + 2] = p.z; dst[o + 3] = p.w;
                dst[o + 4] = d.x; dst[o + 5] = d.y; dst[o + 6] = d.z; dst[o + 7] = d.w;
                dst[o + 8] = c.x; dst[o + 9] = c.y; dst[o + 10] = c.z; dst[o + 11] = c.w;
                dst[o + 12] = u.x; dst[o + 13] = u.y; dst[o + 14] = u.z; dst[o + 15] = u.w;
            }
            if (m > _rtMoveLogged)
            {
                _rtMoveLogged = m;
                Plugin.MyLog.LogInfo("[TraceSix] Ray tracing: " + m + " moving light" + (m == 1 ? "" : "s") + " lit by the traced rays now (room for " + MaxFlash
                    + "); the nearest '" + _rtMove[0].light.name + "' " + (_rtMove[0].pos - cam).magnitude.ToString("F1") + " m from the camera.");
            }
            return n + m;
        }

        // (0.24.154) "Reuse Lamp Choices" (ReSTIR DI): last frame's list slot of each lamp -> this frame's (-1 gone). The still lamps
        // are re-picked and re-sorted every half second, the moving ones every frame, so a reservoir's lamp index from last frame
        // must be renumbered before it is reused (TraceSixProbes RTLAMPRIS, _GiLampRemap). By the lamp itself, not its place.
        private static readonly Lamp[] _slotNow = new Lamp[Max], _slotBefore = new Lamp[Max];
        internal static readonly float[] Remap = new float[Max];

        private static void Renumber(int n, int m)
        {
            for (int i = 0; i < Max; i++)
            {
                _slotBefore[i] = _slotNow[i];
                _slotNow[i] = i < n ? _best[i] : i < n + m ? _rtMove[i - n] : null;
            }
            for (int i = 0; i < Max; i++)
            {
                Remap[i] = -1f;
                Lamp k = _slotBefore[i];
                if (k == null) continue;
                for (int j = 0; j < n + m; j++)
                    if (ReferenceEquals(_slotNow[j], k)) { Remap[i] = j; break; }
            }
        }

        // (1.0.0) "Instant Light Switches": the lights that switched on or off a moment ago - a lamp's LampController (Announced)
        // or a flashlight (PickMoving) - for the ray-traced light round them (GiRt -> _GiRtSwitch): xyz = where, w = how far its
        // light reaches (its range x 1.25 + 1 m: the bounce carries a little past it). Kept for SwitchOff / SwitchOn seconds (a
        // lamp switched on fades in), four at a time (a new one takes the free or the oldest slot).
        private const float SwitchOff = 0.25f, SwitchOn = 0.5f;
        private static readonly Vector4[] _switch = new Vector4[4], _switchNow = new Vector4[4];
        private static readonly float[] _switchUntil = new float[4];
        private static void NoteSwitch(Vector3 at, float range, float keep)
        {
            int slot = 0;
            for (int i = 1; i < 4; i++) if (_switchUntil[i] < _switchUntil[slot]) slot = i;
            _switch[slot] = new Vector4(at.x, at.y, at.z, range * 1.25f + 1f);
            _switchUntil[slot] = Time.unscaledTime + keep;
        }
        internal static Vector4[] SwitchesNow(bool on)
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < 4; i++) _switchNow[i] = on && _switchUntil[i] > now ? _switch[i] : Vector4.zero;
            return _switchNow;
        }

        // The lit moving spots nearest the camera, read now (the same as Flash takes, whatever Moving Lights says).
        private static readonly Lamp[] _rtMove = new Lamp[MaxFlash];
        private static readonly float[] _rtMoveKey = new float[MaxFlash];
        private static int _rtMoveShown, _rtMoveLogged;
        private static int PickMoving(Vector3 cp)
        {
            if (!_collected) return 0;
            RegisterAgain();
            int n = 0;
            for (int i = _moving.Count - 1; i >= 0; i--)
            {
                Lamp k = _moving[i];
                Light l = k.light;
                if (l == null) { k.gone = true; k.on = false; _moving.RemoveAt(i); continue; }
                bool litNow = k.spot && l.isActiveAndEnabled && !(k.ir && l.intensity <= 0.01f);
                float key = (l.transform.position - cp).sqrMagnitude;
                // (1.0.0) "Instant Light Switches": switched on or off since the last frame, within its own reach of the camera
                if (litNow != k.rtLit)
                {
                    k.rtLit = litNow;
                    float reach = l.range * 1.25f + 1f;
                    if (key < reach * reach) NoteSwitch(l.transform.position, l.range, SwitchOff);
                }
                if (!litNow) continue;
                if (key > FlashReach * FlashReach || (n == MaxFlash && key >= _rtMoveKey[MaxFlash - 1])) continue;
                int at = n < MaxFlash ? n++ : MaxFlash - 1;
                while (at > 0 && _rtMoveKey[at - 1] > key) { _rtMoveKey[at] = _rtMoveKey[at - 1]; _rtMove[at] = _rtMove[at - 1]; at--; }
                _rtMoveKey[at] = key;
                _rtMove[at] = k;
            }
            int lit = 0;
            for (int i = 0; i < n; i++)
            {
                Lamp k = _rtMove[i];
                Read(k);
                if (k.on) _rtMove[lit++] = k;
            }
            return lit;
        }

        // Weapons registered a moment ago, looked at again (RegisterWeapon).
        private static void RegisterAgain()
        {
            for (int i = _again.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < _again[i].Value) continue;
                if (_again[i].Key != null) Register(_again[i].Key);
                _again.RemoveAt(i);
            }
        }
        private static float _pickAt, _logAt;
        private static Vector3 _pickPos;

        // The lights that move (a weapon's or a helmet's flashlight): not for the light volumes, which follow a
        // change over a minute. The lit spots among them nearest the camera are read every frame and their
        // first bounce is worked out on its own (TraceSixProbes: FlashLight).
        public const int MaxFlash = 4;             // the shader's FLASH_LIGHTS
        private const float FlashReach = 80f;      // how far from the camera such a light is still followed
        private static readonly List<Lamp> _moving = new List<Lamp>();
        private static readonly Dictionary<int, Lamp> _byLight = new Dictionary<int, Lamp>();
        private static readonly List<Light> _found = new List<Light>();
        private static readonly List<KeyValuePair<Transform, float>> _again = new List<KeyValuePair<Transform, float>>();
        private static readonly Lamp[] _flash = new Lamp[MaxFlash];
        private static readonly float[] _flashKey = new float[MaxFlash];
        private static readonly Vector4[] _fPos = new Vector4[MaxFlash], _fCol = new Vector4[MaxFlash], _fDir = new Vector4[MaxFlash], _fUp = new Vector4[MaxFlash];
        private static int _flashCount, _flashLogged = -1;
        private static bool _collected;

        // The game fades every lamp by the camera's distance to it and puts it out beyond that (CullingLightObject:
        // Light.intensity = the lamp's own x a fade, 0 past its fade distance, 50 - 80 m unless set otherwise).
        // Read here, that faded value makes a lamp's bounce grow as the player walks up to it, and whole
        // groups come on at once. What the lamp IS: its own intensity (getMaxIntensityFinal) x what its
        // switch and flicker make of it (IntensityMultiplier), lit while IsLightEnabled and its Light component
        // is enabled - whatever the distance (the fade is by intensity; it never disables the Light).
        // An infrared light stays enabled; its IkLight sets Light.intensity as each camera is about to cull, to 0
        // unless that camera's night vision is on. So for these the intensity of the moment is the switch (Moves).

        private static readonly int IdPos = Shader.PropertyToID("_GiLampPos");
        private static readonly int IdCol = Shader.PropertyToID("_GiLampCol");
        private static readonly int IdDir = Shader.PropertyToID("_GiLampDir");
        private static readonly int IdCount = Shader.PropertyToID("_GiLampCount");
        private static readonly int IdFlashPos = Shader.PropertyToID("_GiFlashPos"), IdFlashCol = Shader.PropertyToID("_GiFlashCol"),
            IdFlashDir = Shader.PropertyToID("_GiFlashDir"), IdFlashUp = Shader.PropertyToID("_GiFlashUp"), IdFlashCount = Shader.PropertyToID("_GiFlashCount");
        private static readonly int[] IdFlashCookie = { Shader.PropertyToID("_GiFlashCookie0"), Shader.PropertyToID("_GiFlashCookie1"),
            Shader.PropertyToID("_GiFlashCookie2"), Shader.PropertyToID("_GiFlashCookie3") };

        public static void Reset()
        {
            _all.Clear();
            _started = 0;
            _moving.Clear();
            _byLight.Clear();
            _again.Clear();
            System.Array.Clear(_best, 0, _best.Length);     // these keep lamps (their lights, their cookies) too
            System.Array.Clear(_flash, 0, _flash.Length);
            System.Array.Clear(_switchUntil, 0, _switchUntil.Length);
            _collected = false;
            _cookieSlice.Clear();                            // the next map's cookies are other textures
            _cookiesFull = false;
            _cursor = _count = _logged = _flashCount = _rtMoveShown = _rtMoveLogged = _wokeLogged = _flickLogged = 0;
            _hot.Clear();
            _flick.Clear();
            _loggedCount = _flashLogged = -1;
            _pickAt = _logAt = 0f;
            Shader.SetGlobalFloat(IdCount, 0f);
            Shader.SetGlobalFloat(IdFlashCount, 0f);
        }

        // (0.24.155) Where every light of the map stood as the raid loaded, on or off (GiEmission: a glowing surface
        // with one on it is that lamp's, and its light is the lamp's).
        internal static void Places(List<Vector3> into)
        {
            foreach (Lamp k in _all)
                if (k.light != null && !k.moves) into.Add(k.pos);
        }

        // Once, while the raid loads: every point and spot light of the map, also the ones the game has
        // switched off or whose building is not loaded in yet (they come on as the player gets near).
        public static void Collect()
        {
            Reset();
            int point = 0, spot = 0, on = 0, cookie = 0, shadows = 0, faders = 0, shown = 0, fills = 0, fillsOn = 0, cubes = 0, spotCookies = 0;
            var ranges = new List<float>();
            var powers = new List<float>();
            foreach (Light l in Object.FindObjectsOfType<Light>(true))
            {
                if (l == null || (l.type != LightType.Point && l.type != LightType.Spot)) continue;
                var k = new Lamp { light = l, spot = l.type == LightType.Spot, fill = IsFill(l) };
                k.fader = l.GetComponent<CullingLightObject>();
                if (k.fader != null) faders++;
                Read(k);
                if (k.fill) fills++;
                _all.Add(k);
                _byLight[l.GetInstanceID()] = k;
                if (l.isActiveAndEnabled && l.intensity > 0.01f) shown++;
                if (k.spot) spot++; else point++;
                if (l.cookie != null) cookie++;
                if (l.shadows != LightShadows.None) shadows++;
                if (!k.on) continue;
                on++;
                if (k.fill) fillsOn++;
                if (k.spot && k.cookie != null) spotCookies++;
                if (!k.spot && l.cookie != null) cubes++;
                ranges.Add(k.range);
                powers.Add(k.power);
            }
            // Each fill light's lamp: the other lights under the same parent (a lamp's PointGI beside its MainSpot
            // and Cookie). Their on / off is read again as they come round (Read, ReadPerFrame a frame).
            var byParent = new Dictionary<Transform, List<Lamp>>();
            foreach (Lamp k in _all)
            {
                Transform parent = k.light != null ? k.light.transform.parent : null;
                if (parent == null) continue;
                if (!byParent.TryGetValue(parent, out List<Lamp> g)) byParent[parent] = g = new List<Lamp>();
                g.Add(k);
            }
            int standIns = 0;
            foreach (List<Lamp> g in byParent.Values)
                foreach (Lamp k in g)
                {
                    if (!k.fill) continue;
                    var m = new List<Lamp>();
                    foreach (Lamp o in g) if (o != k && !o.fill) m.Add(o);
                    k.mates = m.ToArray();
                    if (k.on && StandsIn(k)) standIns++;
                }
            _collected = true;
            ranges.Sort();
            powers.Sort();
            Plugin.MyLog.LogInfo("[TraceSix] Lamps: " + point + " point and " + spot + " spot lights in the map, " + faders + " of them faded by distance by the game. "
                + on + " are lit (whatever the distance); the game shows " + shown + " of all lights right now (" + cookie + " with a cookie, " + shadows + " casting shadows)."
                + (on > 0 ? " Of the lit ones - range: median " + ranges[on / 2].ToString("F1") + " m, largest " + ranges[on - 1].ToString("F1")
                    + " m; intensity: median " + powers[on / 2].ToString("F2") + ", largest " + powers[on - 1].ToString("F2") + "." : "")
                + " Of the lit ones " + spotCookies + " are spots shaped by a cookie (read), " + cubes + " point lights with a cube cookie (NOT read: taken as lighting every way alike) and "
                + fillsOn + " the game's own fill lights by name (...GI; " + fills + " in the map): " + (GiConfig.Fills.Value ? "bounced like any lamp (the setting)."
                    : standIns + " of them bounced, being all that is lit of their lamp (its real light switched off by the game), the rest not.")
                + " The game works in " + QualitySettings.activeColorSpace + " colour space.");
            Plugin.MyLog.LogInfo("[TraceSix] Lamps in a fixture: for " + _started + " spots the light starts at the far side of the lamp's own fixture, not at the game's Light above it (never more than 3 m or a third of its reach along).");
        }

        private static void Read(Lamp k)
        {
            Light l = k.light;
            if (l == null)
            {
                if (k.read && k.cellsOn && !k.moves) Woke(k, "was destroyed (shot out)");
                k.gone = true;
                k.on = false;
                return;
            }
            Vector3 p = l.transform.position;
            if (k.read && !k.moves && (p - k.pos).sqrMagnitude > MovedSq) Moves(k);   // a weapon light, a vehicle: for good
            bool first = !k.read;
            k.read = true;
            if (first) k.name = l.name;
            k.pos = p;
            k.range = l.range;
            // Switched on = its Light component is enabled (disabled by a culling list is not switched off).
            // A room's light switch (ControlledLampGroup) puts its lamps out by disabling the Light and nothing
            // else: the lamp's own controller goes on saying on (IsLightEnabled) and restores its intensity.
            k.enabled = l.enabled || GiCulling.Culls(l);
            bool lit = k.enabled && !k.saidOff;
            float raw;
            if (k.fader != null)
            {
                raw = k.fader.getMaxIntensityFinal() * k.fader.IntensityMultiplier;
                lit &= k.fader.IsLightEnabled;
            }
            else raw = l.intensity;
            Flickers(k, raw, first);
            k.col = l.color * k.power;
            k.on = lit && GiCulling.Present(l.gameObject) && k.range > 0.5f && k.col.maxColorComponent > 0.01f;
            Switched(k, first);
            if (!k.spot) return;
            k.fwd = l.transform.forward;
            k.up = l.transform.up;
            if (first) k.start = StartOf(k);
            k.cookie = l.cookie as Texture2D;
            k.cosHalf = Mathf.Cos(Mathf.Clamp(l.spotAngle, 1f, 170f) * 0.5f * Mathf.Deg2Rad);
        }

        // (1.0.0) A flickering lamp: its LampController sets its brightness every frame (VisualUpdate ->
        // CullingLightObject.IntensityMultiplier / Light.intensity). Read round the map 16 a frame, it was caught at one
        // moment of its flicker every 2 - 4 s on Customs and held there: the bounce light jumped to a new brightness every
        // few seconds, standing still ("a random bright light pulses"). The bounce shows what the light did over its
        // own history (the ray-traced light's frames, the cells' redos), so a flicker is taken as its average: a lamp
        // seen changing is read every frame and its brightness eased over FlickerTime. Back to the round once it has
        // held still for 5 s (a fade-in at the power switch). A steady lamp is taken as it is.
        private const float FlickerTime = 0.5f;
        private static readonly List<Lamp> _flick = new List<Lamp>();
        private static int _flickLogged;
        private static void Flickers(Lamp k, float raw, bool first)
        {
            float now = Time.unscaledTime;
            if (first || k.moves) k.power = raw;
            else
            {
                if (Mathf.Abs(raw - k.rawWas) > 0.02f * Mathf.Max(k.rawWas, 0.05f))
                {
                    k.steadyAt = now;
                    if (!k.flickers)
                    {
                        k.flickers = true;
                        _flick.Add(k);
                        if (_flickLogged < 10 && Camera.main != null && (k.pos - Camera.main.transform.position).sqrMagnitude < 900f)
                        {
                            _flickLogged++;
                            Plugin.MyLog.LogInfo("[TraceSix] A lamp is flickering or fading: '" + k.name + "' at " + k.pos.ToString("F1")
                                + " - its light is now read every frame and eased over " + FlickerTime + " s.");
                        }
                    }
                }
                else if (k.flickers && now - k.steadyAt > 5f) k.flickers = false;   // (Tick drops it from the list)
                k.power = Mathf.Lerp(k.power, raw, 1f - Mathf.Exp(-(now - k.readAt) / FlickerTime));
            }
            k.rawWas = raw;
            k.readAt = now;
        }

        // Where a spot's light starts: at the far side of its own fixture. The game sets a lamp's Light where
        // the beam looks right and lets it shine through the fixture (these lights cast no shadows): the mall's
        // hanging lamp on Interchange ('shopping_mall_angle_lamp_lum_ceiling') has its spot at the ceiling,
        // 2.3 m ABOVE the panel and tubes one sees, pointing down. Taken at the Light, every top hanging in
        // that 2.3 m - the lamp's own panel, the props beside it - was lit from a metre away and lit the
        // ceiling: "these ceiling lights cast light the wrong way", in both light stores, gone with Lamp
        // Light off (his test). The light of a lamp that hangs under the ceiling starts under the ceiling.
        //   The fixture = the meshes under the lamp's EFT.Interactive.LampController that the spot's axis
        // passes through; its far side = their farthest corner along that axis. Never more than 3 m or a third
        // of the lamp's reach: a lamp post in one mesh with its arm would otherwise push it to the ground.
        private static readonly Vector3[] _corner = new Vector3[8];
        private static int _started;
        private static float StartOf(Lamp k)
        {
            if (k.light == null) return 0f;
            Component fixture = k.light.GetComponentInParent<EFT.Interactive.LampController>(true);
            if (fixture == null) return 0f;
            Vector3 f = k.fwd, side = Vector3.Cross(f, Mathf.Abs(f.y) < 0.9f ? Vector3.up : Vector3.right).normalized, across = Vector3.Cross(f, side);
            float far = 0f;
            foreach (MeshFilter mf in fixture.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || mf.GetComponent<MeshRenderer>() == null) continue;
                Bounds b = m.bounds;
                Matrix4x4 l2w = mf.transform.localToWorldMatrix;
                float d0 = float.MaxValue, d1 = float.MinValue, s0 = float.MaxValue, s1 = float.MinValue, a0 = float.MaxValue, a1 = float.MinValue;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 w = l2w.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((c & 1) != 0 ? 1f : -1f, (c & 2) != 0 ? 1f : -1f, (c & 4) != 0 ? 1f : -1f))) - k.pos;
                    float d = Vector3.Dot(w, f), s = Vector3.Dot(w, side), a = Vector3.Dot(w, across);
                    d0 = Mathf.Min(d0, d); d1 = Mathf.Max(d1, d);
                    s0 = Mathf.Min(s0, s); s1 = Mathf.Max(s1, s);
                    a0 = Mathf.Min(a0, a); a1 = Mathf.Max(a1, a);
                }
                // the axis passes through this mesh's box, in front of the lamp
                if (s0 <= 0f && s1 >= 0f && a0 <= 0f && a1 >= 0f && d1 > far) far = d1;
            }
            far = Mathf.Min(far, 3f, k.range / 3f);
            if (far > 0.05f) _started++;
            return far > 0.05f ? far : 0f;
        }

        // (1.0.0) A still lamp that has gone out or come on: the light cells round it are woken (GiProbes.WakeLamp), or
        // its bounce fades over minutes. Only once it has stayed so for a second: a blinking lamp wakes nothing.
        private static void Switched(Lamp k, bool first)
        {
            if (first || k.moves || k.ir) { k.cellsOn = k.on; k.flipAt = 0f; return; }
            if (k.on == k.cellsOn) { k.flipAt = 0f; return; }
            if (k.flipAt <= 0f) { k.flipAt = Time.unscaledTime; return; }
            if (Time.unscaledTime - k.flipAt < 1f) return;
            k.cellsOn = k.on;
            k.flipAt = 0f;
            Woke(k, k.on ? "came on" : "went out");
        }

        // (1.0.0) The game has switched a lamp (LampController.Switch, GiLampSwitchPatch: shot out, a light switch, the
        // power, the time of day) - told the frame it happens. Read round the map 16 a frame, the lamps took 2 - 4 s to
        // come to it (Customs: 3889 lights) and the cells went on getting its light meanwhile, where a door near the
        // camera is looked at every 0.1 s ("a door updates it instantly, why can't the light going out"). Off: out of
        // the lamps in use and its cells woken, this frame. On: it fades in over a second or so - read every frame for
        // a while, and Switched wakes its cells once it has been lit for a second.
        private static readonly List<Lamp> _hot = new List<Lamp>();
        public static void Announced(Light[] lights, bool on, string why)
        {
            if (!_collected || lights == null || !GiConfig.On || !GiConfig.Lamps.Value) return;
            foreach (Light l in lights)
            {
                if (l == null || !_byLight.TryGetValue(l.GetInstanceID(), out Lamp k) || k.gone || k.moves || k.ir) continue;
                k.saidOff = !on;
                _pickAt = 0f;                                    // the lamps in use picked again this frame
                NoteSwitch(k.pos, k.range, on ? SwitchOn : SwitchOff);   // (1.0.0, "Instant Light Switches")
                if (on)
                {
                    k.hotUntil = Time.unscaledTime + 5f;
                    if (!_hot.Contains(k)) _hot.Add(k);
                    continue;
                }
                k.on = false;
                k.flipAt = 0f;
                if (!k.cellsOn) continue;
                k.cellsOn = false;
                Woke(k, why);
            }
        }

        // (one line each, 20 a raid)
        private static int _wokeLogged;
        private static void Woke(Lamp k, string what)
        {
            string done = GiProbes.WakeLamp(k.pos, k.range);
            if (done == null || _wokeLogged >= 20) return;
            _wokeLogged++;
            Plugin.MyLog.LogInfo("[TraceSix] A lamp " + what + ": '" + k.name + "' at " + k.pos.ToString("F1") + ", reach " + k.range.ToString("F1")
                + " m - " + done + ".");
        }

        private static bool InAlpha(Texture cookie) => UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(cookie.graphicsFormat);

        private static void Moves(Lamp k)
        {
            if (k.moves) return;
            k.moves = true;
            _moving.Add(k);
            if (k.light == null) return;
            EFT.Visual.IkLight ir = k.light.GetComponentInParent<EFT.Visual.IkLight>(true);
            k.ir = ir != null && ReferenceEquals(ir.Light, k.light);
        }

        // A weapon has come into someone's hands (GiWeaponSpawnPatch): its lights move, and one a bot brings into the
        // raid was not there when the map's lights were collected. Looked at again two seconds on, in case the
        // weapon's parts were not all in place yet.
        public static void RegisterWeapon(Transform root)
        {
            if (!_collected || root == null) return;
            Register(root);
            _again.Add(new KeyValuePair<Transform, float>(root, Time.unscaledTime + 2f));
        }

        private static void Register(Transform root)
        {
            root.GetComponentsInChildren(true, _found);
            foreach (Light l in _found)
            {
                if (l == null || (l.type != LightType.Point && l.type != LightType.Spot)) continue;
                if (!_byLight.TryGetValue(l.GetInstanceID(), out Lamp k))
                {
                    k = new Lamp { light = l, spot = l.type == LightType.Spot };
                    k.fader = l.GetComponent<CullingLightObject>();
                    Read(k);
                    _all.Add(k);
                    _byLight[l.GetInstanceID()] = k;
                }
                Moves(k);
            }
            _found.Clear();
        }

        // Every frame, as the picture's camera is about to render: the lit moving spots nearest it, as they
        // are right now.
        public static void Flash(Camera cam, bool wanted)
        {
            int n = 0;
            if (wanted && GiConfig.Lamps.Value && _collected)
            {
                RegisterAgain();
                Vector3 cp = cam.transform.position;
                for (int i = _moving.Count - 1; i >= 0; i--)
                {
                    Lamp k = _moving[i];
                    Light l = k.light;
                    if (l == null) { k.gone = true; k.on = false; _moving.RemoveAt(i); continue; }
                    if (!k.spot || !l.isActiveAndEnabled || (k.ir && l.intensity <= 0.01f)) continue;
                    float key = (l.transform.position - cp).sqrMagnitude;
                    if (key > FlashReach * FlashReach || (n == MaxFlash && key >= _flashKey[MaxFlash - 1])) continue;
                    int at = n < MaxFlash ? n++ : MaxFlash - 1;
                    while (at > 0 && _flashKey[at - 1] > key) { _flashKey[at] = _flashKey[at - 1]; _flash[at] = _flash[at - 1]; at--; }
                    _flashKey[at] = key;
                    _flash[at] = k;
                }
                int lit = 0;
                for (int i = 0; i < n; i++)
                {
                    Lamp k = _flash[i];
                    if (k == null || k.light == null) continue;   // (gone since the pass above: the raid-start asset unload, 0.24.89 log)
                    Read(k);
                    if (!k.on) continue;
                    _fPos[lit] = new Vector4(k.pos.x, k.pos.y, k.pos.z, 1f / (k.range * k.range));
                    // Its cookie shapes the beam (w of the colour: 1 = read from alpha, 2 = from red, where the
                    // texture has no alpha); w of "up" = the mip at which one texel is a ray's share of it (16
                    // rays across).
                    Texture cookie = k.light.cookie;
                    Vector3 up = k.light.transform.up;
                    _fCol[lit] = new Vector4(k.col.r, k.col.g, k.col.b, cookie == null ? 0f : InAlpha(cookie) ? 1f : 2f);
                    _fDir[lit] = new Vector4(k.fwd.x, k.fwd.y, k.fwd.z, k.cosHalf);
                    _fUp[lit] = new Vector4(up.x, up.y, up.z, cookie != null ? Mathf.Max(0f, Mathf.Log(cookie.width / 16f, 2f)) : 0f);
                    Shader.SetGlobalTexture(IdFlashCookie[lit], cookie != null ? cookie : Texture2D.whiteTexture);
                    _flash[lit++] = k;
                }
                n = lit;
            }
            _flashCount = n;
            if (n > 0)
            {
                Shader.SetGlobalVectorArray(IdFlashPos, _fPos);
                Shader.SetGlobalVectorArray(IdFlashCol, _fCol);
                Shader.SetGlobalVectorArray(IdFlashDir, _fDir);
                Shader.SetGlobalVectorArray(IdFlashUp, _fUp);
            }
            Shader.SetGlobalFloat(IdFlashCount, n);
            // The first time a moving light is followed, and whenever more are than ever before in this raid.
            if (n <= _flashLogged || _flash[0] == null) return;
            _flashLogged = n;
            Plugin.MyLog.LogInfo("[TraceSix] Moving lights: " + n + " followed now (room for " + MaxFlash + "), of " + _moving.Count
                + " lights known to move; the nearest is " + (_flash[0].pos - cam.transform.position).magnitude.ToString("F1") + " m from the camera.");
        }

        // Debug: the lamps around the camera, as the game has them and as this mod takes them.
        public static void Near(Camera cam)
        {
            Vector3 eye = cam.transform.position;
            var near = new List<Lamp>();
            foreach (Lamp k in _all)
                if (!k.gone && k.light != null && (k.light.transform.position - eye).sqrMagnitude < 15f * 15f) near.Add(k);
            near.Sort((x, y) => (x.light.transform.position - eye).sqrMagnitude.CompareTo((y.light.transform.position - eye).sqrMagnitude));
            var sb = new System.Text.StringBuilder();
            sb.Append("[TraceSix] Lamps within 15 m of the camera at ").Append(eye.ToString("F1")).Append(", looking ").Append(cam.transform.forward.ToString("F2"))
              .Append(": ").Append(near.Count).Append(" (").Append(_count).Append(" lamps are in use for the indirect light right now).");
            for (int i = 0; i < near.Count && i < 14; i++)
            {
                Lamp k = near[i];
                Light l = k.light;
                Vector3 lp = l.transform.position;
                string path = l.name;
                Transform t = l.transform.parent;
                for (int up = 0; up < 3 && t != null; up++, t = t.parent) path = t.name + "/" + path;
                bool used = System.Array.IndexOf(_best, k, 0, _count) >= 0;
                string moving = System.Array.IndexOf(_flash, k, 0, _flashCount) >= 0 ? "IN USE as a moving light (its first bounce, every frame)"
                    : !k.spot ? "left out (it moves, and only moving spots are followed)"
                    : k.ir && l.intensity <= 0.01f ? "left out (infrared: there only through night vision)"
                    : !GiConfig.Flashing ? "left out (it moves, and Moving Lights is off or cannot run)"
                    : "it moves: not among the " + MaxFlash + " nearest lit ones";
                string why = k.on ? "" : !GiCulling.Present(l.gameObject) ? " (its object is not in the level right now)"
                    : !k.enabled ? " (switched off in the game: its Light component is disabled)"
                    : k.fader != null && !k.fader.IsLightEnabled ? " (its lamp is switched off or broken)"
                    : k.range <= 0.5f ? " (no reach)" : " (no intensity)";
                sb.Append("\n  ").Append((lp - eye).magnitude.ToString("F1")).Append(" m: '").Append(path).Append("' at ").Append(lp.ToString("F1"))
                  .Append(l.type == LightType.Spot ? " - SPOT, cone " + l.spotAngle.ToString("F0") + " deg, pointing " + l.transform.forward.ToString("F2") : " - POINT (the game lights every direction")
                  .Append(l.type == LightType.Spot ? "" : l.cookie != null ? ", shaped by its cookie)" : ")")
                  .Append(", range ").Append(l.range.ToString("F1")).Append(" m, intensity ").Append(l.intensity.ToString("F2")).Append(" now / ").Append(k.power.ToString("F2")).Append(" unfaded")
                  .Append(", colour ").Append(l.color.ToString("F2"))
                  .Append(l.cookie != null ? ", cookie '" + l.cookie.name + "' (" + l.cookie.GetType().Name + " " + l.cookie.width + " px, " + l.cookie.graphicsFormat
                      + (InAlpha(l.cookie) ? ", read from alpha)" : ", read from red)") : ", no cookie")
                  .Append(l.shadows != LightShadows.None ? ", casts shadows" : ", no shadows")
                  .Append(k.fader != null ? ", faded by the game with distance" : "")
                  .Append(". For the indirect light: ").Append(!k.on ? "NOT LIT" + why : k.moves ? moving
                      : k.fill && !GiConfig.Fills.Value && !StandsIn(k) ? "NOT BOUNCED: by its name the game's own fill light (Bounce Fill Lights is off)"
                        + (k.mates != null && k.mates.Length > 0 ? ", and its lamp's real light is on" : ", standing on its own")
                      : k.fill && !GiConfig.Fills.Value ? (used ? "IN USE" : "lit, not among those in use") + " - a fill light, bounced as all that is lit of its lamp"
                      : used ? "IN USE" : "lit, not among those in use")
                  .Append(k.spot ? (k.cookie != null && _cookieSlice.ContainsKey(k.cookie.GetInstanceID()) ? ", taken as a spot shaped by its cookie" : k.cookie != null ? ", taken as a spot; its cookie is read once the lamp is in use" : ", taken as a spot with the stock soft rim")
                      : l.cookie != null ? ", taken as a point light, every direction alike (its cube cookie is NOT read)" : ", taken as a point light, every direction alike").Append('.');
            }
            if (near.Count > 14) sb.Append("\n  ... ").Append(near.Count - 14).Append(" more");
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        // What lamp k sends to a surface at p facing n with nothing in the way, mean of rgb (TraceSixProbes LampAt,
        // without a spot's cookie pattern - that is read on the GPU only; its square outline is kept).
        private static float Sends(Lamp k, Vector3 p, Vector3 n)
        {
            Vector3 to = k.pos - p;
            float d2 = to.sqrMagnitude, x = d2 / (k.range * k.range);
            if (x >= 1f) return 0f;
            Vector3 l = to / Mathf.Sqrt(Mathf.Max(d2, 1e-6f));
            float ndl = Vector3.Dot(n, l);
            if (ndl <= 0f) return 0f;
            float atten = (1f - Mathf.Clamp01((x - 0.64f) / 0.36f)) / (1f + 25f * x);
            if (k.spot)
            {
                if (Vector3.Dot(-to, k.fwd) < k.start) return 0f;       // nearer than where its light starts (StartOf)
                float c = Vector3.Dot(-l, k.fwd);
                float tanHalf = Mathf.Sqrt(Mathf.Max(1f - k.cosHalf * k.cosHalf, 1e-6f)) / k.cosHalf;
                if (k.cookie != null && _cookieSlice.ContainsKey(k.cookie.GetInstanceID()))
                {
                    if (c <= 1e-3f) return 0f;
                    Vector3 right = Vector3.Cross(k.up, k.fwd).normalized;
                    float u = Vector3.Dot(-l, right) / (tanHalf * c), v = Vector3.Dot(-l, Vector3.Cross(k.fwd, right)) / (tanHalf * c);
                    if (Mathf.Abs(u) > 1f || Mathf.Abs(v) > 1f) return 0f;
                }
                else
                {
                    if (c <= k.cosHalf) return 0f;
                    atten *= Mathf.Clamp01((1f - Mathf.Sqrt(Mathf.Max(1f - c * c, 0f)) / c / tanHalf) * 4f);
                }
            }
            return (k.col.r + k.col.g + k.col.b) / 3f * atten * ndl;
        }

        // Debug, for the face read-out: the lamps in use that light a surface at p facing n - which they are,
        // where they sit from that surface, what each would send it unhindered (0 - 1000, the read-out's
        // scale) and what the game's colliders have between the two.
        public static void AtPoint(Vector3 p, Vector3 n, Transform self, System.Text.StringBuilder sb)
        {
            var list = new List<KeyValuePair<float, Lamp>>();
            float sum = 0f;
            for (int i = 0; i < _count; i++)
            {
                Lamp k = _best[i];
                if (k == null || k.light == null) continue;
                float e = Sends(k, p, n);
                if (e <= 0f) continue;
                sum += e;
                list.Add(new KeyValuePair<float, Lamp>(e, k));
            }
            sb.Append(list.Count).Append(" of the ").Append(_count).Append(" lamps in use reach it from its front");
            if (list.Count == 0) { sb.Append('.'); return; }
            sb.Append("; with nothing in the way they send ").Append(Mathf.RoundToInt(sum * 1000f)).Append(" in all (spots before their cookie's pattern).");
            list.Sort((x, y) => y.Key.CompareTo(x.Key));
            Vector3 a = p + n * 0.05f;
            for (int i = 0; i < list.Count && i < 4; i++)
            {
                Lamp k = list[i].Value;
                Light l = k.light;
                string path = l.name;
                Transform t = l.transform.parent;
                for (int up = 0; up < 3 && t != null; up++, t = t.parent) path = t.name + "/" + path;
                Vector3 to = k.pos - p;
                float best = float.MaxValue;
                string wall = null;
                foreach (RaycastHit h in Physics.RaycastAll(a, (k.pos - a).normalized, (k.pos - a).magnitude, ~0, QueryTriggerInteraction.Ignore))
                    if (h.distance < best && (self == null || !h.collider.transform.IsChildOf(self))) { best = h.distance; wall = h.collider.name; }
                sb.Append("\n      ").Append(Mathf.RoundToInt(list[i].Key * 1000f)).Append(" (").Append(Mathf.RoundToInt(100f * list[i].Key / sum)).Append("%): '").Append(path)
                  .Append("' at ").Append(k.pos.ToString("F2"))
                  .Append(k.spot ? " - SPOT, cone " + l.spotAngle.ToString("F0") + " deg, pointing " + k.fwd.ToString("F2")
                      + (k.cookie != null ? ", cookie '" + k.cookie.name + "'" : ", no cookie")
                      + (k.start > 0f ? ", its light starting " + k.start.ToString("F2") + " m out (under its own fixture)" : "") : " - POINT, every direction alike")
                  .Append(", range ").Append(k.range.ToString("F1")).Append(" m, intensity ").Append(k.power.ToString("F2")).Append(" (the game shows ").Append(l.intensity.ToString("F2"))
                  .Append(" now), colour ").Append(l.color.ToString("F2")).Append(k.fill ? ", by its name a fill light" : "")
                  .Append(". It is ").Append(to.magnitude.ToString("F2")).Append(" m from that point: ").Append(Vector3.Dot(to, n).ToString("F2"))
                  .Append(" m out the way the surface faces, ").Append((to - n * Vector3.Dot(to, n)).magnitude.ToString("F2")).Append(" m to the side")
                  .Append(to.magnitude < 1f ? " (nearer than two voxels: no test of what is between)" : "")
                  .Append("; the colliders between: ").Append(wall == null ? "nothing" : "'" + wall + "' after " + best.ToString("F2") + " m").Append('.');
            }
            if (list.Count > 4) sb.Append("\n      ... ").Append(list.Count - 4).Append(" more, weaker");
        }

        // Every frame in a raid.
        public static void Tick(Camera cam)
        {
            if (!GiConfig.On || !GiConfig.Lamps.Value || _all.Count == 0)
            {
                if (_count != 0) { _count = 0; Shader.SetGlobalFloat(IdCount, 0f); }
                _pickAt = 0f;
                return;
            }
            for (int i = _hot.Count - 1; i >= 0; i--)
            {
                Lamp k = _hot[i];
                if (k.gone || Time.unscaledTime > k.hotUntil) { _hot.RemoveAt(i); continue; }
                Read(k);
            }
            for (int i = _flick.Count - 1; i >= 0; i--)
            {
                Lamp k = _flick[i];
                if (k.gone || !k.flickers) { _flick.RemoveAt(i); continue; }
                if (k.readAt < Time.unscaledTime) Read(k);   // (not twice a frame with _hot)
            }
            for (int i = 0; i < ReadPerFrame && i < _all.Count; i++)
            {
                if (_cursor >= _all.Count) _cursor = 0;
                Lamp k = _all[_cursor++];
                if (!k.gone) Read(k);
            }
            Vector3 cp = cam.transform.position;
            if (Time.unscaledTime < _pickAt && (cp - _pickPos).sqrMagnitude < 16f) return;
            _pickAt = Time.unscaledTime + 0.5f;
            _pickPos = cp;

            // The Max lit, still ones whose reach comes nearest the camera.
            int n = 0, lit = 0, moving = 0;
            for (int i = 0; i < _all.Count; i++)
            {
                Lamp k = _all[i];
                if (k.gone || !k.on) continue;
                if (k.moves) { moving++; continue; }
                if (k.fill && !GiConfig.Fills.Value && !StandsIn(k)) continue;   // the artists' stand-in for bounce: not bounced again (StandsIn)
                lit++;
                float key = (k.pos - cp).magnitude - k.range;
                if (n == MaxStill && key >= _bestKey[MaxStill - 1]) continue;
                int at = n < MaxStill ? n++ : MaxStill - 1;
                while (at > 0 && _bestKey[at - 1] > key) { _bestKey[at] = _bestKey[at - 1]; _best[at] = _best[at - 1]; at--; }
                _bestKey[at] = key;
                _best[at] = k;
            }
            for (int i = 0; i < Max; i++)
            {
                if (i >= n) { _pos[i] = _col[i] = _up[i] = Vector4.zero; _dir[i] = new Vector4(0f, 0f, 0f, -2f); continue; }
                Lamp k = _best[i];
                _pos[i] = new Vector4(k.pos.x, k.pos.y, k.pos.z, 1f / (k.range * k.range));
                _col[i] = new Vector4(k.col.r, k.col.g, k.col.b, k.spot ? k.start : 0f);
                _dir[i] = k.spot ? new Vector4(k.fwd.x, k.fwd.y, k.fwd.z, k.cosHalf) : new Vector4(0f, 0f, 0f, -2f);
                // a spot's cookie: its slice + 1, negative where the texture keeps it in red (no alpha channel)
                int slice = k.spot ? CookieSlice(k.cookie) : -1;
                _up[i] = slice < 0 ? Vector4.zero : new Vector4(k.up.x, k.up.y, k.up.z, InAlpha(k.cookie) ? slice + 1 : -(slice + 1));
            }
            _count = n;
            Shader.SetGlobalVectorArray(IdPos, _pos);
            Shader.SetGlobalVectorArray(IdCol, _col);
            Shader.SetGlobalVectorArray(IdDir, _dir);
            Shader.SetGlobalVectorArray(IdUp, _up);
            if (_cookies != null) Shader.SetGlobalTexture(IdCookies, _cookies);
            Shader.SetGlobalFloat(IdCount, n);

            // Whenever the number in use has changed (at most every 10 s, 40 lines a raid): how many lamps there
            // are where the player is NOW. (One line 20 s into the raid said nothing about the room he tested in.)
            if (n == _loggedCount || _logged >= 40 || Time.unscaledTime < _logAt) return;
            _logAt = Time.unscaledTime + 10f;
            _loggedCount = n;
            _logged++;
            int close = 0;
            for (int i = 0; i < n; i++) if (_bestKey[i] < 30f) close++;
            Plugin.MyLog.LogInfo("[TraceSix] Lamps in use: " + n + " of the " + lit + " lit, still ones (room for " + Max + "); " + close
                + " of them reach to within 30 m of the camera" + (n > 0 ? ", the farthest ends " + Mathf.Max(0f, _bestKey[n - 1]).ToString("F0") + " m away" : "")
                + "; " + moving + " lit lights move (Moving Lights follows the nearest spots among them).");
        }
    }
}
