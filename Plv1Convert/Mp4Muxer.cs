using System.Buffers.Binary;

namespace Plv1Convert;

/// <summary>
/// Minimal MP4 writer for MPEG-4 Part 2 video, used when ffmpeg is unavailable.
///
/// The rebuilt frames are stored as-is (no re-encoding), with the synthesized
/// VOL header carried in the esds descriptor where a decoder expects it. The
/// result plays in VLC and most modern players, but it is MPEG-4 Part 2 rather
/// than H.264, so it is the fallback rather than the preferred output.
/// </summary>
static class Mp4Muxer
{
    static byte[] Box(string type, params byte[][] parts)
    {
        int len = 8;
        foreach (var p in parts) len += p.Length;
        var o = new byte[len];
        BinaryPrimitives.WriteInt32BigEndian(o, len);
        for (int i = 0; i < 4; i++) o[4 + i] = (byte)type[i];
        int at = 8;
        foreach (var p in parts) { p.CopyTo(o, at); at += p.Length; }
        return o;
    }

    static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    static byte[] Cat(params byte[][] parts)
    {
        int len = 0;
        foreach (var p in parts) len += p.Length;
        var o = new byte[len];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(o, at); at += p.Length; }
        return o;
    }
    static byte[] Zeros(int n) => new byte[n];

    /// <summary>MPEG-4 descriptor with a 4-byte extended length.</summary>
    static byte[] Descriptor(byte tag, byte[] body) =>
        Cat([tag, 0x80, 0x80, 0x80, (byte)body.Length], body);

    static byte[] Esds(byte[] vol)
    {
        byte[] dsi = Descriptor(0x05, vol);                       // DecoderSpecificInfo
        byte[] dcd = Descriptor(0x04, Cat(
            [0x20],                                               // MPEG-4 visual
            [0x11],                                               // visual stream
            Zeros(3),                                             // buffer size
            U32(0), U32(0),                                       // max / avg bitrate
            dsi));
        byte[] sl = Descriptor(0x06, [0x02]);                     // SLConfig: MP4
        byte[] es = Descriptor(0x03, Cat(U16(1), [0x00], dcd, sl));
        return Box("esds", Zeros(4), es);                         // version + flags
    }

    /// <summary>
    /// Pixel aspect that displays the recording at 4:3, the shape these
    /// cameras frame for whatever pixel grid they sample it onto.
    /// </summary>
    static (uint H, uint V) PixelAspect(int width, int height)
    {
        uint h = (uint)(4 * height), v = (uint)(3 * width);
        uint a = h, b = v;
        while (b != 0) (a, b) = (b, a % b);                       // greatest common divisor
        return a == 0 ? (1, 1) : (h / a, v / a);
    }

    static byte[] VisualSampleEntry(byte[] vol, int width, int height)
    {
        var (sarH, sarV) = PixelAspect(width, height);
        byte[] name = new byte[32];
        name[0] = 0;                                              // empty compressor name
        return Box("mp4v",
            Zeros(6), U16(1),                                     // reserved, data ref index
            Zeros(16),                                            // pre_defined / reserved
            U16((ushort)width), U16((ushort)height),
            U32(0x00480000), U32(0x00480000),                     // 72 dpi
            Zeros(4), U16(1),                                     // reserved, frame count
            name, U16(0x0018), U16(0xFFFF),                       // depth, pre_defined
            Esds(vol),
            Box("pasp", U32(sarH), U32(sarV)));
    }

    public static void Write(string path, List<PelcoFrame> frames, PelcoVideo video)
    {
        int width = video.Width, height = video.Height;
        byte[] vol = Mpeg4.VolHeader(width, height);
        var samples = new List<byte[]>(frames.Count);
        foreach (var f in frames) samples.Add(Mpeg4.RebuildVop(f));

        uint count = (uint)samples.Count;
        uint timeScale = (uint)video.TimeScale;
        uint frameDuration = (uint)video.FrameDuration;
        uint duration = count * frameDuration;                    // in media timescale
        const uint MovieScale = 1000;
        uint movieDuration = (uint)((ulong)duration * MovieScale / timeScale);

        byte[] stts = Box("stts", Zeros(4), U32(1), U32(count), U32(frameDuration));

        var keys = new List<byte[]>();
        for (int i = 0; i < samples.Count; i++)
            if (frames[i].IsKey) keys.Add(U32((uint)(i + 1)));     // 1-based
        byte[] stss = Box("stss", Zeros(4), U32((uint)keys.Count), Cat([.. keys]));

        byte[] stsc = Box("stsc", Zeros(4), U32(1), U32(1), U32(count), U32(1));

        var sizes = new List<byte[]>(samples.Count);
        foreach (var s in samples) sizes.Add(U32((uint)s.Length));
        byte[] stsz = Box("stsz", Zeros(4), U32(0), U32(count), Cat([.. sizes]));

        byte[] ftyp = Box("ftyp",
            "isom"u8.ToArray(), U32(0x200), "isomiso2mp41"u8.ToArray());

        var (sarH, sarV) = PixelAspect(width, height);

        // stco needs the mdat payload offset, which depends on the size of moov.
        // Build once with a placeholder to learn that size, then again for real.
        byte[] BuildMoov(uint mdatOffset)
        {
            byte[] stco = Box("stco", Zeros(4), U32(1), U32(mdatOffset));
            byte[] stbl = Box("stbl",
                Box("stsd", Zeros(4), U32(1), VisualSampleEntry(vol, width, height)),
                stts, stss, stsc, stsz, stco);
            byte[] minf = Box("minf",
                Box("vmhd", U32(1), Zeros(8)),
                Box("dinf", Box("dref", Zeros(4), U32(1), Box("url ", [0, 0, 0, 1]))),
                stbl);
            byte[] mdia = Box("mdia",
                Box("mdhd", Zeros(4), U32(0), U32(0), U32(timeScale), U32(duration),
                    U16(0x55C4), U16(0)),                          // language "und"
                Box("hdlr", Zeros(4), U32(0), "vide"u8.ToArray(), Zeros(12),
                    "VideoHandler\0"u8.ToArray()),
                minf);
            byte[] tkhd = Box("tkhd",
                [0, 0, 0, 7],                                      // enabled, in movie
                U32(0), U32(0), U32(1), U32(0), U32(movieDuration),
                Zeros(8), U16(0), U16(0), U16(0), U16(0),
                U32(0x00010000), U32(0), U32(0),
                U32(0), U32(0x00010000), U32(0),
                U32(0), U32(0), U32(0x40000000),                   // unity matrix
                U32((uint)((long)width * 65536 * sarH / sarV)),    // display width
                U32((uint)height * 65536u));
            byte[] mvhd = Box("mvhd", Zeros(4), U32(0), U32(0), U32(MovieScale),
                U32(movieDuration), U32(0x00010000), U16(0x0100), Zeros(10),
                U32(0x00010000), U32(0), U32(0),
                U32(0), U32(0x00010000), U32(0),
                U32(0), U32(0), U32(0x40000000),
                Zeros(24), U32(2));
            return Box("moov", mvhd, Box("trak", tkhd, mdia));
        }

        uint probe = (uint)BuildMoov(0).Length;
        uint mdatData = (uint)ftyp.Length + probe + 8;             // past the mdat header
        byte[] moov = BuildMoov(mdatData);

        using var fs = File.Create(path);
        fs.Write(ftyp);
        fs.Write(moov);

        int payload = 0;
        foreach (var s in samples) payload += s.Length;
        fs.Write(U32((uint)(payload + 8)));
        fs.Write("mdat"u8);
        foreach (var s in samples) fs.Write(s);
    }
}
