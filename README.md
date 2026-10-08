# plv1-convert

Converts Pelco DX-series AVI exports into playable MP4.

The recorders write MPEG-4 that standard players cannot decode. VLC sees only
the audio track, which on these exports is silent, so its conversions come out
empty. This tool rebuilds the video into a standards-compliant stream.

Nothing from Pelco is needed: no DX client, no codec pack, no filter to
install. The frames are rewritten into MPEG-4 that stock decoders already
understand, so no vendor software is installed or called at any point.

Built with C# NativeAOT: a single 1.5 MB executable with no .NET runtime needed
on the target machine.

## Usage

```
plv1-convert <input.avi> [options]

  -o, --output <file>   output path (default: <input>_h264.mp4)
      --crf <n>         H.264 quality, lower is better (default 18)
      --fps <n>         override the detected frame rate
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
  knows the frame size or timing. The tool synthesizes one, taking the frame
  size from the AVI's own stream format header.
* `vop_quant` is 6 bits wide rather than the usual 5. Combined with the
  variable-length `modulo_time_base`, which carries an extra bit on the frame
  where the recorder's clock ticks over once a second, a fixed-offset parse
  lands a bit out of step on those frames. That still decodes without
  complaint; the picture just comes out blocky once a second.
* `vop_fcode_forward` reads 3 on every P-frame.
* Audio is present but silent on every export seen so far, so it is dropped.

## Frame rate

Three clocks in these files disagree, and only two of them are trustworthy:

* The AVI header rate (29.97 fps on the exports seen here) is the timeline
  rate, not the capture rate. The recorder emits one real frame every fourth
  slot and pads the rest with empty chunks, giving 29.97/4, about 7.49 fps.
* The per-frame timestamps agree: 910 frames across 124 recorded seconds.
* The MPEG-4 stream's own `vop_time_increment` claims 5 fps. It does not match
  the clock the recorder stamps on each frame, and using it plays the footage
  roughly 1.5x too slow.

So the rate is taken from the header rate divided by the slot spacing, and the
stream's internal time base is ignored. `--fps` overrides it if a recorder
turns up that pads differently.

## Resolutions

The frame size is read from the AVI, so CIF (352x240) and 4CIF (704x480) both
work; the DX4600 commonly records the latter. Only 352x240 NTSC has been tested
against real footage. Pixel aspect is set so the picture displays 4:3, which is
what these cameras frame for, and that is derived from the frame size rather
than assumed, so PAL sizes should come out correctly too.

If you run this against another resolution or a PAL recorder, the interesting
question is whether the slot spacing still yields a sane frame rate. The tool
prints the size and rate it detected on every run; check that line first.

## Recording gaps

These exports are motion-triggered, so the footage is not continuous. The tool
reports the gaps it finds, and drops P-frames that have no valid reference: at
the start of the file, and after each gap, where they would otherwise smear the
previous scene across the new one for up to a second.

Playback time therefore does not track wall-clock time across a gap. Use
`--timestamp` when that distinction matters to whoever is watching.

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

## License

MIT, see [LICENSE](LICENSE).

This project is not affiliated with, endorsed by, or supported by Pelco or
Motorola Solutions. "Pelco" and the DX4500/DX4600 product names are their
trademarks, used here only to say which recorders these files come from.

