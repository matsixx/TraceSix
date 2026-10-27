namespace TraceSix.Source
{
    // SixBench's "Measure TraceSix In Detail" (F:\SPT-SixBench) drives these: ways of running the mod that, each
    // read right after its reference, show what one part of it costs. TraceSix never sets them itself, and SixBench
    // reaches them by name (Steps / Set / About), so neither mod needs the other to load. Never saved.
    public static class GiTest
    {
        public static bool Off;          // the whole mod off (as Enabled off, the setting untouched)
        public static bool Freeze;       // the light volumes keep what they hold: the picture alone
        public static bool PerPixel;     // the picture's lookup for every pixel, whatever Fast Indirect Light says
        public static int Twice;         // one picture stage run a second time: 1 the lookup, 2 the smoothing, 3 the final pass
        public static int Edge = -1;     // the shader's _GiEdge: -1 as set, 2 = edge pixels get nothing
        public static bool EdgeOwn;      // edge pixels with no neighbour look up their own, whatever Edge Pixels Always Borrow says
        public static bool NoIndoor;     // no interior volumes (every place outdoors - the light is wrong indoors meanwhile)
        public static int VolUpTo;       // the update's stages left out: 1 worked out only, 2 states, 3 + marks, 4 + rays
        public static int VolTwice;      // 2 = the rays shot a second time
        public static int NoHit;         // where a ray ends: 1 nothing worked out, 2 no sun, 3 no cell read
        public static bool HitFull;      // a ray's end gets the whole lookup, whatever Ray Ends Read The Nearest Cell says
        // The ray-traced light (GiRtCamera), when it lights the picture:
        public static bool RtOff;        // not run (the voxel picture lighting comes back in its place)
        public static bool RtNoTrace;    // no trace: the D3D12 side and the hand-over left out (last frame's hits are lit again)
        public static bool RtNoShade;    // the ray ends not lit (last frame's light goes on through the denoiser)
        public static bool RtNoDenoise;  // no denoiser, whatever Denoise says
        public static bool RtNoApply;    // not put into the picture (nor is anything else)
        public static bool RtNoBounce;   // no second bounce, whatever Traced Second Bounce says
        public static int RtRays;        // rays per traced pixel instead of Rays Per Pixel (0 = as set)
        public static bool RtLate;       // traced after the game's lighting, whatever Trace During Lighting says
        public static bool RtNoVis;      // the cell a ray's end reads not checked, whatever Cell Reads Check Visibility says
        public static bool RtVisNoLimit; // (0.24.141) that check with no limit on its rays, whatever Cell Check Ray Limit says
        public static bool RtNoReuse;    // no samples taken over, whatever Reuse Ray Samples says
        // (0.24.82) each of the optimisations the other way round
        public static bool RtFullRate, RtFullCells, RtSun1000;
        public static bool RtNoTex;      // hits take the voxels' colour, whatever Ray-Traced Textures says (the atlas stays)
        public static bool RtNoSun;      // no ray-traced sun shadows, whatever Ray-Traced Sun Shadows says
        public static bool RtSunSkip;    // (0.24.140) the sun rays skip surfaces facing away, whatever the setting says
        public static bool RtNoSpread;   // (0.24.104) no spread samples, whatever Spread Ray Samples says
        public static bool RtOldDenoise; // (0.24.105) the earlier denoiser, whatever Variance-Guided Denoising says
        public static bool RtUnguided;   // (0.24.106) light rays unguided, whatever Guided Light Rays says
        public static bool RtNoShadowGuide; // (0.24.109) the earlier denoiser's blur without the bounce-shadow guide (Keep Bounce Shadows 0)
        public static bool RtEdge8, RtGuideFrames; // (0.24.110) the edge fixes each off, whatever the settings say
        public static int LightPass = -1;   // (0.24.114) 1 = TraceSix's copy of the game's light pass, 0 = the game's (-1 = as set)
        public static bool NoLampShadows;  // (0.24.114) every point / spot light's shadow map off (GiLightPass)
        public static bool RtNoDirectLamps; // (0.24.115) no ray-traced direct lamp shadows, whatever the setting says
        public static bool RtNoShadowDn;    // (0.24.151) the ray-traced shadows not denoised, whatever the setting says
        public static bool RtNoLampReuse;   // (0.24.154) the lamp choice without ReSTIR DI, whatever the setting says
        public static bool NoGlow;          // (0.24.155) glowing surfaces give no light, whatever the setting says (the glow stays built)
        public static bool RtNoStereo;      // (0.24.157) VR: no stereo blend, whatever Stereo Blend In VR says
        public static bool NearerFacing; // (0.24.111) a surface's facing from the nearer pixel of each pair, whatever Accurate Edge Facing says
        public static bool RtNoRefl;     // ReflectSix's off-screen reflection rays not traced (it falls back to the voxels)
        // (0.24.123) "See-Through Glass Windows": the game's own opaque glass; the glass without TraceSix's light lookup (the game's
        // sky ambient instead); the glass reading what is behind once (no blur)
        public static bool GlassOff, GlassNoLight, GlassOneTap;
        // (0.24.135) the glass's parts one by one: no sun shadow (GiGlassSun off: its sun unshadowed), no screen-space reflection
        // march, no copy of the picture behind it (its own look only - no see-through, no march)
        public static bool GlassNoSun, GlassNoSsr, GlassNoCopy;
        public static int RtAo = -1;     // "Ambient Occlusion" instead of the setting: 0 none, 1 AOSix's, 2 ray-traced (-1 = as set)
        public static bool Active { get; private set; }

        // label | set-up | the set-up it is read against. What the label names is (set-up) - (reference).
        private static readonly string[] Plan =
        {
            "the whole mod|as set|off",
            "the picture (the light volumes standing still)|picture only|off",
            "the light volumes' updates|as set|picture only",
            "the picture's quarter-size lookup (run a second time)|picture: lookup twice|picture only",
            "the picture's smoothing (run a second time)|picture: smoothing twice|picture only",
            "the picture's final pass (run a second time)|picture: final pass twice|picture only",
            "the edge pixels as they are (against nothing for them)|picture only|picture: edge pixels nothing",
            "edge pixels doing their own lookups instead (+ = what borrowing saves)|picture: edge pixels own|picture only",
            "every pixel looked up instead (no quarter-size lookup)|picture: every pixel|picture only",
            "the interior volumes (and the lookups that go with them)|picture only|picture: no interiors",
            "the see-through glass, all of it (against the game's opaque glass; for the glass rows look at a big window)|as set|glass: game's own",
            "TraceSix's light on the glass, its eight-cell read (against the game's sky ambient there)|as set|glass: no light lookup",
            "the glass's blur, 4 taps of the mip chain and their depth checks (against one tap)|as set|glass: one tap",
            "the glass's sun shadow and sky check (one draw of all of it at half size, two voxel-world marches; against neither)|as set|glass: no sun shadow",
            "the glass's screen-space reflection march (against none: the cells and the sky in its place)|as set|glass: no ssr",
            "the glass's copy of the picture behind it and its see-through reads (against its own look only; no march in either)|glass: no ssr|glass: no copy",
            "the update worked out and handed over, nothing run|volumes: nothing run|picture only",
            "the cells' states|volumes: states|volumes: nothing run",
            "the marks|volumes: marks|volumes: states",
            "the rays (their first run in the frame)|volumes: rays|volumes: marks",
            "lighting where the rays end|volumes: rays|volumes: rays, ends unlit",
            "the sun where the rays end|volumes: rays|volumes: rays, no sun",
            "the cell read where the rays end|volumes: rays|volumes: rays, no cell read",
            "the whole lookup at ray ends instead (+ = what the nearest cell saves)|volumes: rays, full lookup|volumes: rays",
            "the fold, the copies and the blend|as set|volumes: rays",
            "the rays shot a second time|volumes: rays twice|as set",
        };

        // When the ray-traced light lights the picture, the voxel picture's own stages do not run: their rows
        // (the lookup, the smoothing, the final pass, the edge pixels, every pixel, the interiors) give way to these.
        private static readonly string[] PlanRt =
        {
            "the ray-traced light, all of it (against the voxel picture lighting it replaces)|as set|rt: off",
            "the trace (the D3D12 side and the hand-over between the two)|as set|rt: no trace",
            "lighting where the traced rays end|as set|rt: no shading",
            "the denoiser|as set|rt: no denoiser",
            "the stereo blend between the eyes (VR only; against each eye's own light)|as set|rt: no stereo blend",
            "the ray-traced light put into the picture|as set|rt: not applied",
            "the traced second bounce|as set|rt: no second bounce",
            "a second ray per traced pixel|rt: 2 rays|as set",
            "two more rays per traced pixel (4 against 2)|rt: 4 rays|rt: 2 rays",
            "tracing after the game's lighting instead (+ = what tracing during it saves)|rt: traced after lighting|as set",
            "checking the cell each ray end reads|as set|rt: cell reads unchecked",
            "the cell check with no limit on its rays instead (+ = what Cell Check Ray Limit saves)|rt: cell check unlimited|as set",
            "reusing ray samples (ReSTIR GI)|as set|rt: no reuse",
            "tracing every pixel every frame instead (+ = what half-rate tracing saves)|rt: full rate|as set",
            "the light cells kept up as for the voxel light (+ = what lighter cells save)|rt: full cells|as set",
            "1 km sun rays instead (+ = what the shorter ones save)|rt: sun 1000|as set",
            "the ray-traced AO instead of AOSix's on the ray-traced light|rt: ao traced|rt: ao screen",
            "tracing ReflectSix's off-screen reflection rays (against its voxel march; ReflectSix's own passes not in it)|as set|rt: no reflections",
            "hits coloured and cut out by their materials' textures|as set|rt: no textures",
            "ray-traced sun shadows|as set|rt: no sun shadows",
            "sun rays skipping surfaces facing away from the sun (- = what the skip saves)|rt: sun skips facing away|as set",
            "spread ray samples (the lasting spatial samples; against borrowing a neighbour for one frame)|as set|rt: no spread",
            "the variance-guided denoiser (ReLAX's structure; against the earlier one)|as set|rt: old denoiser",
            "guided light rays (half the rays aimed by the cells' lobes)|as set|rt: unguided",
            "the bounce-shadow guide in the earlier denoiser's blur (Keep Bounce Shadows as set; against 0)|rt: old denoiser|rt: old denoiser, no shadow guide",
            "full history at edges (against 8 frames there)|as set|rt: edge history 8",
            "accurate edge facing (4 more depth reads a surface; against the nearer pixel)|as set|facing by the nearer pixel",
            "TraceSix's copy of the game's light pass (against the game's own; needs the rebuilt bundle)|light pass: own|light pass: game",
            "glowing surfaces' light (the light rays' glow reads in the voxels and at ray-traced hits; against none)|as set|glow off",
            "the game's lamp shadow maps (what ray-traced lamp shadows could replace: every lamp's shadow map off while measured)|as set|lamps: no shadow maps",
            "ray-traced direct lamp shadows (the lamps' shadow rays and their share; against none)|as set|rt: no direct lamp shadows",
            "ray-traced shadow denoiser (the sun's and the lamps' filter passes; against none)|as set|rt: no shadow denoiser",
            "lamp choice by ReSTIR DI (its reservoir passes; against the kernel's own pick)|as set|rt: no lamp reuse",
            "the guide's tolerance from its grain (against by its frames)|rt: old denoiser|rt: old denoiser, guide by frames",
        };

        private static readonly string[] VoxelPictureRows =
        {
            "picture: lookup twice", "picture: smoothing twice", "picture: final pass twice", "picture: edge pixels nothing",
            "picture: edge pixels own", "picture: every pixel", "picture: no interiors",
        };

        public static string[] Steps()
        {
            if (!GiRt.Applying) return (string[])Plan.Clone();
            var rows = new System.Collections.Generic.List<string>();
            foreach (string row in Plan)
            {
                string[] f = row.Split('|');
                if (System.Array.IndexOf(VoxelPictureRows, f[1]) >= 0 || System.Array.IndexOf(VoxelPictureRows, f[2]) >= 0) continue;
                rows.Add(row);
                if (f[1] == "as set" && f[2] == "picture only") rows.AddRange(PlanRt);   // after "the light volumes' updates"
            }
            return rows.ToArray();
        }

        public static void Set(string setUp)
        {
            Off = Freeze = PerPixel = EdgeOwn = NoIndoor = HitFull = false;
            RtOff = RtNoTrace = RtNoShade = RtNoDenoise = RtNoApply = RtNoBounce = RtLate = RtNoVis = RtNoReuse = false;
            RtFullRate = RtFullCells = RtSun1000 = false;
            RtAo = -1;
            LightPass = -1;
            NoLampShadows = false;
            RtNoDirectLamps = RtNoShadowDn = RtNoLampReuse = NoGlow = RtNoStereo = false;
            GlassOff = GlassNoLight = GlassOneTap = false;
            GlassNoSun = GlassNoSsr = GlassNoCopy = false;
            RtSunSkip = RtVisNoLimit = false;
            RtNoRefl = RtNoTex = RtNoSun = RtNoSpread = RtOldDenoise = RtUnguided = RtNoShadowGuide = RtEdge8 = RtGuideFrames = NearerFacing = false;
            Twice = VolUpTo = VolTwice = NoHit = RtRays = 0;
            Edge = -1;
            switch (setUp)
            {
                case "off": Off = true; break;
                case "picture only": Freeze = true; break;
                case "picture: lookup twice": Freeze = true; Twice = 1; break;
                case "picture: smoothing twice": Freeze = true; Twice = 2; break;
                case "picture: final pass twice": Freeze = true; Twice = 3; break;
                case "picture: edge pixels nothing": Freeze = true; Edge = 2; break;
                case "picture: edge pixels own": Freeze = true; EdgeOwn = true; break;
                case "picture: every pixel": Freeze = true; PerPixel = true; break;
                case "picture: no interiors": Freeze = true; NoIndoor = true; break;
                case "volumes: nothing run": VolUpTo = 1; break;
                case "volumes: states": VolUpTo = 2; break;
                case "volumes: marks": VolUpTo = 3; break;
                case "volumes: rays": VolUpTo = 4; break;
                case "volumes: rays, ends unlit": VolUpTo = 4; NoHit = 1; break;
                case "volumes: rays, no sun": VolUpTo = 4; NoHit = 2; break;
                case "volumes: rays, no cell read": VolUpTo = 4; NoHit = 3; break;
                case "volumes: rays, full lookup": VolUpTo = 4; HitFull = true; break;
                case "volumes: rays twice": VolTwice = 2; break;
                case "rt: off": RtOff = true; break;
                case "rt: no trace": RtNoTrace = true; break;
                case "rt: no shading": RtNoShade = true; break;
                case "rt: no denoiser": RtNoDenoise = true; break;
                case "rt: not applied": RtNoApply = true; break;
                case "rt: no second bounce": RtNoBounce = true; break;
                case "rt: 2 rays": RtRays = 2; break;
                case "rt: 4 rays": RtRays = 4; break;
                case "rt: traced after lighting": RtLate = true; break;
                case "rt: cell reads unchecked": RtNoVis = true; break;
                case "rt: cell check unlimited": RtVisNoLimit = true; break;
                case "rt: no reuse": RtNoReuse = true; break;
                case "rt: full rate": RtFullRate = true; break;
                case "rt: full cells": RtFullCells = true; break;
                case "rt: sun 1000": RtSun1000 = true; break;
                case "rt: ao screen": RtAo = 1; break;
                case "rt: ao traced": RtAo = 2; break;
                case "rt: no reflections": RtNoRefl = true; break;
                case "rt: no textures": RtNoTex = true; break;
                case "rt: no sun shadows": RtNoSun = true; break;
                case "rt: sun skips facing away": RtSunSkip = true; break;
                case "rt: no spread": RtNoSpread = true; break;
                case "rt: old denoiser": RtOldDenoise = true; break;
                case "rt: unguided": RtUnguided = true; break;
                case "rt: old denoiser, no shadow guide": RtOldDenoise = RtNoShadowGuide = true; break;
                case "rt: edge history 8": RtEdge8 = true; break;
                case "facing by the nearer pixel": NearerFacing = true; break;
                case "light pass: own": LightPass = 1; break;
                case "light pass: game": LightPass = 0; break;
                case "lamps: no shadow maps": NoLampShadows = true; break;
                case "rt: no direct lamp shadows": RtNoDirectLamps = true; break;
                case "rt: no shadow denoiser": RtNoShadowDn = true; break;
                case "rt: no lamp reuse": RtNoLampReuse = true; break;
                case "glow off": NoGlow = true; break;
                case "rt: no stereo blend": RtNoStereo = true; break;
                case "rt: old denoiser, guide by frames": RtOldDenoise = RtGuideFrames = true; break;
                case "glass: game's own": GlassOff = true; break;
                case "glass: no light lookup": GlassNoLight = true; break;
                case "glass: one tap": GlassOneTap = true; break;
                case "glass: no sun shadow": GlassNoSun = true; break;
                case "glass: no ssr": GlassNoSsr = true; break;
                case "glass: no copy": GlassNoSsr = GlassNoCopy = true; break;
            }
            Active = setUp != "as set";
            GiProbes.AskIndoor(!NoIndoor);
            GiProbes.Restart();                 // nothing half done under the last set-up is carried on
        }

        // For the run's header: what the numbers were taken with.
        public static string About()
        {
            return "detail reach " + GiConfig.Reach.Value + " m, light update rate " + GiConfig.Layers.Value + ", rays per cell " + GiConfig.CellRays.Value
                + ", lamp light " + (GiConfig.Lamps.Value ? "on" : "off")
                + ", " + GiConfig.Enabled.Value.ToString().ToLowerInvariant() + " enabled; "
                + (GiRt.Applying ? "ray-traced light lighting the picture (" + GiConfig.RtRays.Value + " ray per traced pixel, one pixel in " + GiConfig.RtStep.Value
                   + " each way, second bounce " + (GiConfig.RtBounce.Value ? "on" : "off")
                   + ", denoiser " + (GiConfig.RtDenoise.Value ? "on" : "off") + ", cell check ray limit " + GiConfig.RtCellVisRays.Value
                   + ", ray samples reused " + (GiConfig.RtReuse.Value ? "on" : "off") + ", half-rate " + (GiConfig.RtHalf.Value ? "on" : "off")
                   + ", sun rays " + GiConfig.RtSunLen.Value.ToString("F0") + " m); " : "")
                + GiProbes.BudgetSummary();
        }
    }
}
