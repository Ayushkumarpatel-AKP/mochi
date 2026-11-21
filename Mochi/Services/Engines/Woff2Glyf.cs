using System.Buffers.Binary;

namespace Mochi.Services.Engines;

/// <summary>Undoes the WOFF2 "transform" of the glyf, loca and hmtx tables (W3C WOFF2 spec, sections 5.1 to 5.4).</summary>
public static class Woff2Glyf
{
    private sealed class Stream
    {
        private readonly byte[] _d; public int P;
        public Stream(byte[] d, int start) { _d = d; P = start; }
        public byte Byte() => _d[P++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16BigEndian(_d.AsSpan(P)); P += 2; return v; }
        public short I16() { var v = BinaryPrimitives.ReadInt16BigEndian(_d.AsSpan(P)); P += 2; return v; }
        public byte[] Bytes(int n) { var r = _d.AsSpan(P, n).ToArray(); P += n; return r; }

        /// <summary>255UInt16: one byte below 253, else 253 + word, 253 + byte, 506 + byte.</summary>
        public int U255()
        {
            var c = Byte();
            return c switch { 253 => U16(), 255 => 253 + Byte(), 254 => 506 + Byte(), _ => c };
        }
    }

    public static (byte[] Glyf, byte[] Loca) ReconstructGlyf(byte[] t)
    {
        int U32(int o) => (int)BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(o));
        var option = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(2));
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(4));
        int indexFormat = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(6));
        int nContourSz = U32(8), nPointsSz = U32(12), flagSz = U32(16), glyphSz = U32(20), compSz = U32(24), bboxSz = U32(28), instrSz = U32(32);

        var start = 36;
        var contours = new Stream(t, start); start += nContourSz;
        var points = new Stream(t, start); start += nPointsSz;
        var flags = new Stream(t, start); start += flagSz;
        var glyph = new Stream(t, start); start += glyphSz;
        var comp = new Stream(t, start); start += compSz;
        var bbox = new Stream(t, start); start += bboxSz;
        var instr = new Stream(t, start); start += instrSz;
        var overlapOff = start;
        var bboxBitmapLen = (numGlyphs + 31) / 32 * 4;
        var bboxBitmap = t.AsSpan(bbox.P, bboxBitmapLen).ToArray();
        bbox.P += bboxBitmapLen;

        var glyf = new MemoryStream(); var loca = new List<int>();
        for (var i = 0; i < numGlyphs; i++)
        {
            loca.Add((int)glyf.Length);
            var n = contours.I16();
            if (n == 0) continue;
            var hasBox = (bboxBitmap[i >> 3] & (0x80 >> (i & 7))) != 0;
            var w = new BinaryWriter(glyf);

            if (n > 0)
            {
                var endPts = new List<int>(); var total = 0;
                for (var c = 0; c < n; c++) { total += points.U255(); endPts.Add(total - 1); }
                var fl = flags.Bytes(total);
                int x = 0, y = 0; var xs = new int[total]; var ys = new int[total]; var on = new bool[total];
                for (var k = 0; k < total; k++)
                {
                    var (dx, dy) = Triplet(fl[k] & 0x7F, glyph);
                    x += dx; y += dy; xs[k] = x; ys[k] = y; on[k] = (fl[k] >> 7) == 0;
                }
                var insLen = glyph.U255();
                var ins = instr.Bytes(insLen);
                int x0, y0, x1, y1;
                if (hasBox) { x0 = bbox.I16(); y0 = bbox.I16(); x1 = bbox.I16(); y1 = bbox.I16(); }
                else { x0 = xs.Min(); y0 = ys.Min(); x1 = xs.Max(); y1 = ys.Max(); }

                BE(w, n); BE(w, (short)x0); BE(w, (short)y0); BE(w, (short)x1); BE(w, (short)y1);
                foreach (var e in endPts) BE(w, (ushort)e);
                BE(w, (ushort)insLen); w.Write(ins);

                var overlap = (option & 1) != 0 && (t[overlapOff + (i >> 3)] & (0x80 >> (i & 7))) != 0;
                var fo = new List<byte>(); var xb = new List<byte>(); var yb = new List<byte>(); int px = 0, py = 0;
                for (var k = 0; k < total; k++)
                {
                    int dx = xs[k] - px, dy = ys[k] - py; px = xs[k]; py = ys[k];
                    byte f = on[k] ? (byte)1 : (byte)0;
                    if (k == 0 && overlap) f |= 0x40;
                    if (dx == 0) f |= 0x10;
                    else if (Math.Abs(dx) < 256) { f |= 0x02; if (dx > 0) f |= 0x10; xb.Add((byte)Math.Abs(dx)); }
                    else { xb.Add((byte)(dx >> 8)); xb.Add((byte)dx); }
                    if (dy == 0) f |= 0x20;
                    else if (Math.Abs(dy) < 256) { f |= 0x04; if (dy > 0) f |= 0x20; yb.Add((byte)Math.Abs(dy)); }
                    else { yb.Add((byte)(dy >> 8)); yb.Add((byte)dy); }
                    fo.Add(f);
                }
                w.Write(fo.ToArray()); w.Write(xb.ToArray()); w.Write(yb.ToArray());
            }
            else
            {
                // composite glyph: copy the component records as they are
                var begin = comp.P; bool anyInstr = false; ushort cf;
                do
                {
                    cf = comp.U16(); comp.U16();
                    comp.P += (cf & 0x0001) != 0 ? 4 : 2;
                    if ((cf & 0x0008) != 0) comp.P += 2; else if ((cf & 0x0040) != 0) comp.P += 4; else if ((cf & 0x0080) != 0) comp.P += 8;
                    if ((cf & 0x0100) != 0) anyInstr = true;
                } while ((cf & 0x0020) != 0);
                var raw = t.AsSpan(begin, comp.P - begin).ToArray();
                var x0 = bbox.I16(); var y0 = bbox.I16(); var x1 = bbox.I16(); var y1 = bbox.I16();
                BE(w, (short)-1); BE(w, (short)x0); BE(w, (short)y0); BE(w, (short)x1); BE(w, (short)y1);
                w.Write(raw);
                if (anyInstr) { var len = glyph.U255(); BE(w, (ushort)len); w.Write(instr.Bytes(len)); }
            }
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);   // 4-byte aligned, so short loca also works
        }
        loca.Add((int)glyf.Length);

        var lo = new MemoryStream(); var lw = new BinaryWriter(lo);
        foreach (var o in loca)
            if (indexFormat == 0) BE(lw, (ushort)(o / 2)); else BE(lw, o);
        return (glyf.ToArray(), lo.ToArray());
    }

    /// <summary>The hmtx transform leaves out left side bearings that equal each glyph's xMin; put them back.</summary>
    public static byte[] ReconstructHmtx(byte[] t, byte[] hhea, byte[] maxp, byte[] glyf, byte[] loca, int indexFormat)
    {
        int numH = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));
        var flags = t[0]; var p = 1;
        var adv = new ushort[numH];
        for (var i = 0; i < numH; i++) { adv[i] = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p)); p += 2; }

        short XMin(int g)
        {
            int Off(int k) => indexFormat == 0 ? BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(k * 2)) * 2 : (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(k * 4));
            return Off(g + 1) > Off(g) ? BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan(Off(g) + 2)) : (short)0;
        }

        var lsb = new short[numGlyphs];
        for (var i = 0; i < numH; i++)
            if ((flags & 1) == 0) { lsb[i] = BinaryPrimitives.ReadInt16BigEndian(t.AsSpan(p)); p += 2; } else lsb[i] = XMin(i);
        for (var i = numH; i < numGlyphs; i++)
            if ((flags & 2) == 0) { lsb[i] = BinaryPrimitives.ReadInt16BigEndian(t.AsSpan(p)); p += 2; } else lsb[i] = XMin(i);

        var o = new byte[numH * 4 + (numGlyphs - numH) * 2];
        for (var i = 0; i < numH; i++) { BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(i * 4), adv[i]); BinaryPrimitives.WriteInt16BigEndian(o.AsSpan(i * 4 + 2), lsb[i]); }
        for (var i = numH; i < numGlyphs; i++) BinaryPrimitives.WriteInt16BigEndian(o.AsSpan(numH * 4 + (i - numH) * 2), lsb[i]);
        return o;
    }

    /// <summary>One coordinate pair from the glyph stream, using the WOFF2 triplet encoding.</summary>
    private static (int Dx, int Dy) Triplet(int f, Stream s)
    {
        static int Sign(int flag, int v) => (flag & 1) != 0 ? v : -v;
        int b0, b1, b2, b3;
        if (f < 10) { b0 = s.Byte(); return (0, Sign(f, ((f & 14) << 7) + b0)); }
        if (f < 20) { b0 = s.Byte(); return (Sign(f, (((f - 10) & 14) << 7) + b0), 0); }
        if (f < 84)
        {
            var b = f - 20; b1 = s.Byte();
            return (Sign(f, 1 + (b & 0x30) + (b1 >> 4)), Sign(f >> 1, 1 + ((b & 0x0C) << 2) + (b1 & 0x0F)));
        }
        if (f < 120)
        {
            var b = f - 84; b0 = s.Byte(); b1 = s.Byte();
            return (Sign(f, 1 + ((b / 12) << 8) + b0), Sign(f >> 1, 1 + (((b % 12) >> 2) << 8) + b1));
        }
        if (f < 124)
        {
            b0 = s.Byte(); b1 = s.Byte(); b2 = s.Byte();
            return (Sign(f, (b0 << 4) + (b1 >> 4)), Sign(f >> 1, ((b1 & 0x0F) << 8) + b2));
        }
        b0 = s.Byte(); b1 = s.Byte(); b2 = s.Byte(); b3 = s.Byte();
        return (Sign(f, (b0 << 8) + b1), Sign(f >> 1, (b2 << 8) + b3));
    }

    private static void BE(BinaryWriter w, short v) { var b = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); w.Write(b); }
    private static void BE(BinaryWriter w, ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); w.Write(b); }
    private static void BE(BinaryWriter w, int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); w.Write(b); }
}
