# Concentus (vendored, decoder only)

The Opus decoder from [Concentus](https://github.com/lostromb/concentus) by Logan Stromberg, a C# port
of libopus (fixed-point build). Licensed under the Opus BSD license in `LICENSE`; every file keeps its
copyright notice. Halo 2 in MCC stores most of its sounds as raw Opus packets in `sounds_*.dat`.

Taken from `CSharp/Concentus` at commit 3885c4e ("Fix the Celt decoding bug. (#60)"), version 2.2.2.
The mount has to be one assembly (s&box doesn't resolve dependencies next to a mount's DLL), so the
source is compiled in rather than referenced as a package.

Changes from upstream:

- Only the decoder side is kept. Removed: the native libopus loader (`Native/`, `OpusCodecFactory`),
  the encoders (Opus, CELT, SILK and their analysis, VAD and noise shaping code), the multistream
  codecs, the repacketizer, the resamplers and their factories, and files nothing else used.
- Encoder-only methods removed from files the decoder shares: `CodecHelpers.optimize_framesize`,
  `compute_frame_size`, `compute_stereo_width`, `Filters.silk_prefilter`, `silk_prefilt`,
  `NLSF.silk_process_NLSFs`, `Stereo.silk_stereo_LR_to_MS`, `Tables.net`.
- `OpusDecoder`'s constructor is no longer marked obsolete (it pointed at the factory, which is gone).
- `#define PARITY` at the top of `Common/Inlines.cs` and `Opus/CodecHelpers.cs`, the define Concentus'
  own builds set, which makes its math match libopus.

Checked against libopus 1.2.1 on 58 Halo 2 sounds (mono, stereo, dialog, multi-chunk): within 9 LSB
of the float decoder's output, the expected fixed vs float difference, at ~120x realtime.
