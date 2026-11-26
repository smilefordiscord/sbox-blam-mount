# sbox-blam-mount

Mount the classic Halo games directly inside [s&box](https://sbox.game) from your Steam copy of **Halo: The Master Chief Collection**. Maps, models, shaders, effects, sounds, and weapons are read straight from MCC's `.map` files at runtime with no pre-extraction or redistributed assets.

## Progress

**Halo: Combat Evolved**, **Halo 2 (classic)**, and **Halo 3** all mount as separate entries (`haloce`, `halo2`, `halo3`) in s&box's mount list. Only the Steam version of MCC is supported.

| Game | Mount | Status | Maps | Shaders | Models | Effects | Audio | Weapons | Vehicles |
|---|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **Halo: Combat Evolved** | `haloce` | ![In Progress](https://img.shields.io/badge/In%20Progress-d29922?style=flat-square) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ *(10)* | ⬜ |
| **Halo 2** *(Classic)* | `halo2` | ![In Progress](https://img.shields.io/badge/In%20Progress-d29922?style=flat-square) | ✅ | ✅ | ✅ | ✅ | ✅ | 🚧 *(WIP)* | ⬜ |
| **Halo 3** | `halo3` | ![In Progress](https://img.shields.io/badge/In%20Progress-d29922?style=flat-square) | ✅ | ✅ | ✅ | ✅ | ✅ | ⬜ | ⬜ |
| **Halo 3: ODST** | — | ![Planned](https://img.shields.io/badge/Planned-6e7681?style=flat-square) | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| **Halo: Reach** | — | ![Planned](https://img.shields.io/badge/Planned-6e7681?style=flat-square) | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| **Halo 4** | — | ![Planned](https://img.shields.io/badge/Planned-6e7681?style=flat-square) | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |

<sub>✅ Supported &nbsp;·&nbsp; 🚧 Work in progress &nbsp;·&nbsp; ⬜ Not started yet</sub>

<details>
<summary><strong>Detailed per-game breakdown</strong></summary>

- **Halo: Combat Evolved (`mount://haloce/`)**
  - **Maps**: all 29 maps with collision, spawn points, fog, weather, decals, grass, sky, and object shadows.
  - **Shaders**: environment, model, glass, water, transparent, plasma, meters, active camouflage, and MCC weapon skins.
  - **Models & Objects**: skinned models, animations, LODs, ragdolls, first-person view models, scenery, vehicles, weapons, machines, bipeds, lights, lightning, and flags.
  - **Effects & Audio**: particles, particle systems, contrails, decals, lens flares, light volumes, all sound tags, and level ambience.
  - **Sandbox Weapons**: 10 multiplayer weapons in the spawn menu with Halo combat rules, first-person animations, and HUD.

- **Halo 2 (`mount://halo2/`)**
  - **Maps**: all campaign and multiplayer maps with collision, spawn points, atmospheric/planar/patchy fog, weather, water, decals, decorators, and sky.
  - **Shaders**: all 95 opaque templates, transparent, plasma, water, overlays/emblems, shields, active camouflage, and bloom.
  - **Models & Objects**: skinned models, animations, ragdolls, first-person view models (including dual wield), first-person bodies, scenery, vehicles, weapons, machines, bipeds, crates with physics, multiplayer items, and lights.
  - **Effects & Audio**: particles, contrails, lens flares, light volumes, sun glare, all sound tags, looping sounds, and level ambience.
  - **Sandbox Weapons**: work in progress (early multiplayer weapon prefabs; not fully working yet).

- **Halo 3 (`mount://halo3/`)**
  - **Maps**: all campaign and multiplayer maps with collision, spawn points, atmospheric/patchy fog, weather, water, decals, decorators, and sky.
  - **Shaders**: render methods (shader, terrain, foliage, decal, halogram, custom, water) with baked SH lightmaps, object shadows, exposure, tone curve, bloom, and screen effects.
  - **Models & Objects**: skinned models, animations, variants, attachments, change colours, scenery, vehicles, weapons, equipment, machines, bipeds, crates, and multiplayer items.
  - **Effects & Audio**: particles, particle systems, distortion, lens flares, light volumes, all sound tags, looping sounds, and level ambience.

</details>

## What the mounts provide

- **Maps** (`maps/<map>.scene`): full level geometry, collision, lighting, sky, water, fog, weather, placed objects, and spawn points.
- **Models** (`<tag path>.vmdl`): skinned meshes, variants/body groups, animations, ragdolls/physics models, and first-person weapon view models.
- **Textures & Fonts** (`<tag path>.vtex`): 2D, cubemap, 3D, array, and sprite textures, plus game fonts.
- **Sounds** (`<tag path>.sound`): randomized sound events and looping ambience decoded directly from MCC's audio banks.
- **Effects & Prefabs** (`<tag path>.prefab`): standalone effects, object attachments, and projectiles simulated by the mount's runtime.
- **Sandbox weapons** (`entities/weapons/<name>.prefab`): Halo CE's multiplayer weapons tagged `weapon` so Facepunch's Sandbox lists them in the spawn menu with Halo firing rules, projectiles, reloads, melee, grenades, first-person animations, and HUD (Halo 2 weapons are early WIP).
- **Tag data as JSON** (`game.json`, `hud.json`, `maps/<map>.json`, `tags/...`, `anims/...`): structured gameplay, HUD, scenario, and animation data for game code.

## Building & running

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- **s&box** installed via Steam
- **Halo: The Master Chief Collection** installed via Steam (with the games you want to mount installed)

### Build and deploy

Close s&box and the s&box editor first (they keep mounted DLLs locked while running), then run:

```sh
dotnet build src/Sandbox.Mounting.Blam -p:DeployToSbox=true
```

If s&box isn't in the default Steam library (`C:\Program Files (x86)\Steam\steamapps\common\sbox`), pass your install path:

```sh
dotnet build src/Sandbox.Mounting.Blam -p:DeployToSbox=true -p:SboxDir="D:\path\to\sbox"
```

This compiles the mount DLL, runs s&box's code generator over the gameplay/weapon code, compiles the mount's `.shader` files using s&box's `ShaderCompiler.exe`, and deploys everything to `<sbox>\mounts\blam\`:

- `mounts/blam/blam.dll`
- `mounts/blam/Assets/` (shaders and compiled `.shader_c` files)

Start s&box (or the editor) and enable **Halo: Combat Evolved**, **Halo 2**, and/or **Halo 3** in the mount list.

> **Note on shaders:** Compiled shaders (`.shader_c`) are tied to the s&box build that compiled them. After s&box updates, run the build command again so they recompile. You can pass `-p:CompileShaders=false` to skip shader compilation when only building the C# assembly.

### Useful console commands

- `halo_gamma` (`1`–`10`, default `6`): adjusts brightness to match MCC's gamma slider.
- `mount_generatethumbs haloce`: generates local map thumbnails in s&box (thumbnails aren't shipped since they're rendered from game content). Restart s&box afterwards.

## Repository layout

- `src/Sandbox.Mounting.Blam/Blam/` — standalone Blam format readers (`Halo1/`, `Halo2/`, `Halo3/`, `Audio/` decoders, `Unreal/` pak reader for MCC weapon skins) with no s&box dependency.
- `src/Sandbox.Mounting.Blam/Halo1/`, `Halo2/`, `Halo3/` — the s&box mounts that convert tags into scenes, models, materials, textures, effects, and sounds.
- `src/Sandbox.Mounting.Blam/Gameplay/` — Sandbox weapons, Halo CE/Halo 2 combat simulation, HUD, and player pose components.
- `src/Sandbox.Mounting.Blam/Assets/shaders/` — custom shaders for Halo CE, Halo 2, and Halo 3 rendering.
- `tools/GameplayCodeGen/` — build-time wrapper around s&box's code generator so `[Sync]` and `[Rpc]` work in `Gameplay/`.
- `tools/compile-shaders.ps1` — invokes s&box's `ShaderCompiler.exe` during the build.

## Credits

See [CREDITS.md](CREDITS.md). This project builds on years of Halo modding community research and documentation.

## License

MIT — see [LICENSE](LICENSE). Halo and Halo: The Master Chief Collection are trademarks of Microsoft Corporation. This project is not affiliated with or endorsed by Microsoft, Halo Studios, or Facepunch Studios.
