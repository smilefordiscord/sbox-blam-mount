# Credits

This mount contains no game content, and no code from other projects apart from the audio decoders listed under [Third-party code](#third-party-code). Every format reader here was written from scratch. It would not exist without years of work by the Halo modding community, whose tools and documentation were used as references for the file formats.

## Format documentation and reference implementations

- **c20 / The Reclaimers Library** (https://c20.reclaimers.net), by the Reclaimers community.
  Main reference for cache file headers, tag data headers, tag arrays, resource maps, and Halo CE tag structures (bitmaps, fonts, shaders, models, collision/physics, animations, effects, fog, decals, flags, HUD, and gameplay tags). Documentation licensed CC BY-SA 3.0.
- **Reclaimer** (https://github.com/Gravemind2401/Reclaimer), by Gravemind2401.
  Reference for MCC PC `.map` layouts across H1–H4, MCC version detection, and Halo CE `bitmaps.map` addressing. GPL-3.0; used as a reference only, no code was copied.
- **Invader** (https://github.com/SnowyMouse/invader), by Snowy Mouse.
  Reference for Halo CE tag semantics, `mcc-cea` cache behaviour, and model animation/effect tag layouts. GPL-3.0; used as a reference only, no code was copied.
- **Assembly** (https://github.com/XboxChaos/Assembly), by Xbox Chaos.
  Its per-game plugin layouts (Halo1MCC, Halo2MCC, Halo3MCC) were used as a reference for tag field offsets, enums, and flags across Halo CE, Halo 2, and Halo 3. GPL-3.0; used as a reference only, no code was copied.
- **Prometheus** (https://github.com/grimdoomer/Prometheus-Alpha), by Grimdoomer (I Code 4 Coffee).
  Reference for Halo 2 shader templates, passes, layers, and tag layouts. Closed source; used as a reference only, no code was copied.
- **Halo Asset Blender Development Toolset** (https://github.com/General-101/Halo-Asset-Blender-Development-Toolset), by General-101 and contributors.
  Reference for Halo CE compressed animation layouts. MIT; used as a reference only, no code was copied.
- **CEnshine** (https://github.com/Sledmine/censhine), by Sledmine and contributors.
  Reference for Halo PC environment shader specular, reflection, self-illumination, and lightmap combining. GPL-3.0; used as a reference only, no code was copied.
- **vgmstream** (https://github.com/vgmstream/vgmstream), by the vgmstream contributors.
  Reference for FSB5 bank layout and FMOD CELT framing. Used as documentation only, no code was copied.

## Third-party code

- **CELT 0.11.0** (https://downloads.xiph.org/releases/celt/), by Jean-Marc Valin, Timothy B. Terriberry, Gregory Maxwell, CSIRO, and the Xiph.Org Foundation. BSD license. Floating-point decoder ported to C# in `src/Sandbox.Mounting.Blam/Blam/Audio/Celt`, keeping license and copyright notices (used for MCC Halo CE sound banks).
- **Concentus** (https://github.com/lostromb/concentus), by Logan Stromberg — a C# port of libopus (Xiph.Org Foundation, Skype Limited, CSIRO, Microsoft Corporation, Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry, and the Opus contributors). Opus BSD license. Decoder included in `src/Sandbox.Mounting.Blam/Blam/Audio/Concentus` with license and copyright notices (used for MCC Halo 2 Opus sounds).
- **NVorbis 0.10.5** (https://github.com/NVorbis/NVorbis), by Andrew Ward (MDCT and Huffman codeword generator derived from public domain `stb_vorbis`). MIT license. Included in `src/Sandbox.Mounting.Blam/Blam/Audio/NVorbis` with its license (used for MCC Halo 3 FMOD Vorbis sounds).

## Platform

- **s&box** (https://github.com/Facepunch/sbox-public), by Facepunch Studios. MIT.
  The mounting API and built-in mounts as structural examples for runtime models, bones, skinning weights, and animation frames.

## Halo

Halo and Halo: The Master Chief Collection are trademarks of Microsoft Corporation. This project is not affiliated with or endorsed by Microsoft, Halo Studios, or Facepunch Studios. It reads content only from your own installed copy of the game, and no game content is distributed with it.
