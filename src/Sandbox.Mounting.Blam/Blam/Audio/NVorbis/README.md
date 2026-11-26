# NVorbis (vendored)

[NVorbis](https://github.com/NVorbis/NVorbis) by Andrew Ward, a fully managed Vorbis decoder (no P/Invoke, no unsafe
code). MIT license in `LICENSE`. Halo 3 in MCC stores its sounds as FMOD FSB5 Vorbis.

Taken from `NVorbis/` at release v0.10.5 (2022-10-16). The mount has to be one assembly (s&box doesn't resolve
dependencies next to a mount's DLL), so the source is compiled in rather than referenced as a package.

Changes from upstream:

- `#pragma warning disable CA2265` at the top of `StreamDecoder.cs` (a span compared to null, which the repo's
  warnings-as-errors build rejects).

The mount feeds `StreamDecoder` raw packets through its own `IPacketProvider` (`Blam/Audio/Fsb5Vorbis.cs`); the Ogg
container reader is kept as is but unused.
