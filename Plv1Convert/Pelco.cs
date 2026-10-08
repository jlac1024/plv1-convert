namespace Plv1Convert;

/// <summary>A single coded picture lifted out of the AVI.</summary>
readonly struct PelcoFrame(bool key, uint timestamp, byte[] vop)
{
    public bool IsKey { get; } = key;
    public uint Timestamp { get; } = timestamp;      // recorder clock, unix seconds
    public byte[] Vop { get; } = vop;                // MPEG-4 VOP, start code first
}

/// <summary>Everything the AVI tells us about the recording.</summary>
sealed class PelcoVideo
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Ticks per second of the container timeline.</summary>
    public required int TimeScale { get; init; }

    /// <summary>Ticks each frame is held for, so the rate is TimeScale/FrameDuration.</summary>
    public required int FrameDuration { get; init; }

    public required List<PelcoFrame> Frames { get; init; }

    public double Fps => TimeScale / (double)FrameDuration;
}

/// <summary>
/// Reads the Pelco DX-series (PLV1) AVI layout.
///
/// The recorders write MPEG-4 that no standard player can decode:
///   * every frame carries a 12-byte Pelco header (timestamp + frame type),
///   * keyframes (chunk id 00db, type 6) carry a further 76-byte prefix and
///     store their payload with each 4-byte group byte-reversed,
///   * P-frames (chunk id 00dc, type 4) are plain MPEG-4 after a 24-byte header.
/// </summary>
static class PelcoAvi
{
    /// <summary>Undo the recorder's 4-byte word byte-reversal.</summary>
    public static byte[] Unswap(ReadOnlySpan<byte> d)
    {
        var o = new byte[d.Length];
        for (int i = 0; i < d.Length; i += 4)
        {
            int n = Math.Min(4, d.Length - i);
            for (int j = 0; j < n; j++) { o[i + j] = d[i + n - 1 - j]; }
        }
        return o;
    }

    /// <summary>Walk RIFF chunks, descending into RIFF/LIST containers.</summary>
    static void Walk(byte[] data, int start, int end, Action<string, int, int> visit)
    {
        int i = start;
        while (i + 8 <= end)
        {
            string id = System.Text.Encoding.ASCII.GetString(data, i, 4);
            uint size = BitConverter.ToUInt32(data, i + 4);
            int body = i + 8;
            if (size > int.MaxValue - 16) { break; }           // corrupt header
            if (id is "RIFF" or "LIST")
            {
                Walk(data, body + 4, (int)Math.Min((long)body + size, end), visit);
            }
            else
            {
                visit(id, body, (int)size);
            }
            long next = (long)body + size + (size & 1);
            if (next <= i) { break; }                          // no forward progress
            i = (int)next;
        }
    }

    public static PelcoVideo Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var frames = new List<PelcoFrame>();
        var slots = new List<int>();                 // timeline slot of each real frame
        int slot = 0, width = 0, height = 0, scale = 0, rate = 0;
        bool videoStream = false;

        Walk(data, 0, data.Length, (id, off, size) =>
        {
            // Stream headers: the video stream's rate and frame size live here.
            if (id == "strh" && size >= 36)
            {
                videoStream = System.Text.Encoding.ASCII.GetString(data, off, 4) == "vids";
                if (videoStream && rate == 0)
                {
                    scale = (int)BitConverter.ToUInt32(data, off + 20);
                    rate = (int)BitConverter.ToUInt32(data, off + 24);
                }
                return;
            }
            if (id == "strf" && videoStream && size >= 12 && width == 0)
            {
                width = (int)BitConverter.ToUInt32(data, off + 4);
                height = Math.Abs(BitConverter.ToInt32(data, off + 8));
                videoStream = false;
                return;
            }

            if (id is not ("00dc" or "00db")) { return; }
            slot++;                                            // padding still occupies a slot
            if (size <= 4) { return; }                         // <=4 bytes is padding, no picture
            if (off + size > data.Length) { return; }

            var d = new ReadOnlySpan<byte>(data, off, size);
            uint ts = BitConverter.ToUInt32(data, off + 4);
            byte kind = d[11];

            if (kind == 4)
            {
                int n = (int)BitConverter.ToUInt32(data, off + 16);
                if (n <= 0 || 24 + n > size) { return; }
                frames.Add(new PelcoFrame(false, ts, d.Slice(24, n).ToArray()));
                slots.Add(slot);
            }
            else if (kind == 6)
            {
                int k = IndexOfReversedStartCode(d);
                if (k < 8) { return; }
                int n = (int)BitConverter.ToUInt32(Unswap(d.Slice(k - 8, 4)), 0);
                byte[] body = Unswap(d[(k - 8)..]);
                if (n <= 0 || 8 + n > body.Length) { return; }
                frames.Add(new PelcoFrame(true, ts, body.AsSpan(8, n).ToArray()));
                slots.Add(slot);
            }
        });

        if (width is < 16 or > 4096 || height is < 16 or > 4096)
        {
            throw new Exception($"implausible frame size in the AVI header ({width}x{height})");
        }

        var (timeScale, frameDuration) = Rate(scale, rate, slots, frames);
        return new PelcoVideo
        {
            Width = width,
            Height = height,
            TimeScale = timeScale,
            FrameDuration = frameDuration,
            Frames = frames,
        };
    }

    /// <summary>
    /// Work out how fast the recording actually runs.
    ///
    /// The AVI header rate is the timeline rate, not the capture rate: the
    /// recorder emits one real frame every few slots and pads the rest with
    /// empty chunks, so the capture rate is the header rate divided by that
    /// spacing. The MPEG-4 stream carries its own vop_time_increment, but the
    /// recorder leaves it at a nominal value that does not match the clock it
    /// stamps on each frame, so it must not be used for playback timing.
    /// </summary>
    static (int TimeScale, int FrameDuration) Rate(int scale, int rate,
                                                   List<int> slots, List<PelcoFrame> frames)
    {
        if (rate > 0 && scale > 0 && slots.Count > 1)
        {
            var spacing = new Dictionary<int, int>();
            for (int i = 1; i < slots.Count; i++)
            {
                int gap = slots[i] - slots[i - 1];
                if (gap > 0) { spacing[gap] = spacing.GetValueOrDefault(gap) + 1; }
            }
            int common = 0, seen = 0;
            foreach (var (gap, n) in spacing)
            {
                if (n > seen) { common = gap; seen = n; }
            }
            if (common > 0)
            {
                long duration = (long)scale * common;
                if (duration is > 0 and < int.MaxValue) { return (rate, (int)duration); }
            }
        }

        // No usable header: fall back to the recorder's own clock, counting how
        // many frames it stamped with each second.
        var perSecond = new Dictionary<uint, int>();
        foreach (var f in frames) { perSecond[f.Timestamp] = perSecond.GetValueOrDefault(f.Timestamp) + 1; }
        double fps = perSecond.Count > 0 ? frames.Count / (double)perSecond.Count : 0;
        if (fps is <= 0 or > 60) { fps = 7.5; }
        return (10000, Math.Max(1, (int)Math.Round(10000 / fps)));
    }

    /// <summary>Find the VOP start code as it appears reversed: B6 01 00 00.</summary>
    static int IndexOfReversedStartCode(ReadOnlySpan<byte> d)
    {
        for (int i = 0; i + 4 <= d.Length; i++)
        {
            if (d[i] == 0xB6 && d[i + 1] == 0x01 && d[i + 2] == 0x00 && d[i + 3] == 0x00)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Discard P-frames with no valid reference.
    ///
    /// A P-frame only encodes what changed since the previous frame, so the ones
    /// at the start of the export - and after a recording gap, where the previous
    /// frame is minutes older - reconstruct against the wrong picture and decode
    /// as smeared garbage until the next keyframe.
    /// </summary>
    public static List<PelcoFrame> DropOrphans(List<PelcoFrame> frames, out int dropped)
    {
        var kept = new List<PelcoFrame>(frames.Count);
        bool waiting = true;
        uint? prev = null;
        dropped = 0;

        foreach (var f in frames)
        {
            if (prev is uint p && f.Timestamp > p + 1) { waiting = true; } // recording gap
            prev = f.Timestamp;
            if (waiting)
            {
                if (!f.IsKey) { dropped++; continue; }
                waiting = false;
            }
            kept.Add(f);
        }
        return kept;
    }

    /// <summary>Recording gaps as (timestamp before the gap, seconds missing).</summary>
    public static List<(uint At, uint Seconds)> Gaps(List<PelcoFrame> frames)
    {
        var gaps = new List<(uint, uint)>();
        for (int i = 0; i + 1 < frames.Count; i++)
        {
            uint delta = frames[i + 1].Timestamp - frames[i].Timestamp;
            if (delta > 1) { gaps.Add((frames[i].Timestamp, delta)); }
        }
        return gaps;
    }
}
