namespace Plv1Convert;

sealed class BitWriter
{
    readonly List<byte> _bits = new(256);

    public int Count => _bits.Count;

    public void U(long value, int n)
    {
        for (int i = n - 1; i >= 0; i--) _bits.Add((byte)((value >> i) & 1));
    }

    public void Bit(int b) => _bits.Add((byte)(b & 1));

    /// <summary>MPEG-4 stuffing: a 0 bit then 1s to the byte boundary.</summary>
    public void Stuff()
    {
        U(0, 1);
        while (_bits.Count % 8 != 0) U(1, 1);
    }

    public void AppendFrom(byte[] bits, int start)
    {
        for (int i = start; i < bits.Length; i++) _bits.Add(bits[i]);
    }

    public byte[] ToBytes()
    {
        int len = (_bits.Count + 7) / 8;
        var o = new byte[len];
        for (int i = 0; i < _bits.Count; i++)
            if (_bits[i] != 0) o[i >> 3] |= (byte)(0x80 >> (i & 7));
        return o;
    }
}

/// <summary>
/// Rebuilds a standards-compliant MPEG-4 elementary stream from Pelco frames.
///
/// The export omits the sequence/VOL header entirely, so nothing downstream
/// knows the frame size or timing. It also differs from stock MPEG-4 in one
/// way that matters: vop_quant is 6 bits wide here rather than the usual 5.
/// Combined with the variable-length modulo_time_base that every MPEG-4 header
/// carries - it holds a 1 on the frame where the recorder's clock ticks over,
/// once a second - a fixed-offset parse lands a bit out of step on those
/// frames. That still decodes without complaint; it just comes out blocky.
/// </summary>
static class Mpeg4
{
    public const int TimeResolution = 15;   // vop_time_increment_resolution

    public static byte[] VolHeader(int width, int height)
    {
        var o = new BitWriter();
        o.U(0x000001B0, 32); o.U(0x03, 8);          // sequence start, Simple @ L3
        o.U(0x000001B5, 32);                        // visual object start
        o.U(0, 1); o.U(1, 4); o.U(0, 1); o.Stuff(); // video, no signal type
        o.U(0x00000100, 32);                        // video object start
        o.U(0x00000120, 32);                        // video object layer start
        o.U(0, 1);                                  // random_accessible_vol
        o.U(1, 8);                                  // simple object type
        o.U(0, 1);                                  // no layer identifier
        o.U(1, 4);                                  // square pixel aspect
        o.U(0, 1);                                  // no vol control parameters
        o.U(0, 2);                                  // rectangular shape
        o.U(1, 1);                                  // marker
        o.U(TimeResolution, 16);
        o.U(1, 1);                                  // marker
        o.U(0, 1);                                  // fixed_vop_rate
        o.U(1, 1); o.U(width, 13); o.U(1, 1); o.U(height, 13); o.U(1, 1);
        o.U(0, 1);                                  // interlaced
        o.U(1, 1);                                  // obmc_disable
        o.U(0, 1);                                  // sprite_enable
        o.U(0, 1);                                  // not_8_bit
        o.U(0, 1);                                  // quant_type: H.263
        o.U(1, 1);                                  // complexity_estimation_disable
        o.U(1, 1);                                  // resync_marker_disable
        o.U(0, 1);                                  // data_partitioned
        o.U(0, 1);                                  // scalability
        o.Stuff();
        return o.ToBytes();
    }

    static byte[] ToBits(byte[] d, int offset)
    {
        var bits = new byte[(d.Length - offset) * 8];
        for (int i = offset, k = 0; i < d.Length; i++)
            for (int b = 7; b >= 0; b--) bits[k++] = (byte)((d[i] >> b) & 1);
        return bits;
    }

    static int Read(byte[] bits, ref int i, int n)
    {
        int v = 0;
        for (int k = 0; k < n; k++) v = (v << 1) | bits[i++];
        return v;
    }

    /// <summary>Rewrite one Pelco frame header as a compliant VOP header.</summary>
    public static byte[] RebuildVop(PelcoFrame f)
    {
        byte[] b = ToBits(f.Vop, 4);       // skip the start code
        int i = 2;                         // past vop_coding_type

        while (b[i] == 1) i++;             // modulo_time_base: run of 1s
        i++;                               // its terminating 0
        i++;                               // marker
        i += 4;                            // vop_time_increment
        i++;                               // marker
        i++;                               // vop_coded

        int rounding = 0;
        if (!f.IsKey) rounding = Read(b, ref i, 1);
        int dcThr = Read(b, ref i, 3);
        int quant = Read(b, ref i, 6);     // 6 bits wide, not the usual 5
        int fcode = 3;
        if (!f.IsKey) fcode = Read(b, ref i, 3);

        var o = new BitWriter();
        o.U(f.IsKey ? 0 : 1, 2);                            // coding type
        o.U(0, 1); o.U(1, 1); o.U(0, 4); o.U(1, 1);         // timing + markers
        o.U(1, 1);                                          // vop_coded
        if (!f.IsKey) o.U(rounding, 1);
        o.U(dcThr, 3);
        o.U(Math.Clamp(quant, 1, 31), 5);
        if (!f.IsKey) o.U(Math.Clamp(fcode, 1, 7), 3);
        o.AppendFrom(b, i);

        byte[] payload = o.ToBytes();
        var vop = new byte[4 + payload.Length];
        vop[0] = 0x00; vop[1] = 0x00; vop[2] = 0x01; vop[3] = 0xB6;
        payload.CopyTo(vop, 4);
        return vop;
    }

    /// <summary>VOL header followed by every frame, as a raw .m4v stream.</summary>
    public static byte[] BuildStream(List<PelcoFrame> frames, int width, int height)
    {
        var ms = new MemoryStream();
        byte[] vol = VolHeader(width, height);
        ms.Write(vol);
        foreach (var f in frames) ms.Write(RebuildVop(f));
        return ms.ToArray();
    }
}
