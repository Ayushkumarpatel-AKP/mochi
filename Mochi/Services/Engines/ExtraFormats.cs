using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;

namespace Mochi.Services.Engines;

/// <summary>macOS icons: pictures to ICNS (PNG-compressed sizes 16 to 1024) and ICNS back to the largest PNG inside.</summary>
public sealed class IcnsEngine : EngineBase
{
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
    private static readonly (string Type, int Size)[] Sizes =
        { ("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024) };

    public override string Id => "icns";
    public override string Name => "ICNS (built-in)";
    public override string Category => "Image";
    public override string Description => "macOS app icons: PNG, JPG and other pictures to ICNS (16 to 1024 px), and ICNS to PNG.";
    public override string Formats => "PNG JPG WEBP BMP GIF ↔ ICNS";

    public override bool CanHandle(string e, Preset p) =>
        p.Mode == "" && ((e == ".icns" && p.Extension == ".png") || (Is(e, Img) && p.Extension == ".icns"));

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, p.Extension);
        if (p.Extension == ".png") { File.WriteAllBytes(dest, LargestPng(File.ReadAllBytes(input))); return new[] { dest }; }

        using var src = SKBitmap.Decode(input) ?? throw new InvalidDataException("Cannot decode picture");
        var body = new MemoryStream();
        foreach (var (type, size) in Sizes)
        {
            using var sq = new SKBitmap(size, size);
            using (var c = new SKCanvas(sq))
            {
                c.Clear(SKColors.Transparent);
                var k = Math.Min((float)size / src.Width, (float)size / src.Height);   // keep the aspect ratio, centre it
                float w = src.Width * k, h = src.Height * k;
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                c.DrawBitmap(src, new SKRect((size - w) / 2, (size - h) / 2, (size + w) / 2, (size + h) / 2), paint);
            }
            using var png = SKImage.FromBitmap(sq).Encode(SKEncodedImageFormat.Png, 100);
            var data = png.ToArray();
            body.Write(Encoding.ASCII.GetBytes(type));
            body.Write(BE(data.Length + 8));
            body.Write(data);
        }
        using var fs = File.Create(dest);
        fs.Write(Encoding.ASCII.GetBytes("icns"));
        fs.Write(BE((int)body.Length + 8));
        body.WriteTo(fs);
        return new[] { dest };
    }

    private static byte[] BE(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }

    private static byte[] LargestPng(byte[] b)
    {
        if (b.Length < 8 || Encoding.ASCII.GetString(b, 0, 4) != "icns") throw new InvalidDataException("Not an ICNS file");
        byte[]? best = null;
        for (var p = 8; p + 8 <= b.Length;)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 4));
            if (len < 8 || p + len > b.Length) break;
            var data = b.AsSpan(p + 8, len - 8);
            if (data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G' && (best is null || data.Length > best.Length))
                best = data.ToArray();
            p += len;
        }
        return best ?? throw new NotSupportedException("This ICNS only has old-style bitmaps (no PNG sizes)");
    }
}

/// <summary>JPEG XR (.jxr, .wdp, .hdp) pictures, such as Windows HDR screenshots, through Windows' own imaging codec.</summary>
public sealed class WicEngine : EngineBase
{
    private const string PowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    public override string Id => "wic";
    public override string Name => "JPEG XR (Windows codec)";
    public override string Category => "Image";
    public override string Description => "JPEG XR pictures (.jxr, .wdp, .hdp), such as HDR screenshots of Windows, to PNG with Windows' own codec. HDR is clamped to standard colours.";
    public override string Formats => "JXR WDP HDP → PNG (→ JPG WEBP PDF …)";
    public override bool Ready => File.Exists(PowerShell);

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && Is(e, ".jxr", ".wdp", ".hdp") && p.Extension == ".png";

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, ".png");
        var tmp = Util.TempDir();
        try
        {
            var script = Path.Combine(tmp, "jxr.ps1");
            File.WriteAllText(script, """
                param($In, $Out)
                Add-Type -AssemblyName PresentationCore
                $dec = [System.Windows.Media.Imaging.BitmapDecoder]::Create([Uri]$In, 'PreservePixelFormat', 'OnLoad')
                $f = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($dec.Frames[0], [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
                $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
                $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($f))
                $fs = [IO.File]::Create($Out); $enc.Save($fs); $fs.Close()
                """);
            Proc.Run(PowerShell, new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", script, "-In", Path.GetFullPath(input), "-Out", dest });
            return new[] { dest };
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
    }
}

/// <summary>OFD (the Chinese fixed-layout document format) to PDF and text: text, pictures and vector paths.</summary>
public sealed class OfdEngine : EngineBase
{
    public override string Id => "ofd";
    public override string Name => "OFD (built-in)";
    public override string Category => "Pdf";
    public override string Description => "OFD documents to PDF and text. Text, pictures and simple vector paths are drawn; signatures and annotations are skipped.";
    public override string Formats => "OFD → PDF TXT (→ PNG DOCX …)";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && e == ".ofd" && Is(p.Extension, ".pdf", ".txt");

    private const float Pt = 72f / 25.4f;   // OFD lengths are millimetres

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        using var zip = ZipFile.OpenRead(input);
        var doc = new Package(zip);
        var dest = Util.Out(input, outDir, p.Extension);
        if (p.Extension == ".txt") { Util.WriteText(dest, string.Join("\n\n", doc.Pages.Select(doc.PageText))); return new[] { dest }; }

        using var fs = File.Create(dest);
        using var pdf = SKDocument.CreatePdf(fs);
        foreach (var page in doc.Pages)
        {
            var box = doc.PageBox(page);
            using var canvas = pdf.BeginPage(box.W * Pt, box.H * Pt);
            canvas.Scale(Pt);
            doc.Draw(canvas, page);
            pdf.EndPage();
        }
        pdf.Close();
        return new[] { dest };
    }

    private sealed class Package
    {
        private readonly ZipArchive _zip;
        private readonly string _docDir;
        private readonly XElement _document;
        private readonly Dictionary<string, string> _fonts = new();
        private readonly Dictionary<string, string> _media = new();
        public List<string> Pages { get; } = new();

        public Package(ZipArchive zip)
        {
            _zip = zip;
            var root = Load("OFD.xml") ?? throw new InvalidDataException("Not an OFD file");
            var docRoot = root.Descendants().First(x => x.Name.LocalName == "DocRoot").Value.Trim().TrimStart('/');
            _docDir = docRoot.Contains('/') ? docRoot[..(docRoot.LastIndexOf('/') + 1)] : "";
            _document = Load(docRoot)!;

            foreach (var res in _document.Descendants().Where(x => x.Name.LocalName is "PublicRes" or "DocumentRes"))
            {
                var xml = Load(_docDir + res.Value.Trim());
                if (xml is null) continue;
                var baseLoc = xml.Attribute("BaseLoc")?.Value ?? "";
                foreach (var f in xml.Descendants().Where(x => x.Name.LocalName == "Font"))
                    _fonts[f.Attribute("ID")!.Value] = f.Attribute("FamilyName")?.Value ?? f.Attribute("FontName")?.Value ?? "";
                foreach (var m in xml.Descendants().Where(x => x.Name.LocalName == "MultiMedia"))
                    _media[m.Attribute("ID")!.Value] = (_docDir + (baseLoc.Length > 0 ? baseLoc.TrimEnd('/') + "/" : "") + m.Elements().First(x => x.Name.LocalName == "MediaFile").Value.Trim()).Replace("//", "/");
            }
            foreach (var pg in _document.Descendants().Where(x => x.Name.LocalName == "Page" && x.Attribute("BaseLoc") is not null))
                Pages.Add(_docDir + pg.Attribute("BaseLoc")!.Value.TrimStart('/'));
        }

        private XElement? Load(string path)
        {
            var e = _zip.GetEntry(path.TrimStart('/'));
            if (e is null) return null;
            using var s = e.Open();
            return XDocument.Load(s).Root;
        }

        public (float W, float H) PageBox(string page)
        {
            var box = Load(page)?.Descendants().FirstOrDefault(x => x.Name.LocalName == "PhysicalBox")?.Value
                      ?? _document.Descendants().First(x => x.Name.LocalName == "PhysicalBox").Value;
            var v = Nums(box);
            return (v[2], v[3]);
        }

        private static float[] Nums(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();

        private static IEnumerable<XElement> Objects(XElement? page) =>
            page?.Descendants().Where(x => x.Name.LocalName is "TextObject" or "ImageObject" or "PathObject") ?? Enumerable.Empty<XElement>();

        public string PageText(string page) =>
            string.Join("\n", Objects(Load(page)).Where(x => x.Name.LocalName == "TextObject").Select(x => string.Concat(x.Descendants().Where(c => c.Name.LocalName == "TextCode").Select(c => c.Value))));

        private static SKColor Color(XElement? e, SKColor fallback)
        {
            var v = e?.Attribute("Value")?.Value;
            if (v is null) return fallback;
            var n = Nums(v.Replace("#", ""));
            return n.Length >= 3 ? new SKColor((byte)n[0], (byte)n[1], (byte)n[2]) : fallback;
        }

        public void Draw(SKCanvas canvas, string pagePath)
        {
            foreach (var o in Objects(Load(pagePath)))
            {
                var b = Nums(o.Attribute("Boundary")?.Value ?? "0 0 0 0");
                switch (o.Name.LocalName)
                {
                    case "TextObject": DrawText(canvas, o, b); break;
                    case "ImageObject": DrawImage(canvas, o, b); break;
                    case "PathObject": DrawPath(canvas, o, b); break;
                }
            }
        }

        private void DrawText(SKCanvas canvas, XElement o, float[] b)
        {
            var size = float.Parse(o.Attribute("Size")?.Value ?? "3.5", CultureInfo.InvariantCulture);
            var fam = _fonts.GetValueOrDefault(o.Attribute("Font")?.Value ?? "", "");
            var tf = SKTypeface.FromFamilyName(string.IsNullOrEmpty(fam) ? "Microsoft YaHei" : fam);
            using var paint = new SKPaint { Typeface = tf, TextSize = size, IsAntialias = true, Color = Color(o.Elements().FirstOrDefault(x => x.Name.LocalName == "FillColor"), SKColors.Black) };
            if (tf.GlyphCount < 100 || !tf.ContainsGlyphs("中")) paint.Typeface = SKTypeface.FromFamilyName("Microsoft YaHei");

            foreach (var tc in o.Elements().Where(x => x.Name.LocalName == "TextCode"))
            {
                float x = b[0] + float.Parse(tc.Attribute("X")?.Value ?? "0", CultureInfo.InvariantCulture);
                float y = b[1] + float.Parse(tc.Attribute("Y")?.Value ?? "0", CultureInfo.InvariantCulture);
                var text = tc.Value;
                var dx = Deltas(tc.Attribute("DeltaX")?.Value, text.Length);
                if (dx is null) canvas.DrawText(text, x, y, paint);
                else
                    for (var i = 0; i < text.Length; i++)
                    {
                        canvas.DrawText(text[i].ToString(), x, y, paint);
                        x += dx.Count == 0 ? 0 : dx[Math.Min(i, dx.Count - 1)];   // a short list repeats its last value
                    }
            }
        }

        /// <summary>DeltaX is a list of numbers, where "g n v" means n copies of v.</summary>
        private static List<float>? Deltas(string? s, int count)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); var res = new List<float>();
            for (var i = 0; i < t.Length; i++)
                if (t[i] == "g" && i + 2 < t.Length) { for (var k = 0; k < int.Parse(t[i + 1]); k++) res.Add(float.Parse(t[i + 2], CultureInfo.InvariantCulture)); i += 2; }
                else res.Add(float.Parse(t[i], CultureInfo.InvariantCulture));
            return res;
        }

        private void DrawImage(SKCanvas canvas, XElement o, float[] b)
        {
            if (!_media.TryGetValue(o.Attribute("ResourceID")?.Value ?? "", out var path)) return;
            var e = _zip.GetEntry(path.TrimStart('/'));
            if (e is null) return;
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            using var bmp = SKBitmap.Decode(ms.ToArray());
            if (bmp is null) return;   // JBIG2 and other rare codecs are not supported
            canvas.DrawBitmap(bmp, new SKRect(b[0], b[1], b[0] + b[2], b[1] + b[3]));
        }

        private static void DrawPath(SKCanvas canvas, XElement o, float[] b)
        {
            var data = o.Elements().FirstOrDefault(x => x.Name.LocalName == "AbbreviatedData")?.Value;
            if (data is null) return;
            var path = new SKPath(); var t = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            float F(int i) => float.Parse(t[i], CultureInfo.InvariantCulture);
            for (var i = 0; i < t.Length;)
                switch (t[i])
                {
                    case "S" or "M": path.MoveTo(b[0] + F(i + 1), b[1] + F(i + 2)); i += 3; break;
                    case "L": path.LineTo(b[0] + F(i + 1), b[1] + F(i + 2)); i += 3; break;
                    case "Q": path.QuadTo(b[0] + F(i + 1), b[1] + F(i + 2), b[0] + F(i + 3), b[1] + F(i + 4)); i += 5; break;
                    case "B": path.CubicTo(b[0] + F(i + 1), b[1] + F(i + 2), b[0] + F(i + 3), b[1] + F(i + 4), b[0] + F(i + 5), b[1] + F(i + 6)); i += 7; break;
                    case "C": path.Close(); i++; break;
                    case "A": i += 8; break;   // arcs are rare in practice
                    default: i++; break;
                }
            if (!string.Equals(o.Attribute("Fill")?.Value, "false", StringComparison.OrdinalIgnoreCase) && o.Attribute("Fill") is not null)
            {
                using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = Color(o.Elements().FirstOrDefault(x => x.Name.LocalName == "FillColor"), SKColors.Black) };
                canvas.DrawPath(path, fill);
            }
            if (!string.Equals(o.Attribute("Stroke")?.Value, "false", StringComparison.OrdinalIgnoreCase))
            {
                using var line = new SKPaint
                {
                    Style = SKPaintStyle.Stroke, IsAntialias = true,
                    StrokeWidth = float.Parse(o.Attribute("LineWidth")?.Value ?? "0.353", CultureInfo.InvariantCulture),
                    Color = Color(o.Elements().FirstOrDefault(x => x.Name.LocalName == "StrokeColor"), SKColors.Black),
                };
                canvas.DrawPath(path, line);
            }
        }
    }
}

/// <summary>Windows bitmap fonts (.fon, .fnt) turned into TrueType: every set pixel becomes a square, so sizes stay crisp.</summary>
public static class BitmapFont
{
    private sealed record Glyph(int Advance, bool[,] Pixels);   // Pixels[x, y], y from the top

    public static byte[] ToTrueType(byte[] file)
    {
        var fnt = FirstFnt(file);
        int U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(fnt.AsSpan(o));
        long U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(fnt.AsSpan(o));

        var version = U16(0);
        int points = U16(68), ascent = U16(74), extLead = U16(78), weight = U16(83), height = U16(88);
        bool italic = fnt[80] != 0;
        int charset = fnt[85], first = fnt[95], last = fnt[96], def = fnt[97];
        int faceOff = (int)U32(105);
        var face = faceOff > 0 && faceOff < fnt.Length ? ReadZ(fnt, faceOff) : "Bitmap Font";
        var count = last - first + 2;                               // plus the default glyph
        var tableOff = version >= 0x300 ? 148 : 118;
        var entry = version >= 0x300 ? 6 : 4;

        var glyphs = new List<Glyph>();
        for (var i = 0; i < count; i++)
        {
            var e = tableOff + i * entry;
            int w = U16(e);
            var off = version >= 0x300 ? (int)U32(e + 2) : U16(e + 2);
            var px = new bool[Math.Max(w, 1), Math.Max(height, 1)];
            var strips = (w + 7) / 8;
            for (var s = 0; s < strips; s++)
                for (var y = 0; y < height; y++)
                {
                    var idx = off + s * height + y;
                    if (idx >= fnt.Length) continue;
                    var b = fnt[idx];
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var x = s * 8 + bit;
                        if (x < w && (b & (0x80 >> bit)) != 0) px[x, y] = true;
                    }
                }
            glyphs.Add(new Glyph(w, px));
        }

        // glyph 0 is .notdef (the font's default character), then one glyph per character code
        var order = new List<(int Code, Glyph G)> { (-1, glyphs[Math.Clamp(def - first, 0, count - 1)]) };
        for (var c = first; c <= last; c++) order.Add((c, glyphs[c - first]));

        const int S = 64;   // font units per pixel
        var enc = CodePage(charset);
        var cmap = order.Where(o => o.Code >= 0)
            .Select((o, i) => (Uni: (int)enc.GetString(new[] { (byte)o.Code })[0], Gid: order.IndexOf(o)))
            .Where(x => x.Uni != 0xFFFD).GroupBy(x => x.Uni).Select(g => g.First()).OrderBy(x => x.Uni).ToList();

        var glyf = new MemoryStream(); var loca = new List<int>(); var hmtx = new List<(int Adv, int Lsb)>();
        int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue, maxPts = 0, maxCont = 0, maxAdv = 0;
        foreach (var (_, g) in order)
        {
            loca.Add((int)glyf.Length);
            var rects = new List<(int X0, int X1, int Y0, int Y1)>();
            for (var y = 0; y < height; y++)
                for (var x = 0; x < g.Pixels.GetLength(0);)
                {
                    if (!g.Pixels[x, y]) { x++; continue; }
                    var x0 = x; while (x < g.Pixels.GetLength(0) && g.Pixels[x, y]) x++;
                    rects.Add((x0 * S, x * S, (ascent - y - 1) * S, (ascent - y) * S));
                }
            var adv = g.Advance * S; maxAdv = Math.Max(maxAdv, adv);
            if (rects.Count == 0) { hmtx.Add((adv, 0)); continue; }

            var w = new BinaryWriter(glyf);
            int gx0 = rects.Min(r => r.X0), gx1 = rects.Max(r => r.X1), gy0 = rects.Min(r => r.Y0), gy1 = rects.Max(r => r.Y1);
            xMin = Math.Min(xMin, gx0); xMax = Math.Max(xMax, gx1); yMin = Math.Min(yMin, gy0); yMax = Math.Max(yMax, gy1);
            maxPts = Math.Max(maxPts, rects.Count * 4); maxCont = Math.Max(maxCont, rects.Count);
            WriteBE(w, (short)rects.Count); WriteBE(w, (short)gx0); WriteBE(w, (short)gy0); WriteBE(w, (short)gx1); WriteBE(w, (short)gy1);
            for (var i = 0; i < rects.Count; i++) WriteBE(w, (ushort)(i * 4 + 3));
            WriteBE(w, (ushort)0);                                           // no instructions
            var pts = rects.SelectMany(r => new[] { (r.X0, r.Y0), (r.X0, r.Y1), (r.X1, r.Y1), (r.X1, r.Y0) }).ToList();   // clockwise
            for (var i = 0; i < pts.Count; i++) w.Write((byte)1);              // all on-curve, 16-bit deltas
            int px0 = 0; foreach (var p in pts) { WriteBE(w, (short)(p.Item1 - px0)); px0 = p.Item1; }
            int py0 = 0; foreach (var p in pts) { WriteBE(w, (short)(p.Item2 - py0)); py0 = p.Item2; }
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            hmtx.Add((adv, gx0));
        }
        loca.Add((int)glyf.Length);
        if (xMin == int.MaxValue) { xMin = yMin = xMax = yMax = 0; }

        var upem = Math.Clamp(height * S, 16, 16384);
        var asc = ascent * S; var desc = -(height - ascent) * S;
        var tabs = new List<FontEngine.Table>
        {
            T("head", Head(upem, xMin, yMin, xMax, yMax, weight >= 700, italic, height)),
            T("hhea", Hhea(asc, desc, extLead * S, maxAdv, order.Count)),
            T("maxp", Maxp(order.Count, maxPts, maxCont)),
            T("hmtx", Hmtx(hmtx)),
            T("cmap", Cmap(cmap)),
            T("name", Name(face, weight >= 700, italic)),
            T("OS/2", Os2(weight, asc, desc, cmap.First().Uni, cmap.Last().Uni, def)),
            T("post", Post()),
            T("glyf", glyf.ToArray()),
            T("loca", Loca(loca)),
        };
        return FontEngine.BuildSfnt(new byte[] { 0, 1, 0, 0 }, tabs);
    }

    private static FontEngine.Table T(string tag, byte[] data) => new(BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(tag)), tag, data);

    private static void WriteBE(BinaryWriter w, short v) { var b = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); w.Write(b); }
    private static void WriteBE(BinaryWriter w, ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); w.Write(b); }
    private static void WriteBE(BinaryWriter w, int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); w.Write(b); }
    private static void WriteBE(BinaryWriter w, uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); w.Write(b); }

    private static string ReadZ(byte[] b, int o) { var e = Array.IndexOf(b, (byte)0, o); return Encoding.Latin1.GetString(b, o, (e < 0 ? b.Length : e) - o); }

    private static Encoding CodePage(int charset)
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return System.Text.Encoding.GetEncoding(charset switch
            {
                255 => 437, 2 => 1252, 128 => 932, 129 => 949, 134 => 936, 136 => 950, 161 => 1253, 162 => 1254, 177 => 1255, 178 => 1256,
                186 => 1257, 204 => 1251, 238 => 1250, _ => 1252,
            });
        }
        catch { return System.Text.Encoding.Latin1; }
    }

    private static byte[] Head(int upem, int x0, int y0, int x1, int y1, bool bold, bool italic, int ppem)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        WriteBE(w, 0x00010000); WriteBE(w, 0x00010000); WriteBE(w, 0u); WriteBE(w, 0x5F0F3CF5u);
        WriteBE(w, (ushort)0x000B); WriteBE(w, (ushort)upem);
        var now = (long)(DateTime.UtcNow - new DateTime(1904, 1, 1)).TotalSeconds;   // LONGDATETIME: seconds since 1904
        for (var i = 0; i < 2; i++) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, now); w.Write(b); }
        WriteBE(w, (short)x0); WriteBE(w, (short)y0); WriteBE(w, (short)x1); WriteBE(w, (short)y1);
        WriteBE(w, (ushort)((bold ? 1 : 0) | (italic ? 2 : 0))); WriteBE(w, (ushort)ppem); WriteBE(w, (short)2);
        WriteBE(w, (short)1); WriteBE(w, (short)0);                    // long loca, glyph data format
        return m.ToArray();
    }

    private static byte[] Hhea(int asc, int desc, int gap, int maxAdv, int n)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        WriteBE(w, 0x00010000); WriteBE(w, (short)asc); WriteBE(w, (short)desc); WriteBE(w, (short)gap); WriteBE(w, (ushort)maxAdv);
        WriteBE(w, (short)0); WriteBE(w, (short)0); WriteBE(w, (short)maxAdv); WriteBE(w, (short)1); WriteBE(w, (short)0); WriteBE(w, (short)0);
        w.Write(new byte[8]); WriteBE(w, (short)0); WriteBE(w, (ushort)n);
        return m.ToArray();
    }

    private static byte[] Maxp(int n, int pts, int cont)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        WriteBE(w, 0x00010000); WriteBE(w, (ushort)n); WriteBE(w, (ushort)pts); WriteBE(w, (ushort)cont);
        WriteBE(w, (ushort)0); WriteBE(w, (ushort)0); WriteBE(w, (ushort)1);
        for (var i = 0; i < 8; i++) WriteBE(w, (ushort)0);
        return m.ToArray();
    }

    private static byte[] Hmtx(List<(int Adv, int Lsb)> h)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        foreach (var (a, l) in h) { WriteBE(w, (ushort)a); WriteBE(w, (short)l); }
        return m.ToArray();
    }

    private static byte[] Loca(List<int> l)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        foreach (var o in l) WriteBE(w, o);
        return m.ToArray();
    }

    /// <summary>cmap format 4, one segment per run of consecutive codes with consecutive glyphs.</summary>
    private static byte[] Cmap(List<(int Uni, int Gid)> map)
    {
        var segs = new List<(int Start, int End, int Delta)>();
        foreach (var (u, g) in map)
        {
            if (u > 0xFFFE) continue;
            if (segs.Count > 0 && segs[^1].End + 1 == u && segs[^1].Delta == g - u) { segs[^1] = (segs[^1].Start, u, segs[^1].Delta); continue; }
            segs.Add((u, u, g - u));
        }
        segs.Add((0xFFFF, 0xFFFF, 1));
        var n = segs.Count;
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        WriteBE(w, (ushort)0); WriteBE(w, (ushort)1); WriteBE(w, (ushort)3); WriteBE(w, (ushort)1); WriteBE(w, 12u);
        var len = 16 + n * 8;
        int pow = 1, log = 0; while (pow * 2 <= n) { pow *= 2; log++; }
        WriteBE(w, (ushort)4); WriteBE(w, (ushort)len); WriteBE(w, (ushort)0); WriteBE(w, (ushort)(n * 2)); WriteBE(w, (ushort)(pow * 2)); WriteBE(w, (ushort)log); WriteBE(w, (ushort)(n * 2 - pow * 2));
        foreach (var s in segs) WriteBE(w, (ushort)s.End);
        WriteBE(w, (ushort)0);
        foreach (var s in segs) WriteBE(w, (ushort)s.Start);
        foreach (var s in segs) WriteBE(w, (ushort)(s.Delta & 0xFFFF));
        foreach (var _ in segs) WriteBE(w, (ushort)0);
        return m.ToArray();
    }

    private static byte[] Name(string face, bool bold, bool italic)
    {
        var style = bold && italic ? "Bold Italic" : bold ? "Bold" : italic ? "Italic" : "Regular";
        var names = new (int Id, string Text)[] { (1, face), (2, style), (4, face + " " + style), (6, face.Replace(" ", "") + "-" + style.Replace(" ", "")) };
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        WriteBE(w, (ushort)0); WriteBE(w, (ushort)names.Length); WriteBE(w, (ushort)(6 + 12 * names.Length));
        var off = 0;
        foreach (var (id, text) in names)
        {
            var len = System.Text.Encoding.BigEndianUnicode.GetByteCount(text);
            WriteBE(w, (ushort)3); WriteBE(w, (ushort)1); WriteBE(w, (ushort)0x409); WriteBE(w, (ushort)id); WriteBE(w, (ushort)len); WriteBE(w, (ushort)off);
            off += len;
        }
        foreach (var (_, text) in names) w.Write(System.Text.Encoding.BigEndianUnicode.GetBytes(text));
        return m.ToArray();
    }

    private static byte[] Os2(int weight, int asc, int desc, int firstUni, int lastUni, int def)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        WriteBE(w, (ushort)3); WriteBE(w, (short)0); WriteBE(w, (ushort)(weight is > 0 and < 1000 ? weight : 400)); WriteBE(w, (ushort)5); WriteBE(w, (ushort)0);
        for (var i = 0; i < 12; i++) WriteBE(w, (short)0);             // subscript, superscript, strikeout, family class
        w.Write(new byte[10]); w.Write(new byte[16]);                  // panose, unicode ranges
        w.Write(Encoding_ASCII("FILE")); WriteBE(w, (ushort)0x40);
        WriteBE(w, (ushort)Math.Min(firstUni, 0xFFFF)); WriteBE(w, (ushort)Math.Min(lastUni, 0xFFFF));
        WriteBE(w, (short)asc); WriteBE(w, (short)desc); WriteBE(w, (short)0); WriteBE(w, (ushort)asc); WriteBE(w, (ushort)-desc);
        WriteBE(w, 1u); WriteBE(w, 0u); WriteBE(w, (short)0); WriteBE(w, (short)0); WriteBE(w, (ushort)def); WriteBE(w, (ushort)32); WriteBE(w, (ushort)0);
        return m.ToArray();
    }

    private static byte[] Encoding_ASCII(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    private static byte[] Post()
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        WriteBE(w, 0x00030000); WriteBE(w, 0); WriteBE(w, (short)-64); WriteBE(w, (short)32); WriteBE(w, 1u);
        w.Write(new byte[16]);
        return m.ToArray();
    }

    /// <summary>The first FNT resource of a 16-bit NE .fon, or the file itself when it is a bare .fnt.</summary>
    private static byte[] FirstFnt(byte[] f)
    {
        if (f.Length > 2 && f[0] == 0x00 && (f[1] == 0x02 || f[1] == 0x03)) return f;               // raw .fnt
        if (f.Length < 0x40 || f[0] != 'M' || f[1] != 'Z') throw new InvalidDataException("Not a Windows font file");
        var ne = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(0x3C));
        if (f[ne] != 'N' || f[ne + 1] != 'E') throw new NotSupportedException("Only 16-bit .fon files (NE) are supported");
        var p = ne + BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(ne + 0x24));
        var shift = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p)); p += 2;
        while (true)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p));
            if (type == 0) break;
            var n = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p + 2)); p += 8;
            for (var i = 0; i < n; i++, p += 12)
            {
                if (type != 0x8008) continue;                                                       // RT_FONT
                var off = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p)) << shift;
                var len = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p + 2)) << shift;
                return f.AsSpan(off, len).ToArray();
            }
        }
        throw new InvalidDataException("No bitmap font found in this file");
    }
}
