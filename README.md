# plv1-convert

Converts Pelco DX-series (DX4500/DX4600) AVI exports into playable MP4.

The recorders write MPEG-4 that standard players cannot decode. VLC sees only
the audio track, which on these exports is silent, so its conversions come out
empty. This tool rebuilds the video into a standards-compliant stream.

Built with C# NativeAOT: a single 1.5 MB executable with no .NET runtime needed
on the target machine.

## Usage

```
plv1-convert <input.avi> [options]

  -o, --output <file>   output path (default: <input>_h264.mp4)
      --crf <n>         H.264 quality, lower is better (default 18)
      --timestamp       show the recording clock
      --deblock         smooth MPEG-4 block edges (alters the picture)
      --es-only         write just the raw .m4v elementary stream
      --keep-es         keep the .m4v alongside the MP4
      --ffmpeg <path>   use this ffmpeg instead of searching
```

## ffmpeg

All the Pelco-specific work happens inside the exe. ffmpeg is used only for the
final H.264 encode, and is looked for next to the exe and then on PATH.

Without it the exe still produces a working MP4, but the video stays MPEG-4
Part 2 (the recorder's own format, remuxed rather than re-encoded): playable in
VLC and most modern players, though not as universally as H.264. In that mode
`--timestamp` writes the clock as a `.ass` subtitle file next to the video
instead of burning it in, and `--deblock` is unavailable.

## The format

Fields marked PLV1 in the AVI header. Per frame:

| Part | Detail |
| --- | --- |
| Frame header | 12 bytes: recording timestamp (unix seconds) and frame type |
| Keyframes | chunk id `00db`, type 6, plus a 76-byte prefix; payload stored with each 4-byte group byte-reversed |
| P-frames | chunk id `00dc`, type 4, plain MPEG-4 after a 24-byte header |
| Padding | chunks of 4 bytes or fewer, no picture data |

Beyond that:

* The MPEG-4 sequence/VOL header is missing entirely, so nothing downstream
  knows the frame size or timing. The tool synthesizes one: 352x240,
  `vop_time_increment_resolution` 15, H.263 quantizer.
* `vop_quant` is 6 bits wide rather than the usual 5. Combined with the
  variable-length `modulo_time_base`, which carries an extra bit on the frame
  where the recorder's clock ticks over once a second, a fixed-offset parse
  lands a bit out of step on those frames. That still decodes without
  complaint; the picture just comes out blocky once a second.
* `vop_fcode_forward` reads 3 on every P-frame.
* The AVI header claims 29.97 fps. The real rate is 5 fps, padded out with
  empty placeholder chunks.
* Audio is present but silent on every export seen so far, so it is dropped.

## Recording gaps

These exports are motion-triggered, so the footage is not continuous. The tool
reports the gaps it finds, and drops P-frames that have no valid reference: at
the start of the file, and after each gap, where they would otherwise smear the
previous scene across the new one for up to a second.

Playback time therefore does not track wall-clock time. Use `--timestamp` when
that distinction matters to whoever is watching.

## Evidentiary use

`--deblock` alters the picture. It is for viewing only; hand over an unfiltered
copy if the footage might be used as evidence.

Timestamps render in the local timezone of the machine doing the conversion.

## Building

```powershell
.\build.ps1
```

Needs the .NET 10 SDK and the MSVC x64 toolset (Visual Studio Build Tools with
"Desktop development with C++"). `dotnet publish` normally finds the linker on
its own; where vcvarsall is broken or half-installed, the script locates the
toolset and Windows SDK and passes them in directly.

Output: `Plv1Convert\bin\Release\net10.0\win-x64\publish\plv1-convert.exe`
