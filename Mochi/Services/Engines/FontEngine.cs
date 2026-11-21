using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Mochi.Services.Engines;

/// <summary>TTF, OTF, WOFF, WOFF2 and EOT into each other. WOFF2 is written with untransformed tables; reading it needs the same.</summary>
public sealed class FontEngine : EngineBase
{
    private static readonly string[] Fmt = { ".ttf", ".otf", ".woff", ".woff2", ".eot" };

    public override string Id => "font";
    public override string Name => "Fonts (built-in)";
    public override string Category => "Font";
    public override string Description => "TTF, OTF, WOFF, WOFF2 and EOT into each other, and Windows bitmap fonts (FON) into any of them.";
    public override string Formats => "TTF OTF WOFF WOFF2 EOT ↔ each other · FON → TTF WOFF WOFF2 EOT";

    public override bool CanHandle(string e, Preset p) =>
        p.Mode == "" && ((Is(e, Fmt) && Is(p.Extension, Fmt) && Norm(e) != Norm(p.Extension))
                         || (e == ".fon" && Is(p.Extension, ".ttf", ".woff", ".woff2", ".eot")));

    private static string Norm(string e) => e is ".ttf" or ".otf" ? ".sfnt" : e;

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var sfnt = ToSfnt(Util.Ext(input), File.ReadAllBytes(input));
        var ext = p.Extension;
        if (ext is ".ttf" or ".otf") ext = Encoding.ASCII.GetString(sfnt, 0, 4) == "OTTO" ? ".otf" : ".ttf";   // CFF outlines belong in .otf
        var dest = Util.Out(input, outDir, ext);
        File.WriteAllBytes(dest, ext switch { ".woff" => ToWoff(sfnt), ".woff2" => ToWoff2(sfnt), ".eot" => ToEot(sfnt), _ => sfnt });
        return new[] { dest };
    }

    private static byte[] ToSfnt(string ext, byte[] b) => ext switch
    {
        ".woff" => FromWoff(b),
        ".woff2" => FromWoff2(b),
        ".eot" => FromEot(b),
        ".fon" => BitmapFont.ToTrueType(b),
        _ => b,
    };

    private static int Pad4(int n) => (n + 3) & ~3;
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));

    internal sealed record Table(uint Tag, string Name, byte[] Data);

    private static List<Table> Tables(byte[] f)
    {
        int n = U16(f, 4);
        var res = new List<Table>();
        for (var i = 0; i < n; i++)
        {
            var d = 12 + 16 * i;
            res.Add(new Table(U32(f, d), Encoding.ASCII.GetString(f, d, 4), f.AsSpan((int)U32(f, d + 8), (int)U32(f, d + 12)).ToArray()));
        }
        return res;
    }

    private static uint Sum(byte[] data, bool zeroAdjust)
    {
        uint s = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint v = 0;
            for (var k = 0; k < 4; k++) v = (v << 8) | (i + k < data.Length && !(zeroAdjust && i + k >= 8 && i + k < 12) ? data[i + k] : (byte)0);
            s += v;
        }
        return s;
    }

    /// <summary>Lays tables out as a plain sfnt (TTF/OTF) with a sorted directory.</summary>
    internal static byte[] BuildSfnt(byte[] flavor, List<Table> tabs)
    {
        tabs = tabs.OrderBy(t => t.Tag).ToList();
        var n = tabs.Count;
        var o = new byte[12 + 16 * n + tabs.Sum(t => Pad4(t.Data.Length))];
        flavor.CopyTo(o, 0);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(4), (ushort)n);
        int pow = 1, log = 0;
        while (pow * 2 <= n) { pow *= 2; log++; }
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(6), (ushort)(pow * 16));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(10), (ushort)(n * 16 - pow * 16));
        var off = 12 + 16 * n;
        for (var i = 0; i < n; i++)
        {
            var d = 12 + 16 * i;
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d), tabs[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d + 4), Sum(tabs[i].Data, tabs[i].Name == "head"));
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d + 8), (uint)off);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d + 12), (uint)tabs[i].Data.Length);
            tabs[i].Data.CopyTo(o, off);
            off += Pad4(tabs[i].Data.Length);
        }

        // head.checkSumAdjustment makes the whole file add up to 0xB1B0AFBA
        var headIdx = tabs.FindIndex(t => t.Name == "head");
        if (headIdx >= 0)
        {
            var headOff = (int)U32(o, 12 + 16 * headIdx + 8);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(headOff + 8), 0);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(headOff + 8), 0xB1B0AFBAu - Sum(o, false));
        }
        return o;
    }

    private static int SfntSize(List<Table> t) => 12 + 16 * t.Count + t.Sum(x => Pad4(x.Data.Length));

    // ---- WOFF
    private static byte[] ToWoff(byte[] f)
    {
        var tabs = Tables(f);
        var packed = tabs.Select(t =>
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(t.Data);
            var c = ms.ToArray();
            return c.Length < t.Data.Length ? c : t.Data;
        }).ToList();

        var n = tabs.Count; var dirEnd = 44 + 20 * n;
        var o = new byte[dirEnd + packed.Sum(c => Pad4(c.Length))];
        Encoding.ASCII.GetBytes("wOFF").CopyTo(o, 0);
        f.AsSpan(0, 4).CopyTo(o.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(8), (uint)o.Length);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(12), (ushort)n);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(16), (uint)SfntSize(tabs));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(20), 1);
        var off = dirEnd;
        for (var i = 0; i < n; i++)
        {
            var d = 44 + 20 * i;
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d), tabs[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d + 4), (uint)off);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d + 8), (uint)packed[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d + 12), (uint)tabs[i].Data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(d + 16), Sum(tabs[i].Data, tabs[i].Name == "head"));
            packed[i].CopyTo(o, off);
            off += Pad4(packed[i].Length);
        }
        return o;
    }

    private static byte[] FromWoff(byte[] w)
    {
        if (Encoding.ASCII.GetString(w, 0, 4) != "wOFF") throw new InvalidDataException("Not a WOFF font");
        int n = U16(w, 12);
        var tabs = new List<Table>();
        for (var i = 0; i < n; i++)
        {
            var d = 44 + 20 * i;
            int off = (int)U32(w, d + 4), comp = (int)U32(w, d + 8), orig = (int)U32(w, d + 12);
            byte[] data;
            if (comp == orig) data = w.AsSpan(off, orig).ToArray();
            else
            {
                using var z = new ZLibStream(new MemoryStream(w, off, comp), CompressionMode.Decompress);
                data = new byte[orig];
                z.ReadExactly(data);
            }
            tabs.Add(new Table(U32(w, d), Encoding.ASCII.GetString(w, d, 4), data));
        }
        return BuildSfnt(w.AsSpan(4, 4).ToArray(), tabs);
    }

    // ---- WOFF2
    private static readonly string[] Known =
    {
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT", "EBLC",
        "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC",
        "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just",
        "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    };

    private static void Base128(List<byte> o, uint v)
    {
        var parts = new List<byte>();
        do { parts.Insert(0, (byte)(v & 0x7F)); v >>= 7; } while (v > 0);
        for (var i = 0; i < parts.Count - 1; i++) parts[i] |= 0x80;
        o.AddRange(parts);
    }

    private static uint ReadBase128(byte[] b, ref int p)
    {
        uint v = 0;
        for (var i = 0; i < 5; i++)
        {
            var x = b[p++];
            v = (v << 7) | (uint)(x & 0x7F);
            if ((x & 0x80) == 0) return v;
        }
        throw new InvalidDataException("Bad WOFF2 length");
    }

    private static byte[] ToWoff2(byte[] f)
    {
        var tabs = Tables(f).OrderBy(t => t.Tag).ToList();
        var loca = tabs.FirstOrDefault(t => t.Name == "loca");
        if (loca is not null) { tabs.Remove(loca); tabs.Insert(tabs.FindIndex(t => t.Name == "glyf") + 1, loca); }   // loca must follow glyf

        var dir = new List<byte>();
        foreach (var t in tabs)
        {
            var idx = Array.IndexOf(Known, t.Name);
            var nullTransform = t.Name is "glyf" or "loca";   // version 3 = untransformed for these two
            dir.Add((byte)((idx < 0 ? 63 : idx) | (nullTransform ? 3 << 6 : 0)));
            if (idx < 0) dir.AddRange(Encoding.ASCII.GetBytes(t.Name));
            Base128(dir, (uint)t.Data.Length);
        }

        var raw = new MemoryStream();
        foreach (var t in tabs) raw.Write(t.Data);
        using var comp = new MemoryStream();
        using (var br = new BrotliStream(comp, CompressionLevel.SmallestSize, true)) br.Write(raw.ToArray());
        var c = comp.ToArray();

        var o = new byte[Pad4(48 + dir.Count + c.Length)];
        Encoding.ASCII.GetBytes("wOF2").CopyTo(o, 0);
        f.AsSpan(0, 4).CopyTo(o.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(8), (uint)o.Length);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(12), (ushort)tabs.Count);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(16), (uint)SfntSize(tabs));
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(20), (uint)c.Length);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(24), 1);
        dir.CopyTo(o, 48);
        c.CopyTo(o, 48 + dir.Count);
        return o;
    }

    private static byte[] FromWoff2(byte[] w)
    {
        if (Encoding.ASCII.GetString(w, 0, 4) != "wOF2") throw new InvalidDataException("Not a WOFF2 font");
        int n = U16(w, 12); var compLen = (int)U32(w, 20);
        var p = 48;
        var entries = new List<(string name, uint len, bool transformed)>();
        for (var i = 0; i < n; i++)
        {
            var flags = w[p++]; var idx = flags & 0x3F; var ver = flags >> 6;
            var name = idx == 63 ? Encoding.ASCII.GetString(w, p, 4) : Known[idx];
            if (idx == 63) p += 4;
            var orig = ReadBase128(w, ref p);
            var transformed = name is "glyf" or "loca" ? ver == 0 : name == "hmtx" && ver == 1;
            entries.Add((name, transformed ? ReadBase128(w, ref p) : orig, transformed));   // transformed tables store their transformed length
        }

        using var br = new BrotliStream(new MemoryStream(w, p, compLen), CompressionMode.Decompress);
        var data = new Dictionary<string, byte[]>();
        foreach (var (name, len, _) in entries)
        {
            var b = new byte[len];
            br.ReadExactly(b);
            data[name] = b;
        }

        if (entries.Any(e => e.name == "glyf" && e.transformed))
        {
            var (glyf, loca) = Woff2Glyf.ReconstructGlyf(data["glyf"]);
            data["glyf"] = glyf; data["loca"] = loca;
        }
        if (entries.Any(e => e.name == "hmtx" && e.transformed))
        {
            var indexFormat = BinaryPrimitives.ReadInt16BigEndian(data["head"].AsSpan(50));
            data["hmtx"] = Woff2Glyf.ReconstructHmtx(data["hmtx"], data["hhea"], data["maxp"], data["glyf"], data["loca"], indexFormat);
        }

        var tabs = entries.Select(e => new Table(U32(Encoding.ASCII.GetBytes(e.name.PadRight(4)), 0), e.name, data[e.name])).ToList();
        return BuildSfnt(w.AsSpan(4, 4).ToArray(), tabs);
    }

    // ---- EOT (uncompressed, version 0x00020001)
    private static string NameOf(List<Table> tabs, int id)
    {
        var t = tabs.FirstOrDefault(x => x.Name == "name");
        if (t is null) return "";
        int count = U16(t.Data, 2), strOff = U16(t.Data, 4);
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < count; i++)
            {
                var r = 6 + 12 * i;
                int plat = U16(t.Data, r), nid = U16(t.Data, r + 6), len = U16(t.Data, r + 8), off = U16(t.Data, r + 10);
                if (nid != id || (pass == 0 ? plat != 3 : plat != 1)) continue;
                return pass == 0 ? Encoding.BigEndianUnicode.GetString(t.Data, strOff + off, len) : Encoding.ASCII.GetString(t.Data, strOff + off, len);
            }
        return "";
    }

    private static byte[] ToEot(byte[] f)
    {
        var tabs = Tables(f);
        var os2 = tabs.FirstOrDefault(t => t.Name == "OS/2")?.Data;
        var head = tabs.First(t => t.Name == "head").Data;
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0u); w.Write((uint)f.Length); w.Write(0x00020001u); w.Write(0u);
        var panose = new byte[10]; if (os2 is { Length: >= 42 }) Array.Copy(os2, 32, panose, 0, 10);
        w.Write(panose);
        w.Write((byte)1);
        w.Write((byte)(os2 is { Length: >= 64 } && (U16(os2, 62) & 1) != 0 ? 1 : 0));
        w.Write(os2 is { Length: >= 6 } ? (uint)U16(os2, 4) : 400u);
        w.Write(os2 is { Length: >= 10 } ? U16(os2, 8) : (ushort)0);
        w.Write((ushort)0x504C);
        for (var i = 0; i < 4; i++) w.Write(os2 is { Length: >= 58 } ? U32(os2, 42 + 4 * i) : 0u);
        for (var i = 0; i < 2; i++) w.Write(os2 is { Length: >= 86 } ? U32(os2, 78 + 4 * i) : 0u);
        w.Write(U32(head, 8));
        for (var i = 0; i < 4; i++) w.Write(0u);
        foreach (var id in new[] { 1, 2, 5, 4 })
        {
            w.Write((ushort)0);
            var s = Encoding.Unicode.GetBytes(NameOf(tabs, id));
            w.Write((ushort)s.Length); w.Write(s);
        }
        w.Write((ushort)0); w.Write((ushort)0);   // padding + empty root string
        w.Write(f);
        var o = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(0), (uint)o.Length);
        return o;
    }

    private static byte[] FromEot(byte[] e)
    {
        var total = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(0));
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(12));
        if ((flags & 0x4) != 0) throw new NotSupportedException("This EOT is compressed (MicroType Express), which is not supported");
        var data = e.AsSpan((int)total - size, size).ToArray();
        if ((flags & 0x10000000) != 0) for (var i = 0; i < data.Length; i++) data[i] ^= 0x50;
        return data;
    }
}
