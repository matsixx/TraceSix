using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace TraceSix.Source
{
    // ConfigurationManager reads these by name: IsAdvanced hides a setting until "Advanced settings" is ticked,
    // Order sorts within a section (higher first), Browsable = false keeps it out of the menu.
    internal sealed class ConfigurationManagerAttributes { public bool? IsAdvanced; public int? Order; public bool? Browsable; }

    // "Show Ray Traced Light": Split = ray-traced on the left half, the voxel light on the right.
    public enum RtViewMode { Off, Full, Split, AmbientOcclusion, Albedo, AlbedoVsGame, Sources, Guided, ShadowGuide, History }
    public enum RtAoMode { Off, ScreenSpace, RayTraced }
    public enum RtSunMode { Mixed, RayTracedWorld }   // (0.24.148: the RayTracedOnly test - the game's sun shadow wiped - removed)
    // (0.24.126) Debug "Glass View": one term of the see-through glass alone (TraceSixProbes GLASS)
    public enum GlassViewMode { Off, SunShadow, IndirectLight, ProbeAmbient, Reflections, SunLight }

    internal static class GiConfig
    {
        // General
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<float> Strength;
        public static ConfigEntry<int> Reach, Layers;
        public static ConfigEntry<bool> Smooth, Between, Lamps, Moving, Scopes, VrSupport, OpenHoles, Glow;
        public static ConfigEntry<string> SkipMaps;
        // General, advanced
        public static ConfigEntry<bool> Fills;
        public static ConfigEntry<int> CellRays, RayBudget;
        public static ConfigEntry<float> GlowGap;
        // Glass
        public static ConfigEntry<bool> GlassSeeThrough;
        public static ConfigEntry<float> GlassAlpha, GlassTint, GlassFrost;
        // Voxel World (advanced: changing one reads the map again or rebuilds a volume)
        public static ConfigEntry<bool> MidVoxels, CoarseFull, ColliderLayer, CullLayer, LampFixtures;
        public static ConfigEntry<int> DrawBudget;
        // Debug
        public static ConfigEntry<bool> VoxelDebug, LightDebug, EdgeDebug, GridDebug;
        public static ConfigEntry<bool> AheadNow, OverheadNow, LampsNow, UnderNow, SpotNow, DebugLog, LampCensusNow, LightPassMark, SunPassOwn;
        public static ConfigEntry<GlassViewMode> GlassView;
        public static ConfigEntry<RtViewMode> RtView;
        public static ConfigEntry<bool> RtSurfaceNow, RtCharNow;
        // Ray Tracing (GiRt). RtTest = "Ray Tracing", RtApply = "RT Global Illumination"
        public static ConfigEntry<bool> RtTest, RtApply, RtBounce, RtTextures, RtDirectLamps, RtSunShadows, RtSunScopes, RtStereo, RtVr;
        public static ConfigEntry<RtSunMode> RtSunShadowMode;
        public static ConfigEntry<RtAoMode> RtAo;
        public static ConfigEntry<int> RtRays, RtStep;
        // Ray Tracing, advanced
        public static ConfigEntry<bool> RtHalf, RtReuse, RtSpread, RtGuide, RtDenoise, RtPreBlur, RtStabilize, RtInstantSwitch, RtRelax, RtSunSkipAway, RtShadowDn, RtLampReuse;
        public static ConfigEntry<int> RtHistory, RtReuseHistory, RtCellVisRays, RtTexSize, RtAoRays, RtVoxNew;
        public static ConfigEntry<float> RtLength, RtCover, RtSunLen, RtShadowKeep, RtReuseLimit, RtSunSoft, RtAoRadius;

        // What the mod goes by: the settings, unless SixBench's detail run (GiTest) is overriding them.
        public static bool On => Enabled.Value && !GiTest.Off;
        public static bool Smoothed => Smooth.Value;
        public static bool Quartered => Smoothed && !GiTest.PerPixel;   // (0.24.148: Fast Indirect Light, always on)
        public static bool Flashing => Quartered && Moving.Value && Lamps.Value;

        private static ConfigDescription Basic(string text, int order, AcceptableValueBase values = null) =>
            new ConfigDescription(text, values, new ConfigurationManagerAttributes { Order = order });
        private static ConfigDescription Advanced(string text, int order, AcceptableValueBase values = null) =>
            new ConfigDescription(text, values, new ConfigurationManagerAttributes { IsAdvanced = true, Order = order });
        private static ConfigDescription Hidden(string text) =>
            new ConfigDescription(text, null, new ConfigurationManagerAttributes { Browsable = false });

        // The sections, numbered so the menu lists them in this order
        private const string G = "1. General", L = "2. Glass", R = "3. Ray Tracing", W = "4. Voxel World", D = "5. Debug";

        public static void Bind(ConfigFile cfg)
        {
            // A new version starts from its own defaults (as CloudSix does): the settings file is emptied when
            // the version it was written by is not this one, so a retuned default or a renamed setting leaves
            // no old value behind. By major.minor: the third number changes with every test build.
            System.Version v = BepInEx.MetadataHelper.GetMetadata(typeof(Plugin)).Version;
            string currentVersion = v.Major + "." + v.Minor;
            var version = cfg.Bind("Internal", "ConfigVersion", "", Hidden("Do not modify"));
            if (version == null || version.Value != currentVersion)
            {
                cfg.Clear();
                System.IO.File.WriteAllText(cfg.ConfigFilePath, "");
                cfg.Reload();
                version = cfg.Bind("Internal", "ConfigVersion", currentVersion, Hidden("Do not modify"));
                version.Value = currentVersion;
                cfg.Save();
                Plugin.MyLog.LogInfo($"Config reset for version {currentVersion}");
            }
            bool save = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false;

            //--- 1. General ----------------------------------------------------------------------------------
            Enabled = cfg.Bind(G, "Enabled", true, Basic(
                "Global illumination: sunlight, skylight and lamp light bounce off surfaces and light the world around "
                + "them, in place of the game's flat ambient light. Off = the game's own lighting.", 100));
            ToggleKey = cfg.Bind(G, "Toggle Key", new KeyboardShortcut(UnityEngine.KeyCode.Insert), Basic(
                "Switches Enabled off and on, to compare the picture with and without. Switched back on, the light fades "
                + "in over about a second and a half.", 99));
            Strength = cfg.Bind(G, "Strength", 1.5f, Basic(
                "How bright the bounced light is. 1 = exactly what the game's own sun and sky give, which leaves shade and "
                + "window-lit rooms darker than an eye would see them. Higher = rooms read more as they would to someone "
                + "standing in them.", 98, new AcceptableValueRange<float>(0f, 3f)));
            Reach = cfg.Bind(G, "Detail Reach", 24, Basic(
                "How far around you the finest (half-metre) light reaches, in metres. Beyond it coarser light takes over, "
                + "which in large interiors can show as corners and walls brightening as you come up to them. 16 = closer in, "
                + "with a sharper hand-over; the light updates cost about a third less and settle sooner.", 97,
                new AcceptableValueList<int>(16, 24)));
            Layers = cfg.Bind(G, "Light Update Rate", 1, Basic(
                "How much of the light is recalculated every frame. Higher costs more; lower is cheaper, and the light then "
                + "takes longer to settle after something changes (walking into a new area, the time of day).", 96,
                new AcceptableValueList<int>(1, 2, 4, 8)));
            Lamps = cfg.Bind(G, "Lamp Light", true, Basic(
                "The map's lamps bounce their light too, and walls between a lamp and a surface block it. Off = sun and sky "
                + "only. The lamps' own direct light is the game's either way.", 95));
            Moving = cfg.Bind(G, "Moving Lights", true, Basic(
                "Flashlights bounce their light too - yours and the nearest few others that are on - following the light "
                + "every frame. Costs something only while such a light is on near you. Needs Lamp Light.", 94));
            Scopes = cfg.Bind(G, "Indirect Light In Scopes", true, Basic(
                "The scope's picture gets the bounced light too, so what you see through a scope is lit like what is around "
                + "it. Costs a second, smaller pass while you look through a scope.", 93));
            VrSupport = cfg.Bind(G, "VR Support", true, Basic(
                "Runs the mod in VR (SPT-VR): the bounced light, each eye drawn on its own - about twice the flat screen's "
                + "cost. The ray-traced light in VR also needs Ray Tracing In VR (EXPERIMENTAL), an advanced setting under "
                + "Ray Tracing. Off = the game's own lighting in VR.", 92));
            OpenHoles = cfg.Bind(G, "Open Doors And Windows", true, Basic(
                "Keeps the door and window openings the game itself marks open in the light's copy of the world, so "
                + "daylight reaches rooms whose windows are framed too finely for it. Takes effect as the area around you "
                + "is rebuilt (walk about 15 m).", 91));
            SkipMaps = cfg.Bind(G, "Maps Without Indirect Light", "laboratory,labyrinth", Basic(
                "Maps the mod stays off on, by the game's own map names, separated by commas (the log names the map at the "
                + "start of every raid). The Lab and the Labyrinth are sealed and lit by lamps alone, and look as intended "
                + "with the game's own fill light. Read when a raid starts.", 90));
            Glow = cfg.Bind(G, "Glowing Surfaces Give Light", true, Basic(
                "Glowing surfaces that have no lamp of their own - signs, screens, billboards, lit logos, Factory's daylit "
                + "windows - give light too, bounced onto what is around them, as bright as the game draws them. The glow of "
                + "a lamp's own fixture is left to that lamp's light. Switches at once; which surfaces glow is worked out "
                + "when a raid starts.", 89));
            // advanced
            Smooth = cfg.Bind(G, "Smooth Indirect Light", true, Advanced(
                "Smooths the light along each surface before it goes on the picture, hiding the blocky edges of the light "
                + "cells. Off = the cells as they are.", 60));
            Between = cfg.Bind(G, "In-Between Detail", true, Advanced(
                "A level of 1 m light cells between the 0.5 m ones around you and the 2 m ones further out, so the light "
                + "gets coarser in two small steps instead of one large one. No frame-time cost, about a third more video "
                + "memory. Off = three levels.", 59));
            CellRays = cfg.Bind(G, "Rays Per Cell", 16, Advanced(
                "How many rays a light cell sends each time it updates. 32 doubles their cost, and is a little calmer in "
                + "places lit from many directions at once.", 58, new AcceptableValueList<int>(16, 32)));
            RayBudget = cfg.Bind(G, "Light Ray Budget", 45, Advanced(
                "At most this many in a hundred of the light cells updated in a frame send their rays; the rest wait for "
                + "their next turn. Caps the cost in cluttered places, which then settle a little slower. Cells that have "
                + "just come into reach always send theirs. 100 = no cap.", 57, new AcceptableValueList<int>(100, 60, 45, 30)));
            Fills = cfg.Bind(G, "Bounce Fill Lights", true, Advanced(
                "The game's artists placed shadowless fill lights (named ...GI) beside many lamps, as a stand-in for the "
                + "light that lamp bounces. On = those are bounced as well. Off = only where one is all that is lit of its "
                + "lamp: elsewhere the mod already works out the lamp's bounce itself, and the stand-in adds to it. The game "
                + "draws them either way.", 56));
            GlowGap = cfg.Bind(G, "Glow Lamp Distance", 1f, Advanced(
                "Glowing Surfaces Give Light: a glowing surface with one of the game's lights within this many metres of "
                + "it is taken to be lit by that light already (ceiling light strips, lamp panels: the game's lights sit "
                + "on them) and gives no light of its own. 0 = every glowing surface that is not a lamp fixture. Read when "
                + "a raid starts.", 55, new AcceptableValueRange<float>(0f, 5f)));

            //--- 2. Glass ------------------------------------------------------------------------------------
            GlassSeeThrough = cfg.Bind(L, "See-Through Glass Windows", true, Basic(
                "Makes the glass-block windows the game draws as solid walls see-through: what is behind them shows "
                + "through, blurred and tinted by the glass, while the mortar between the blocks stays solid. Off = the "
                + "game's solid look.", 30));
            GlassAlpha = cfg.Bind(L, "Glass Transparency", 0.4976526f, Basic(
                "See-Through Glass Windows: how much of what is behind the glass shows through. 0 = the game's solid look, "
                + "1 = only what is behind it (its reflections stay).", 29, new AcceptableValueRange<float>(0f, 1f)));
            GlassTint = cfg.Bind(L, "Glass Tint", 1f, Basic(
                "See-Through Glass Windows: how much of the glass's own colour what is behind it (and the sunlight through "
                + "it) takes. 0 = clear glass, 1 = the full colour.", 28, new AcceptableValueRange<float>(0f, 1f)));
            GlassFrost = cfg.Bind(L, "Glass Frost", 0.4037559f, Basic(
                "See-Through Glass Windows: how blurred what is seen through the glass is. 0 = clear, 1 = strongly frosted.", 27,
                new AcceptableValueRange<float>(0f, 1f)));

            //--- 3. Ray Tracing ------------------------------------------------------------------------------
            RtTest = cfg.Bind(R, "Ray Tracing", false, Basic(
                "Hardware ray tracing against the map's real geometry (needs a GPU and driver with DirectX ray tracing "
                + "tier 1.1). When a raid starts with it on, the game pauses for a few seconds, once, while the map is read "
                + "in; that takes about 1 GB of video memory until the raid ends (switched off mid-raid, it is kept until "
                + "then). Off = the voxel light only.", 50));
            RtApply = cfg.Bind(R, "RT Global Illumination", true, Basic(
                "The bounced light comes from the rays instead of the voxel lookup: shadowing and contact shading from the "
                + "real geometry, with the voxel light cells answering for the light past the traced bounces and past Ray "
                + "Length. Off = the voxel light, while the ray-traced shadows below keep working.", 49));
            RtStep = cfg.Bind(R, "Pixel Step", 2, Basic(
                "Rays are traced from one pixel in every N x N. 1 = every pixel (about four times the cost of 2), "
                + "2 = a quarter of them, 3 or 4 = cheaper and softer.", 48, new AcceptableValueRange<int>(1, 4)));
            RtRays = cfg.Bind(R, "Rays Per Pixel", 1, Basic(
                "Rays from each traced pixel per frame. More = less grain, about the whole cost again per ray.", 47,
                new AcceptableValueRange<int>(1, 16)));
            RtBounce = cfg.Bind(R, "Traced Second Bounce", true, Basic(
                "Where a ray hits, one more ray, so the second bounce also comes from the real geometry instead of the light "
                + "cells: less light leaking through thin walls, at a noticeable cost and a little more grain.", 46));
            RtTextures = cfg.Bind(R, "Ray-Traced Textures", true, Basic(
                "Rays take the real colour of the textures they hit, and leaves, fences and grates stop rays only where they "
                + "are solid. Off = the voxels' coarser colours. About 100 MB of video memory at the default Ray-Traced "
                + "Texture Size. Takes effect at the next raid.", 45));
            RtDirectLamps = cfg.Bind(R, "Ray-Traced Direct Lamp Shadows", true, Basic(
                "The lamps' direct light is shadowed by the real geometry: no more lamp light through walls, shelves and "
                + "floors (most of the game's lamps cast no shadow of their own). Each lamp keeps the game's look.", 44));
            RtSunShadows = cfg.Bind(R, "Ray-Traced Sun Shadows", true, Basic(
                "Sun shadows traced against the map's real geometry: shadows as far as you can see, from small and thin "
                + "things too, with edges that soften with distance from what casts them.", 43));
            RtSunShadowMode = cfg.Bind(R, "Sun Shadow Mode", RtSunMode.RayTracedWorld, Basic(
                "How the ray-traced sun shadows go with the game's own. RayTracedWorld = the map's shadows come from the rays "
                + "alone, and the game still shadows what the rays do not see (you, your gun, bots, grass, swaying leaves); "
                + "the game's shadow pass also draws far less. Mixed = the darker of the two everywhere.", 42));
            RtSunScopes = cfg.Bind(R, "Ray-Traced Sun Shadows In Scopes", true, Basic(
                "The scope's picture traces its own sun shadows, so what you see through a scope is shadowed like what is "
                + "around it. Off = the scope has the game's shadows.", 41));
            RtStereo = cfg.Bind(R, "Stereo Blend In VR", true, Basic(
                "VR: each eye's ray-traced light is blended with the other eye's where both see the same surface and agree up to "
                + "their noise (Philippi, Frisvad and Jensen 2023), so the leftover grain is the same in both eyes instead of "
                + "shimmering between them. Where the eyes truly differ - a surface only one eye sees, an edge - each keeps its "
                + "own. Costs one light pass an eye. Needs Ray Tracing In VR (EXPERIMENTAL). Off = each eye's light alone.", 41));
            // (0.24.158) the ray tracing on the VR camera, opt-in: off, VR keeps the voxel light and the map is not read in
            RtVr = cfg.Bind(R, "Ray Tracing In VR (EXPERIMENTAL)", false, Advanced(
                "VR (SPT-VR, with VR Support on): the ray-traced light on the VR camera too, each eye traced on its own - "
                + "about twice the flat screen's cost, and the eyes' grain can differ (Stereo Blend In VR evens it out). "
                + "Ray-traced sun and lamp shadows, AO and reflections stay flat screen only. Off = VR keeps the voxel light, "
                + "and while you play in VR the map is not read in for the rays (no pause at the raid's start, no video memory).", 48));
            RtAo = cfg.Bind(R, "Ambient Occlusion", RtAoMode.ScreenSpace, Basic(
                "Fine contact shading on top of the ray-traced light, for what is smaller than its rays resolve (seams, the "
                + "gap under a crate, where an object meets the floor). ScreenSpace = AOSix's AO (needs AOSix), which also "
                + "covers bots and loot. RayTraced = short rays from every pixel: no screen-space halos, but only the map casts "
                + "it. Off = none.", 40));
            // advanced
            RtLength = cfg.Bind(R, "Ray Length", 50f, Advanced(
                "How far each ray looks, in metres. Past that, the light comes from the light cells. Longer costs more.", 30,
                new AcceptableValueRange<float>(1f, 200f)));
            RtHalf = cfg.Bind(R, "Half-Rate Tracing", true, Advanced(
                "Each frame only half of the traced pixels trace a new ray, in a checkerboard that swaps every frame; the "
                + "others reuse their kept samples. Halves the tracing cost for a little more grain. Needs Reuse Ray Samples.", 29));
            RtReuse = cfg.Bind(R, "Reuse Ray Samples", true, Advanced(
                "ReSTIR GI: each traced pixel keeps the best of what its rays found, and takes over better finds from its "
                + "last frame and from a neighbour (only where a ray confirms nothing is in the way). Much less grain in "
                + "rooms lit by a lamp or a doorway.", 28));
            RtReuseHistory = cfg.Bind(R, "Reuse History", 20, Advanced(
                "Reuse Ray Samples: how many frames a kept sample may stand for. More = calmer light, but a rare bright find "
                + "lingers as a blotch around it. Fewer = blotches clear faster, a little more grain.", 27,
                new AcceptableValueRange<int>(1, 32)));
            RtReuseLimit = cfg.Bind(R, "Reuse Brightness Limit", 10f, Advanced(
                "Reuse Ray Samples: a sample taken over from a neighbour is left out where it would count more than this many "
                + "times brighter or darker here (corners, where objects meet the floor). Lower = fewer bright specks there, "
                + "a little less light shared.", 26, new AcceptableValueRange<float>(1.5f, 10f)));
            RtSpread = cfg.Bind(R, "Spread Ray Samples", false, Advanced(
                "Reuse Ray Samples: neighbours' finds also go into a lasting sample of each pixel, so a small bright area (the "
                + "ground round a fire or a small lamp, seen from afar) spreads and settles instead of showing as blotches. "
                + "Off = a neighbour's find is borrowed for one frame only.", 25));
            RtGuide = cfg.Bind(R, "Guided Light Rays", false, Advanced(
                "Half of each pixel's rays aim at the brightest points it and its neighbours have found (path guiding), weighted "
                + "so the average light is unchanged. Small bright patches - the pool under a lamp - are found more often. Off by "
                + "default: in dim rooms lit through a window it can make the light come and go in blobs.", 24));
            RtDenoise = cfg.Bind(R, "Denoise", true, Advanced(
                "Keeps each traced pixel's light over the frames before and blurs it along its own surface. Off = the raw "
                + "rays of each frame (for comparison).", 23));
            RtPreBlur = cfg.Bind(R, "Pre-Blur", true, Advanced(
                "Denoise: each frame's light is blurred along its surface before it goes into the history (NVIDIA ReBLUR's "
                + "pre-pass). A lucky bright ray no longer becomes a patch that sits, drifts and jumps - the boiling in dim "
                + "rooms. Off = the history takes each frame's rays as they are.", 23));
            RtInstantSwitch = cfg.Bind(R, "Instant Light Switches", true, Advanced(
                "Denoise: when a lamp or a flashlight switches on or off near you, the bounce light round it follows within a "
                + "few frames instead of fading over about a second - where its light really changed, the history starts over, "
                + "and the kept ray samples count for little for a moment. That area is a little grainier for a quarter of a "
                + "second. Off = the history fades to the new light at its usual pace.", 23));
            RtStabilize = cfg.Bind(R, "Stabilize Output", true, Advanced(
                "Denoise: the finished light is also averaged over the last frames (NVIDIA ReBLUR's temporal stabilization), "
                + "which calms the fine shimmer the blur leaves. It may only differ from the frame's own light by about the "
                + "grain, so a real change (a door, a lamp shot out) still shows at once. Off = each frame's denoised light "
                + "as it is.", 23));
            RtVoxNew = cfg.Bind(R, "Voxel Light For New Pixels", 4, Advanced(
                "Denoise: a surface that has just come into view (turning, walking backwards, a door opening) starts from the "
                + "voxel light there, counted as this many frames, instead of from a single ray - the blotchy edges while you "
                + "turn. The ray-traced light takes over as frames come in. Higher = smoother new areas, but they show the voxel "
                + "light longer (in dim rooms it can be a little brighter or flatter). 0 = off.", 23, new AcceptableValueRange<int>(0, 16)));
            RtHistory = cfg.Bind(R, "History Frames", 32, Advanced(
                "How many frames of light a traced pixel keeps. More = smoother, but light that changes (a door opening, a "
                + "cloud over the sun) takes longer to follow.", 22, new AcceptableValueRange<int>(4, 64)));
            RtRelax = cfg.Bind(R, "Variance-Guided Denoising", false, Advanced(
                "A denoiser built like NVIDIA's ReLAX: it tracks how much each pixel's light varies and keeps a lone bright "
                + "speck from being smeared into a blotch. Off = the standard denoiser, which keeps bounce shadows with Keep "
                + "Bounce Shadows.", 21));
            RtShadowKeep = cfg.Bind(R, "Keep Bounce Shadows", 0.5f, Advanced(
                "With Variance-Guided Denoising off: how much the blur keeps the shadows bounced light casts (under a table, "
                + "behind a crate, in corners). 0 = none kept. Higher = crisper bounce shadows, a little more grain along "
                + "walls and in corners.", 20, new AcceptableValueRange<float>(0f, 1f)));
            RtCellVisRays = cfg.Bind(R, "Cell Check Ray Limit", 1, Advanced(
                "The most short rays a ray's end casts to find a light cell it can really see (which keeps light from leaking "
                + "through thin walls). Past the limit that point takes no cell light: a little darker in clutter, never a "
                + "leak. Higher = brighter in clutter, costs more there. 0 = no limit.", 19, new AcceptableValueRange<int>(0, 20)));
            RtLampReuse = cfg.Bind(R, "Reuse Lamp Choices", true, Advanced(
                "Ray-Traced Direct Lamp Shadows: ReSTIR DI. The two lamps that light a point most are always traced; for the rest, each "
                + "point keeps a record of the lamp worth tracing and takes over better ones from where it was last frame and from its "
                + "neighbours, dropping lamps that turned out blocked - so in a room full of lamps the choice settles on the ones that "
                + "really reach each point, instead of a new random one every frame. Off = one of the rest picked at random each frame.", 18));
            RtShadowDn = cfg.Bind(R, "Denoise Ray-Traced Shadows", true, Advanced(
                "The ray-traced sun and lamp shadows are filtered where they are still noisy - penumbras, spots lit by several lamps, "
                + "the light of a moving flashlight - as AMD's FidelityFX shadow denoiser does it: each pixel keeps track of how much its "
                + "shadow jumps about, the sun's keeps up to 32 frames where it is steady, and it is blurred along its own surface only "
                + "where it still jumps, so sharp shadow edges stay sharp. Off = the shadows as accumulated.", 18));
            RtSunLen = cfg.Bind(R, "Sun Ray Length", 400f, Advanced(
                "How far a ray toward the sun looks for something in the way, in metres.", 18, new AcceptableValueRange<float>(50f, 2000f)));
            RtSunSoft = cfg.Bind(R, "Sun Shadow Softness", 0.27f, Advanced(
                "The sun's size in degrees: how quickly a shadow softens away from what casts it. 0.27 = the real sun; more "
                + "= softer.", 17, new AcceptableValueRange<float>(0f, 3f)));
            RtSunSkipAway = cfg.Bind(R, "Sun Rays Skip Surfaces Facing Away", false, Advanced(
                "Surfaces facing away from the sun are put in shadow without a ray: cheaper, but leaves lit from behind go "
                + "dark and the sunny sides of trees speckle.", 16));
            RtTexSize = cfg.Bind(R, "Ray-Traced Texture Size", 128, Advanced(
                "Ray-Traced Textures: the size of each material's copy, in pixels. 128 = about 100 MB of video memory on a "
                + "big map; 64 = a quarter of that, slightly blurrier ray-traced reflections. Takes effect at the next raid.", 15,
                new AcceptableValueList<int>(32, 64, 128, 256)));
            RtCover = cfg.Bind(R, "Leaf Cover", 0.5f, Advanced(
                "With Ray-Traced Textures off: the chance that a ray hitting leaves, grates or fences is stopped. 0 = "
                + "see-through, 1 = solid.", 14, new AcceptableValueRange<float>(0f, 1f)));
            RtAoRadius = cfg.Bind(R, "Ray-Traced AO Radius", 0.3f, Advanced(
                "Ambient Occlusion = RayTraced: how far its rays look, in metres. Longer = wider shading.", 13,
                new AcceptableValueRange<float>(0.05f, 1.5f)));
            RtAoRays = cfg.Bind(R, "Ray-Traced AO Rays", 1, Advanced(
                "Ambient Occlusion = RayTraced: rays from each pixel per frame. More = less grain while moving, about the "
                + "whole cost again per ray.", 12, new AcceptableValueRange<int>(1, 8)));

            //--- 4. Voxel World (advanced): what the light is traced through ---------------------------------
            MidVoxels = cfg.Bind(W, "One-Metre Voxels", true, Advanced(
                "A copy of the world in 1 m voxels, 128 m across, between the 0.5 m one around you and the 2 m one beyond: "
                + "what lies 25 - 50 m away (the far end of a corridor, the rooms across a hall) is lit through finer voxels. "
                + "About 25 MB of video memory, and one more volume rebuilt as you move (every 13 m or so).", 40));
            CoarseFull = cfg.Bind(W, "Coarse Voxels From Full Detail", true, Advanced(
                "The coarser voxel volumes (2 m and 8 m) are built from the same full-detail models as the detailed one. "
                + "Off = from each object's simpler models where it has them, which can lack interior ceilings: a hall then "
                + "looks roofless to the light until the detailed voxels reach it. Changing it during a raid reads the map "
                + "again (the light drops out for some seconds).", 39));
            ColliderLayer = cfg.Bind(W, "Voxelise Collider-Layer Geometry", true, Advanced(
                "The voxel world also takes the visible meshes the game keeps on its collision layer - Interchange's parking "
                + "roof and floor are there. Off = left out: the garage is then open to the hall above for the light. "
                + "Changing it during a raid reads the map again.", 38));
            CullLayer = cfg.Bind(W, "Voxelise Culling-Layer Geometry", true, Advanced(
                "The voxel world also takes the visible meshes the game keeps on its culling layer - on Reserve every wall, "
                + "floor and ceiling of the underground. Off = left out: Reserve's bunkers are then open to the sky for the "
                + "light. Changing it during a raid reads the map again.", 37));
            LampFixtures = cfg.Bind(W, "Lamp Fixtures In The Voxel World", true, Advanced(
                "The lamps' own meshes (shade, panel, pole) are part of the light's copy of the world. Off = left out (the "
                + "game lets a lamp's light shine through its own fixture; kept in, a hanging lamp's panel can catch its own "
                + "light). Changing it during a raid reads the map again.", 36));
            DrawBudget = cfg.Bind(W, "Voxel Draws Per Frame", 400, Advanced(
                "How many meshes are drawn into a voxel volume per frame while it rebuilds. Higher finishes a rebuild in fewer "
                + "frames and costs more in each of them.", 35, new AcceptableValueRange<int>(50, 4000)));

            //--- 5. Debug (advanced): views and read-outs ----------------------------------------------------
            VoxelDebug = cfg.Bind(D, "Show Voxel World", false, Advanced(
                "Replaces the picture with the voxel copy of the world the light is traced through. Untinted = the fine "
                + "volume (64 m), bluish = the middle one (256 m), reddish = the coarse one (1 km).", 20));
            LightDebug = cfg.Bind(D, "Show Indirect Light Only", false, Advanced(
                "Replaces the picture with the bounced light alone, as it falls on white surfaces: no textures, no sun, no "
                + "lamps.", 19));
            RtView = cfg.Bind(D, "Show Ray Traced Light", RtViewMode.Off, Advanced(
                "With Ray Tracing on: replaces the picture with the ray-traced light alone, on white surfaces (keep Show "
                + "Indirect Light Only off with it). Split = ray-traced on the left half, the voxel light on the right. "
                + "AmbientOcclusion = the AO laid on the ray-traced light, white = none. Albedo = the colour the ray tracing "
                + "takes for each surface (faint stripes = the voxels' colour; magenta = not in the ray-traced scene: the gun, "
                + "bots, grass) - the light starts over when you leave it. AlbedoVsGame = that on the left, the game's surface "
                + "colour on the right. Sources = each pixel's own ray by where its light came from: red the sun, green the "
                + "lamps, blue the rest (light cells, second bounce). Guided = green where Guided Light Rays has something to "
                + "aim by, red where a ray was aimed. ShadowGuide = Keep Bounce Shadows' guide. History = the frames each "
                + "pixel's light holds: white = History Frames, black = none, red = under 4.", 18));
            EdgeDebug = cfg.Bind(D, "Show Edge Pixels", false, Advanced(
                "The bounced light alone, with the pixels marked that found no neighbour on their own surface in the "
                + "quarter-size lookup: green = it took a near neighbour's light, yellow = it borrowed with none near.", 17));
            GridDebug = cfg.Bind(D, "Show Which Probes Answer", false, Advanced(
                "Replaces the picture with where each pixel's light comes from: green = the 0.5 m light volume, blue-green = "
                + "the 1 m one, yellow = the 2 m one, red = the 8 m one, blue = none of them (plain sky ambient). Darker = "
                + "the surface counts as indoors.", 16));
            GlassView = cfg.Bind(D, "Glass View", GlassViewMode.Off, Advanced(
                "See-Through Glass Windows: the glass shows one part of its light alone. SunShadow = the sun's shadow on it "
                + "(white lit, black shadowed; red = no sample of a pane there, magenta = none made this frame); "
                + "IndirectLight = the bounced light on it; ProbeAmbient = the light probes' ambient; Reflections = its "
                + "reflections; SunLight = the sun's light on it. Off = the glass.", 15));
            LightPassMark = cfg.Bind(D, "Mark Own Light Pass", false, Advanced(
                "With Ray-Traced Direct Lamp Shadows on: the lamps' direct light (not the sun's) drawn in green, to see that "
                + "TraceSix's copy of the game's light pass is the one in use. Leave off.", 14));
            SunPassOwn = cfg.Bind(D, "Sun Rays Pass Their Own Object", true, Advanced(
                "A test for the ray-traced sun's flicker (a whole object black for a moment): a sun ray that meets the inside "
                + "of an object within a few centimetres of where it starts passes through it. If the flicker stops with this "
                + "on, the object's level of detail was the cause. (A single-sided wall or roof met from behind that close "
                + "then lets the sun through.)", 13));
            AheadNow = cfg.Bind(D, "Log What The Voxels Hold Ahead", false, Advanced(
                "Tick while looking at something (a window, a doorway): writes to the log what a light ray from where you "
                + "stand meets along your view in the voxel world, with the meshes there (unticks itself).", 12));
            OverheadNow = cfg.Bind(D, "Log What Is Overhead", false, Advanced(
                "Tick while aiming at a surface: writes to the log whether the voxel world has something overhead there, and "
                + "which meshes are there, in the voxel world or left out of it (unticks itself).", 11));
            LampsNow = cfg.Bind(D, "Log Lamps Near Me", false, Advanced(
                "Tick while standing near a lamp: writes to the log every lamp within 15 m - spot or point, its cone, range, "
                + "cookie, and how the bounced light takes it (unticks itself).", 10));
            LampCensusNow = cfg.Bind(D, "Log Lamp Census", false, Advanced(
                "Tick in a raid: writes to the log how many lamps the map has, how many the game draws for your view now, how "
                + "many of those render a shadow map, and the nearest shadowed ones by name (unticks itself).", 9));
            UnderNow = cfg.Bind(D, "Log Light Lookup Under Me", false, Advanced(
                "Tick while standing where the floor under you is lit wrongly (a doorway): writes to the log how the light "
                + "lookup goes for that floor, step by step (unticks itself).", 8));
            SpotNow = cfg.Bind(D, "Log Light Lookup Ahead", false, Advanced(
                "Tick while aiming at a surface some way off that is lit wrongly: writes to the log which cell each light "
                + "volume gives it and why the others are refused, what that cell holds, and which meshes around it are "
                + "missing from the voxel world (unticks itself).", 7));
            RtSurfaceNow = cfg.Bind(D, "Log Surface Ahead", false, Advanced(
                "With Ray Tracing on, tick while aiming at a surface whose ray-traced colour (Show Ray Traced Light = "
                + "AlbedoVsGame) differs from the game's: writes to the log its meshes' materials, shaders and vertex "
                + "colours, and the materials the ray tracing took for them (unticks itself).", 6));
            RtCharNow = cfg.Bind(D, "Log Character Meshes", false, Advanced(
                "With Ray Tracing on, tick in a raid: writes to the log your character's meshes and what their vertex buffers "
                + "hold, and whether this driver can share a buffer between the ray tracing and the game (unticks itself).", 5));
            DebugLog = cfg.Bind(D, "Debug Log", false, Advanced(
                "Log every voxel volume rebuild (meshes, frames, time), now and then what the light volumes hold, and every "
                + "300 frames the ray tracing's GPU time and video memory.", 4));

            Tidy(cfg);
            cfg.SaveOnConfigSet = save;
            cfg.Save();
        }

        // Settings that moved section or were renamed keep the value they had, and what no setting reads any more
        // (removed ones) is dropped from the file.
        // (0.24.148) Every section was renamed (numbered, "Ray Tracing Test" -> "3. Ray Tracing", glass and the ray-traced
        // debug views moved), keys kept except the three below; 0.24.59's moves out of "Debug" are covered the same way.
        private static readonly Dictionary<string, string> OldKey = new Dictionary<string, string>
        {
            { "Ray Tracing", "Trace Rays" }, { "RT Global Illumination", "Light The Picture" }, { "VR Support", "Voxel Light In VR" },
        };
        private static readonly string[] OldSections = { "General", "Ray Tracing Test", "Voxel World", "Debug" };

        private static void Tidy(ConfigFile cfg)
        {
            PropertyInfo pi = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (!(pi?.GetValue(cfg) is Dictionary<ConfigDefinition, string> orphans)) return;
            int moved = 0;
            foreach (ConfigDefinition d in new List<ConfigDefinition>(cfg.Keys))
            {
                if (d.Section == "Internal") continue;
                string key = OldKey.TryGetValue(d.Key, out string was) ? was : d.Key;
                foreach (string s in OldSections)
                {
                    if (s == d.Section || !orphans.TryGetValue(new ConfigDefinition(s, key), out string value)) continue;
                    try { cfg[d].SetSerializedValue(value); moved++; } catch (System.Exception) { }
                    break;
                }
            }
            if (orphans.Count == 0) return;
            Plugin.MyLog?.LogInfo("[TraceSix] Settings: " + moved + " kept their value in their new place; " + orphans.Count
                + " that nothing reads any more removed from the settings file.");
            orphans.Clear();
        }
    }
}
