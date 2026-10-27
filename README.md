# TraceSix

Global illumination for Single Player Tarkov. TraceSix replaces Tarkov's flat ambient light with light that
actually bounces: sunlight, skylight and lamp light bounce off surfaces and light the rooms and streets around
them. With a GPU that supports it, it can also ray trace that light and the shadows against the map's real
geometry. Works on flatscreen and in VR. Ray tracing in VR is experimental and off by default and only supports GI.

Support my work on Ko-fi: https://ko-fi.com/matsix

## Features

- Bounced light from the sun, the sky and the map's lamps, in place of the game's flat ambient
- Flashlights and weapon lights bounce their light too, yours and other players'
- Glowing surfaces (signs, screens, billboards, lit logos) light what is around them
- Doors and windows the game marks open let daylight into the rooms behind them
- Scopes show the same lighting as the world around them
- See-through glass-block windows: what is behind them shows through, blurred and tinted
- Optional hardware ray tracing: bounced light, lamp shadows and sun shadows traced against the real geometry, plus ray-traced ambient occlusion
- The ray-traced light is denoised the way NVIDIA's ReBLUR does it (a blur before and after the history, a stabilized result), areas that just came into view start from the voxel light instead of a single ray, and a lamp or flashlight switching on or off shows in the bounced light within a few frames
- Off-screen reflection rays for ReflectSix
- Compatible with VR

## Requirements

- SPT 4.1.
- Ray tracing (optional): a GPU and driver with DirectX ray tracing tier 1.1, about 1 GB of free video memory. Tested on an AMD RX 9070 XT; players report it working on an AMD RX 7900 XTX and on NVIDIA cards down to an RTX 3080 10 GB.

## Install

1. Extract the release into your SPT folder. You should end up with:
   ```
   BepInEx/plugins/TraceSix/TraceSix.dll
   BepInEx/plugins/TraceSix/Assets/tracesix
   BepInEx/plugins/TraceSix/native/TraceSixRT.dll
   ```
2. Launch the game.

Press **Insert** in a raid to switch the bounced light off and on and compare.

## Settings

Open the in-game configuration manager (F12) or edit `BepInEx/config/com.matsix.tracesix.cfg`.
These are the main ones. The Advanced view has many more.

| Setting | Default | What it does |
|---|---|---|
| Enabled | On | Off restores the game's own lighting. |
| Toggle Key | Insert | Switches Enabled off and on to compare. |
| Strength | 1.5 | How bright the bounced light is. 1 = exactly what the game's sun and sky give. |
| Detail Reach | 24 | How far around you the finest light reaches, in metres (16 = cheaper). |
| Light Update Rate | 1 | How much of the light is recalculated every frame. Lower is cheaper but settles slower. |
| Lamp Light | On | The map's lamps bounce their light too. |
| Moving Lights | On | Flashlights bounce their light too. |
| Indirect Light In Scopes | On | Scopes get the bounced light. |
| VR Support | On | Runs the mod in VR. |
| Maps Without Indirect Light | laboratory,labyrinth | Maps the mod stays off on. The Lab and the Labyrinth are lit by lamps alone and look as intended with the game's own light. |
| Glowing Surfaces Give Light | On | Signs, screens and other glowing surfaces light what is around them. |
| See-Through Glass Windows | On | Glass-block windows show what is behind them. |
| Ray Tracing | Off | Hardware ray tracing. The game pauses for a few seconds once, when a raid starts, while the map is read in. |
| RT Global Illumination | On | The bounced light comes from the rays instead of the voxel lookup. Needs Ray Tracing. |
| Ray-Traced Direct Lamp Shadows | On | Lamp light is shadowed by the real geometry, so it no longer goes through walls and floors. Needs Ray Tracing. |
| Ray-Traced Sun Shadows | On | Sun shadows traced as far as you can see, from small and thin things too. Needs Ray Tracing. |
| Ambient Occlusion | ScreenSpace | Contact shading on top of the ray-traced light. ScreenSpace uses AOSix. |
| Ray Tracing In VR (EXPERIMENTAL) | Off | Advanced. Ray-traced bounced light in VR too. |

The Advanced view of Ray Tracing also holds the denoiser's switches - **Pre-Blur**, **Stabilize Output**, **Voxel Light For New
Pixels** and **Instant Light Switches**, all on by default. Switch one off to see what it does.

## Performance

If you need frames back:

- Lower **Light Update Rate** or **Detail Reach**.
- With ray tracing on, raise **Pixel Step** to 3 or 4, and turn off **Traced Second Bounce**.
- In VR every eye is drawn on its own, so the mod costs about twice what it does on flatscreen.

## Compatibility

- **ReflectSix:** reflection rays that leave the screen continue through TraceSix's world: through its voxels, or with ray tracing on (flatscreen), traced against the real geometry, so they show what they hit.
- **AOSix:** with ray tracing lighting the picture, Ambient Occlusion = ScreenSpace lays AOSix's AO on top. AOSix's own bounce light is limited to its AO radius so near light isn't counted twice.
- **SPT-VR:** supported. Ray tracing in VR is experimental and off by default.

## Known limitations

- In VR only the ray-traced bounced light runs. Ray-traced sun and lamp shadows, ray-traced AO and ray-traced reflections are flatscreen only.
- Factory, the Lab and the Labyrinth never use ray-traced sun shadows. Those maps keep the game's own sun shadows.
- Players, bots, loot and grass are not in the ray-traced scene.
- When you walk into a new area the bounced light takes a moment to settle.

## Troubleshooting

If ray tracing ever freezes or crashes your game, TraceSix switches it off for the rest of the session and writes a GPU
report to `BepInEx/plugins/TraceSix/native/TraceSixRT-gpu-report.txt`. Please include that file and
`BepInEx/LogOutput.log` when you report it.

## Building from source

```sh
dotnet build TraceSix.csproj -c Release
```

The project references the game's DLLs from a local `libs/` folder that isn't in the repository. Copy these
into it from your SPT install:

- From `EscapeFromTarkov_Data/Managed`: `Assembly-CSharp.dll` (publicized), `Comfort.dll`, `Comfort.Unity.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.PhysicsModule.dll`, `UnityEngine.TerrainModule.dll`, `UnityEngine.AssetBundleModule.dll`
- From `BepInEx/core`: `0Harmony.dll`, `BepInEx.dll`
- From `BepInEx/plugins/spt`: `spt-reflection.dll`

The shaders ship compiled in the `tracesix` bundle in each release, and the ray tracing ships as the prebuilt
`native/TraceSixRT.dll`. Their shader source isn't part of this repository.

## Credits

- Y. Ouyang et al., "ReSTIR GI: Path Resampling for Real-Time Path Tracing" (HPG 2021).
- B. Bitterli et al., "Spatiotemporal reservoir resampling for real-time ray tracing with dynamic direct lighting" (SIGGRAPH 2020).
- NVIDIA's ReLAX and ReBLUR denoisers (NRD), whose structure the ray-traced light's denoiser follows.
- G. Boissé et al. (AMD), "GI-1.0: A Fast and Scalable Two-Level Radiance Caching Scheme for Real-Time Global Illumination", for starting newly revealed areas from a world-space cache.
- H. Philippi, J. R. Frisvad and H. W. Jensen, "Practical temporal and stereoscopic filtering for real-time ray tracing" (EGSR 2023), for the VR stereo blend.

## License

The code in this repository is licensed under the GNU General Public License v3.0. See `LICENSE.txt`.
