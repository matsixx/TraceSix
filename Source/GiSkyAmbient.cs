using System.Collections.Generic;
using EFT.Weather;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // The sky light TraceSix lights with, as the SH its shaders read (_SHAr.._SHC). Every Probes material carries it
    // as material values (a material value takes precedence over a global), so the game keeps its own globals.
    //  - From CloudSix's sky when it publishes one (_SsrSkyReflMap: the sky as displayed, clouds included, discs
    //    left out here - TraceSix adds the sun and moon itself), projected to SH twice a second, at the brightness
    //    the sky is displayed at (the frame's units, as the game's lights): dusk, night and clouds light the world
    //    as the sky you see.
    //  - Else the game's ambient SH (AmbientLight.SetSH) minus the fake sunlight bounce ToDController bakes into
    //    it: a directional light from below, on the far side from the sun, coloured by the sun light's COLOUR and
    //    a curve of sun height but never by its intensity, so it stays on after TOD has turned the sun off.
    internal static class GiSkyAmbient
    {
        public static bool removeFakeSunBounce = true;   // false = the game's SH unchanged (A/B, from the next SetSH)
        public static float groundAlbedo = 0.2f;          // CloudSix's map is the upper half: below = sky light x this
        public static float discCapDegrees = 7f;          // map texels this near the sun / moon take their row's mean
        public static float skyReadInterval = 0.5f;       // s between reads of CloudSix's sky

        private const string SkyMapName = "_SsrSkyReflMap";
        private const int SkyMapMip = 3;                  // 64x32 of CloudSix's 512x256 (sky light needs no detail)
        private const float StaleSeconds = 3f;            // no new read of CloudSix's sky for this long = the game's

        // Order = AmbientLight.SetSH's: A rgb, B rgb, C.
        private static readonly int[] ShIds =
        {
            Shader.PropertyToID("_SHAr"), Shader.PropertyToID("_SHAg"), Shader.PropertyToID("_SHAb"),
            Shader.PropertyToID("_SHBr"), Shader.PropertyToID("_SHBg"), Shader.PropertyToID("_SHBb"),
            Shader.PropertyToID("_SHC"),
        };
        private static readonly Vector4[] gameVectors = new Vector4[7];     // the game's SH minus the fake bounce
        private static readonly Vector4[] skyVectors = new Vector4[7];      // what the materials carry
        private static readonly Vector4[] bounceVectors = new Vector4[7];
        private static readonly Vector4[] scratchVectors = new Vector4[7];
        private static bool haveGame, haveSky, loggedBounce;
        private static readonly List<Material> probesMaterials = new List<Material>();

        // ToDController.Update completes LAST frame's SH job and hands its result to AmbientLight.SetSH, then schedules
        // this frame's job: the bounce rebuilt from the inputs it schedules with belongs to the SH set next frame.
        private static SphericalHarmonicsL2 scheduledBounce;
        private static float scheduledStrength;
        private static bool haveScheduled, insideTimeOfDayUpdate;

        // CloudSix's sky, projected (linear)
        private static SphericalHarmonicsL2 cloudSixSh;
        private static bool haveCloudSix, readPending;
        private static float cloudSixReadTime = -100f, lastRequestTime = -100f;
        private static Vector3 requestSunDirection, requestMoonDirection;
        private static string lastSource = "";

        // The brightest channel of the bounce taken out of the game's sky light (0 = none), for the log.
        internal static float BounceRemoved { get; private set; }
        internal static bool FromCloudSix { get; private set; }

        internal static Material NewProbesMaterial()
        {
            var material = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
            probesMaterials.Add(material);
            if (haveSky) Apply(material);
            return material;
        }

        // A packed vector of the sky light TraceSix uses (index in ShIds' order); the game's global until a SetSH is seen.
        internal static Vector4 Packed(int index) => haveSky ? skyVectors[index] : Shader.GetGlobalVector(ShIds[index]);

        // The log's words for where the sky light comes from.
        internal static string Describe()
        {
            if (!FromCloudSix) return "the game's ambient" + (lastSource.Length > 0 ? " (" + lastSource + ")" : "");
            Vector3 game = UpValue(gameVectors);
            Pack(cloudSixSh, scratchVectors);
            Vector3 ours = UpValue(scratchVectors);
            return "CloudSix's sky as displayed: (" + Gamma(ours.x).ToString("F3") + ", " + Gamma(ours.y).ToString("F3") + ", " + Gamma(ours.z).ToString("F3")
                + ") straight up; the game's ambient would give (" + Gamma(game.x).ToString("F3") + ", " + Gamma(game.y).ToString("F3") + ", " + Gamma(game.z).ToString("F3") + ")";
        }

        internal static void TimeOfDayUpdateStarting() => insideTimeOfDayUpdate = true;

        internal static void TimeOfDayUpdateEnded() => insideTimeOfDayUpdate = false;

        //--- the game's sky ------------------------------------------------------------------------------

        // After ToDController.Update scheduled its job: the bounce that job adds, from the same inputs and the same steps
        // (ToDController.ComputeSphericalHarmonicsContainerJob.CalculateSphericalHarmonics).
        internal static void RebuildScheduledBounce(ToDController timeOfDay)
        {
            haveScheduled = false;
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            Light sun = sky != null && sky.Components != null ? sky.Components.LightSource : null;
            if (timeOfDay == null || sun == null) return;
            float sunHeight = sky.SunDirection.y;
            float fogginess = sky.Atmosphere.Fogginess;
            float cloudiness = WeatherController.Instance != null ? -WeatherController.Instance.WeatherCurve.Cloudiness : 1f;
            float floorLight = Mathf.Lerp(0.003f, 0.25f, Mathf.InverseLerp(0.07f, 1f, sunHeight));
            Color color = ToDController.ContrastColor(sun.color, timeOfDay.AmbientContrast.Evaluate(sunHeight));
            color = ToDController.ContrastColor2(floorLight, color, Mathf.Lerp(1f, 0.3f, fogginess));
            color = ToDController.SaturateColor(color, timeOfDay.AmbientSaturation.Evaluate(sunHeight) * (1f - fogginess));
            color *= timeOfDay.AmbientBrightness.Evaluate(sunHeight);
            color = ToDController.LimitColor(color);
            float intensity = timeOfDay.BounceHarmonicIntensity.Evaluate(sunHeight) * Mathf.Max(1f - (cloudiness + fogginess) * 1.3f, 0f);
            float share = 1f - timeOfDay.HarmonicsDaylight;   // the job blends its whole SH toward EnvironmentColor by this
            var bounce = new SphericalHarmonicsL2();
            SphericalHarmonicsL2JobExtensions.AddDirLightMT(ref bounce, (-sky.SunDirection + Vector3.down).normalized, color, intensity);
            scheduledBounce = bounce * share;
            scheduledStrength = color.maxColorComponent * intensity * share;
            haveScheduled = true;
        }

        // AmbientLight.SetSH: the SH the game lights with from now on, packed as its globals hold it, minus the bounce
        // in it when it came from ToDController (the simple sky of other maps builds its own: left as it is).
        internal static void ShSet(SphericalHarmonicsL2 sh)
        {
            Pack(sh, gameVectors);
            bool remove = removeFakeSunBounce && insideTimeOfDayUpdate && haveScheduled;
            if (remove)
            {
                Pack(scheduledBounce, bounceVectors);
                for (int i = 0; i < gameVectors.Length; i++) gameVectors[i] -= bounceVectors[i];
            }
            gameVectors[6].w = 1f;
            BounceRemoved = remove ? scheduledStrength : 0f;
            haveGame = true;
            if (remove && !loggedBounce)
            {
                loggedBounce = true;
                Plugin.LogDetail("[TraceSix] Sky light: the game's fake sun bounce is taken out of the game's ambient where TraceSix uses it (now "
                    + scheduledStrength.ToString("F3") + " at its brightest channel); the game's own ambient keeps it.");
            }
            CollectProjection();
            RequestCloudSixSky();
            Publish();
            UpdateCloudShadowGate();
        }

        //--- CloudSix's cloud shadow on the sun -----------------------------------------------------------

        // CloudSix publishes its cloud shadow (cookie + lookup) and stamps each redraw's frame; the Probes shader's
        // CloudShadow() darkens TraceSix's sunlight by it while _GiCloudShadowOn is 1 - only while the copy is fresh, so
        // a CloudSix switched off mid-raid leaves no frozen shadow behind.
        private const int CloudShadowStaleFrames = 60;    // CloudSix redraws every few frames
        private static readonly int IdCloudShadowOn = Shader.PropertyToID("_GiCloudShadowOn"),
            IdCloudSixShadowFrame = Shader.PropertyToID("_CloudSixShadowFrame"), IdCloudSixShadowOrigin = Shader.PropertyToID("_CloudSixShadowOrigin"),
            IdSunCookie = Shader.PropertyToID("_GiSunCookie"), IdSunCookieU = Shader.PropertyToID("_GiSunCookieU"), IdSunCookieV = Shader.PropertyToID("_GiSunCookieV");
        private static int cloudShadowLogged = 0;       // 0 none yet, 1 bundle lacks it, 2 CloudSix's, 3 none any more, 4 the sun's cookie, 5 bundle lacks that

        // The game's own clouds: their shadow is the sun light's cookie (EFT.Rendering.Clouds.CloudController sets
        // Light.cookie / cookieSize every frame; tiled, Repeat). Internal-DeferredShadingEFT reads its alpha at
        // (unity_WorldToLight p).xy; that matrix for a directional cookie = centred on the light (+ sunCookieCentre),
        // 1 / cookieSize a metre along the light's right and up. CloudSix's own copy is used while it publishes one.
        public static bool sunCookieShadow = true;
        public static float sunCookieCentre = 0.5f;

        private static void UpdateCloudShadowGate()
        {
            float frame = Shader.GetGlobalFloat(IdCloudSixShadowFrame);
            bool fresh = frame > 0f && Time.frameCount - frame <= CloudShadowStaleFrames && Shader.GetGlobalVector(IdCloudSixShadowOrigin).w > 0f;
            Light sun = GiProbes.Sun;
            Texture cookie = !fresh && sunCookieShadow && sun != null && sun.type == LightType.Directional && sun.isActiveAndEnabled ? sun.cookie : null;
            bool cookieShader = GiAssets.Probes != null && GiAssets.Probes.FindPropertyIndex("_GiCloudShadowV2") >= 0;
            if (cookie != null && cookieShader)
            {
                float metres = 1f / Mathf.Max(sun.cookieSize, 1e-3f);
                Matrix4x4 worldToLight = sun.transform.worldToLocalMatrix;
                Vector4 rowU = worldToLight.GetRow(0) * metres, rowV = worldToLight.GetRow(1) * metres;
                rowU.w += sunCookieCentre;
                rowV.w += sunCookieCentre;
                Shader.SetGlobalTexture(IdSunCookie, cookie);
                Shader.SetGlobalVector(IdSunCookieU, rowU);
                Shader.SetGlobalVector(IdSunCookieV, rowV);
            }
            Shader.SetGlobalFloat(IdCloudShadowOn, fresh ? 1f : cookie != null && cookieShader ? 2f : 0f);
            int state = fresh ? (GiAssets.Probes != null && GiAssets.Probes.FindPropertyIndex("_GiCloudShadowV1") >= 0 ? 2 : 1)
                : cookie != null ? (cookieShader ? 4 : 5)
                : (cloudShadowLogged >= 1 ? 3 : 0);
            if (state == cloudShadowLogged || state == 0) return;
            cloudShadowLogged = state;
            if (state == 1 || state == 5)
                Plugin.MyLog.LogWarning(state == 1 ? "[TraceSix] Cloud shadow: CloudSix publishes one, but this TraceSix bundle predates it (no _GiCloudShadowV1) - rebuild the bundle."
                    : "[TraceSix] Cloud shadow: the sun light has a cookie (the game's clouds), but this TraceSix bundle predates reading it (no _GiCloudShadowV2) - rebuild the bundle.");
            else
                Plugin.LogDetail(state == 2 ? "[TraceSix] Cloud shadow: CloudSix's cloud shadow darkens TraceSix's sunlight, as the game's light pass does."
                    : state == 4 ? "[TraceSix] Cloud shadow: the game's clouds' shadow (the sun light's cookie '" + cookie.name + "', " + sun.cookieSize.ToString("F0")
                        + " m tiles) darkens TraceSix's sunlight, as the game's light pass does."
                    : "[TraceSix] Cloud shadow: no cloud shadow on the sun any more (CloudSix stopped publishing, or the sun light has no cookie) - TraceSix's sunlight goes without it.");
        }

        //--- CloudSix's sky ------------------------------------------------------------------------------

        private static void RequestCloudSixSky()
        {
            float now = Time.unscaledTime;
            if (!GiConfig.SkyFromCloudSix.Value || readPending || now - lastRequestTime < skyReadInterval) return;
            var map = Shader.GetGlobalTexture(SkyMapName) as RenderTexture;
            if (map == null || !map.IsCreated() || !SystemInfo.supportsAsyncGPUReadback) return;
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            if (sky == null) return;
            lastRequestTime = now;
            readPending = true;
            requestSunDirection = sky.LocalSunDirection;     // the frame CloudSix draws its sky (and discs) in
            requestMoonDirection = sky.LocalMoonDirection;
            AsyncGPUReadback.Request(map, Mathf.Min(SkyMapMip, map.mipmapCount - 1), OnCloudSixSky);   // ARGBHalf as it is
        }

        // A read is turned into sky light on a worker thread (pure maths: no Unity API there); the main thread only
        // copies the 16 KB out and picks the result up at the next SetSH.
        private sealed class SkyProjection
        {
            public ushort[] texels;
            public int width, height;
            public Vector3 sunDirection, moonDirection;
            public float discCos, groundAlbedo;
            public SphericalHarmonicsL2 result;
            public double milliseconds;
        }
        private static System.Threading.Tasks.Task<SkyProjection> projecting;
        private static ushort[] texelCopy = new ushort[0];   // reused: readPending holds new reads off until it is collected
        private static int projectionsTimed;
        private static double projectionMillisecondsTotal, copyMillisecondsTotal;
        private static bool loggedFault;

        private static void OnCloudSixSky(AsyncGPUReadbackRequest request)
        {
            if (request.hasError || request.width < 4 || request.height < 2) { readPending = false; return; }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            NativeArray<ushort> data = request.GetData<ushort>();
            if (texelCopy.Length != data.Length) texelCopy = new ushort[data.Length];
            data.CopyTo(texelCopy);
            var job = new SkyProjection
            {
                texels = texelCopy, width = request.width, height = request.height,
                sunDirection = requestSunDirection, moonDirection = requestMoonDirection,
                discCos = Mathf.Cos(discCapDegrees * Mathf.Deg2Rad), groundAlbedo = groundAlbedo,
            };
            copyMillisecondsTotal += timer.Elapsed.TotalMilliseconds;
            projecting = System.Threading.Tasks.Task.Run(() => ProjectSky(job));
        }

        // Main thread, from SetSH: a finished projection becomes the sky light.
        private static void CollectProjection()
        {
            if (projecting == null || !projecting.IsCompleted) return;
            var task = projecting;
            projecting = null;
            readPending = false;
            if (task.IsFaulted || task.IsCanceled)
            {
                if (!loggedFault)
                {
                    loggedFault = true;
                    Plugin.MyLog.LogWarning("[TraceSix] Sky light: CloudSix's sky could not be read (" + task.Exception?.GetBaseException().Message + ").");
                }
                return;
            }
            SkyProjection done = task.Result;
            cloudSixSh = done.result;
            haveCloudSix = true;
            cloudSixReadTime = Time.unscaledTime;
            if (projectionsTimed < 11)
            {
                projectionsTimed++;
                if (projectionsTimed > 1) projectionMillisecondsTotal += done.milliseconds;   // the first one compiles the code
                if (projectionsTimed == 11)
                    Plugin.LogDetail("[TraceSix] Sky light: CloudSix's sky (" + done.width + "x" + done.height + ") turned into sky light every "
                        + skyReadInterval.ToString("0.##") + " s: " + (projectionMillisecondsTotal / 10.0).ToString("F2") + " ms on a worker thread, "
                        + (copyMillisecondsTotal / 11.0).ToString("F3") + " ms of main thread for the copy (averages).");
            }
        }

        // CloudSix's map -> SH in Unity's normalised form (a uniform sky L gives L for every facing), linear light.
        // Map layout (CloudSix's contract): u = atan2(x, z) / 2pi + 0.5, v = sqrt(height), rows from the horizon up.
        // Solid angle of a texel = dAzimuth x dHeight. Texels near the sun / moon take their row's mean. Below the
        // horizon: a ground lit by this sky (groundAlbedo). Runs on a worker thread: no Unity API.
        private static readonly float[] BasisScale = { 0.282095f, 0.488603f, 0.488603f, 0.488603f, 1.092548f, 1.092548f, 0.315392f, 1.092548f, 0.546274f };
        private static readonly float[] CosineScale = { 1f, 2f / 3f, 2f / 3f, 2f / 3f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f };

        private static SkyProjection ProjectSky(SkyProjection job)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int width = job.width, height = job.height;
            var projected = new Vector3[9];
            var columnSin = new float[width];
            var columnCos = new float[width];
            for (int column = 0; column < width; column++)
            {
                double azimuth = ((column + 0.5) / width - 0.5) * 2.0 * System.Math.PI;
                columnSin[column] = (float)System.Math.Sin(azimuth);
                columnCos[column] = (float)System.Math.Cos(azimuth);
            }
            var rowRadiance = new Vector3[width];
            var rowNearDisc = new bool[width];
            float azimuthStep = 2f * (float)System.Math.PI / width;
            Vector3 upIrradiance = Vector3.zero;   // cosine-weighted, / pi below
            for (int row = 0; row < height; row++)
            {
                float rowV = (row + 0.5f) / height, low = (float)row / height, high = (float)(row + 1) / height;
                float up = rowV * rowV, heightStep = high * high - low * low;
                float across = (float)System.Math.Sqrt(System.Math.Max(0.0, 1.0 - up * up));
                Vector3 rowSum = Vector3.zero;
                int rowCount = 0;
                for (int column = 0; column < width; column++)
                {
                    Vector3 direction = new Vector3(across * columnSin[column], up, across * columnCos[column]);
                    rowNearDisc[column] = Vector3.Dot(direction, job.sunDirection) > job.discCos || Vector3.Dot(direction, job.moonDirection) > job.discCos;
                    int texel = (row * width + column) * 4;
                    rowRadiance[column] = new Vector3(ToLinear(HalfToFloat(job.texels[texel])), ToLinear(HalfToFloat(job.texels[texel + 1])), ToLinear(HalfToFloat(job.texels[texel + 2])));
                    if (rowNearDisc[column]) continue;
                    rowSum += rowRadiance[column];
                    rowCount++;
                }
                Vector3 rowMean = rowCount > 0 ? rowSum / rowCount : Vector3.zero;
                float weight = heightStep * azimuthStep;
                for (int column = 0; column < width; column++)
                {
                    Vector3 d = new Vector3(across * columnSin[column], up, across * columnCos[column]);
                    Vector3 radiance = (rowNearDisc[column] ? rowMean : rowRadiance[column]) * weight;
                    // Unity's normalised basis in its packing's order: 1, y, z, x, xy, yz, 3z^2 - 1, xz, x^2 - y^2
                    projected[0] += radiance * BasisScale[0];
                    projected[1] += radiance * (BasisScale[1] * d.y);
                    projected[2] += radiance * (BasisScale[2] * d.z);
                    projected[3] += radiance * (BasisScale[3] * d.x);
                    projected[4] += radiance * (BasisScale[4] * d.x * d.y);
                    projected[5] += radiance * (BasisScale[5] * d.y * d.z);
                    projected[6] += radiance * (BasisScale[6] * (3f * d.z * d.z - 1f));
                    projected[7] += radiance * (BasisScale[7] * d.x * d.z);
                    projected[8] += radiance * (BasisScale[8] * (d.x * d.x - d.y * d.y));
                    upIrradiance += radiance * up;
                }
            }
            // ground: constant radiance over the lower half - of the basis only the constant and the y term integrate
            Vector3 ground = upIrradiance / (float)System.Math.PI * job.groundAlbedo;
            projected[0] += ground * (BasisScale[0] * 2f * (float)System.Math.PI);
            projected[1] += ground * (BasisScale[1] * -(float)System.Math.PI);
            var sh = new SphericalHarmonicsL2();
            for (int k = 0; k < 9; k++)
            {
                Vector3 value = projected[k] * (CosineScale[k] * BasisScale[k]);
                sh[0, k] = value.x; sh[1, k] = value.y; sh[2, k] = value.z;
            }
            job.result = sh;
            job.milliseconds = timer.Elapsed.TotalMilliseconds;
            return job;
        }

        // IEEE half -> float (Mathf.HalfToFloat is native: not for a worker thread).
        private static float HalfToFloat(ushort half)
        {
            int sign = (half >> 15) & 1, exponent = (half >> 10) & 0x1F, mantissa = half & 0x3FF;
            float value;
            if (exponent == 0) value = mantissa * (1f / 16777216f);
            else if (exponent == 31) return 0f;   // inf / NaN: no light
            else value = System.BitConverter.Int32BitsToSingle(((exponent - 15 + 127) << 23) | (mantissa << 13));
            return sign != 0 ? -value : value;
        }

        // The map holds the sky as the frame shows it (gamma-space game): sRGB back to linear light, as the SH holds it.
        private static float ToLinear(float value) =>
            value <= 0f ? 0f : value <= 0.04045f ? value / 12.92f : (float)System.Math.Pow((value + 0.055) / 1.055, 2.4);

        //--- out ----------------------------------------------------------------------------------------

        private static void Publish()
        {
            string source;
            bool cloudSix = false;
            if (!GiConfig.SkyFromCloudSix.Value) source = "Sky Light From CloudSix is off";
            else if (Shader.GetGlobalTexture(SkyMapName) == null) source = "CloudSix publishes no sky: needs its SkyDome mode, custom sky and Publish Sky For Reflections";
            else if (!haveCloudSix || Time.unscaledTime - cloudSixReadTime > StaleSeconds) source = "";   // reading it: not worth a line
            else { source = "CloudSix"; cloudSix = true; }
            if (cloudSix)
            {
                Pack(cloudSixSh, skyVectors);
                skyVectors[6].w = 1f;
            }
            else if (haveGame)
                for (int i = 0; i < skyVectors.Length; i++) skyVectors[i] = gameVectors[i];
            else return;
            FromCloudSix = cloudSix;
            haveSky = true;
            if (source.Length > 0 && source != lastSource)
            {
                lastSource = cloudSix ? "CloudSix" : source;
                Plugin.LogDetail("[TraceSix] Sky light: from " + (cloudSix ? "CloudSix's sky as you see it" : "the game's ambient (" + source + ")") + ".");
            }
            for (int i = probesMaterials.Count - 1; i >= 0; i--)
            {
                if (probesMaterials[i] == null) { probesMaterials.RemoveAt(i); continue; }
                Apply(probesMaterials[i]);
            }
        }

        private static void Apply(Material material)
        {
            for (int i = 0; i < ShIds.Length; i++) material.SetVector(ShIds[i], skyVectors[i]);
        }

        // AmbientLight.SetSH's packing (linear in the SH, so a bounce packs into what it adds to each vector); C's w left 0.
        private static void Pack(SphericalHarmonicsL2 sh, Vector4[] packed)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                packed[channel] = new Vector4(sh[channel, 3], sh[channel, 1], sh[channel, 2], sh[channel, 0] - sh[channel, 6]);
                packed[3 + channel] = new Vector4(sh[channel, 4], sh[channel, 5], sh[channel, 6] * 3f, sh[channel, 7]);
            }
            packed[6] = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 0f);
        }

        // Packed SH at a surface facing straight up, linear (A.y + A.w - C).
        private static Vector3 UpValue(Vector4[] packed) =>
            new Vector3(packed[0].y + packed[0].w - packed[6].x, packed[1].y + packed[1].w - packed[6].y, packed[2].y + packed[2].w - packed[6].z);


        private static float Gamma(float linear) => Mathf.Max(1.055f * Mathf.Pow(Mathf.Max(linear, 0f), 0.416667f) - 0.055f, 0f);
    }
}
