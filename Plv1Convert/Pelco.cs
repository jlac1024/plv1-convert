namespace Plv1Convert;

/// <summary>A single coded picture lifted out of the AVI.</summary>
readonly struct PelcoFrame(bool key, uint timestamp, byte[] vop)
{
    public bool IsKey { get; } = key;
    public uint Timestamp { get; } = timestamp;      // recorder clock, unix seconds
    public byte[] Vop { get; } = vop;                // MPEG-4 VOP, start code first
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
            for (int j = 0; j < n; j++) o[i + j] = d[i + n - 1 - j];
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
            if (size > int.MaxValue - 16) break;               // corrupt header
            if (id is "RIFF" or "LIST")
            {
                Walk(data, body + 4, (int)Math.Min((long)body + size, end), visit);
            }
            else
            {
                visit(id, body, (int)size);
            }
            long next = (long)body + size + (size & 1);
            if (next <= i) break;                              // no forward progress
            i = (int)next;
        }
    }

    public static List<PelcoFrame> ReadFrames(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var frames = new List<PelcoFrame>();

        Walk(data, 0, data.Length, (id, off, size) =>
        {
            if (id is not ("00dc" or "00db") || size <= 4) return;   // <=4 bytes is padding
            if (off + size > data.Length) return;
            var d = new ReadOnlySpan<byte>(data, off, size);
            uint ts = BitConverter.ToUInt32(data, off + 4);
            byte kind = d[11];

            if (kind == 4)
            {
                int n = (int)BitConverter.ToUInt32(data, off + 16);
                if (n <= 0 || 24 + n > size) return;
                frames.Add(new PelcoFrame(false, ts, d.Slice(24, n).ToArray()));
            }
            else if (kind == 6)
            {
                int k = IndexOfReversedStartCode(d);
                if (k < 8) return;
                int n = (int)BitConverter.ToUInt32(Unswap(d.Slice(k - 8, 4)), 0);
                byte[] body = Unswap(d[(k - 8)..]);
                if (n <= 0 || 8 + n > body.Length) return;
                frames.Add(new PelcoFrame(true, ts, body.AsSpan(8, n).ToArray()));
            }
        });

        return frames;
    }

    /// <summary>Find the VOP start code as it appears reversed: B6 01 00 00.</summary>
    static int IndexOfReversedStartCode(ReadOnlySpan<byte> d)
    {
        for (int i = 0; i + 4 <= d.Length; i++)
            if (d[i] == 0xB6 && d[i + 1] == 0x01 && d[i + 2] == 0x00 && d[i + 3] == 0x00)
                return i;
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
            if (prev is uint p && f.Timestamp > p + 1) waiting = true;   // recording gap
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
            if (delta > 1) gaps.Add((frames[i].Timestamp, delta));
        }
        return gaps;
    }
}
