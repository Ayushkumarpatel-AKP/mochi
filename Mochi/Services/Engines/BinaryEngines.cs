using System.Buffers.Binary;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using ACadSharp;
using ACadSharp.IO;
using SkiaSharp;

namespace Mochi.Services.Engines;

/// <summary>Extracts ZIP, TAR, GZ built in and RAR, 7Z, ISO, CAB… through 7-Zip; packs ZIP and TAR.GZ built in, 7Z through 7-Zip.</summary>
public sealed class ArchiveEngine : EngineBase
{
    private static readonly string[] Packed = { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso", ".cab", ".jar", ".apk", ".zst" };

    public override string Id => "archive";
    public override string Name => "Archives";
    public override string Category => "Archive";
    public override string Description => "Extracts ZIP, TAR and GZ; with 7-Zip also RAR, 7Z, ISO, CAB and more. Packs files and folders into ZIP, TAR.GZ or 7Z.";
    public override string Formats => "ZIP RAR 7Z TAR GZ ISO CAB → folder · files/folders → ZIP 7Z TAR.GZ";
    public override string? WingetId => "7zip.7zip";

    public override bool CanHandle(string e, Preset p) => p.Mode switch
    {
        "extract" => Is(e, Packed),
        "pack" => p.Extension != ".7z" || Tools.Find("7z") is not null,
        _ => false,
    };

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir) =>
        new[] { p.Mode == "extract" ? Extract(input, outDir) : Pack(input, p.Extension, outDir) };

    private static string Extract(string input, string outDir)
    {
        var e = Util.Ext(input);
        var dest = Util.Out(input, outDir, "");
        Directory.CreateDirectory(dest);
        var name = Path.GetFileName(input).ToLowerInvariant();
        if (e is ".zip" or ".jar" or ".apk") ZipFile.ExtractToDirectory(input, dest);
        else if (e == ".tar") TarFile.ExtractToDirectory(input, dest, true);
        else if ((e == ".tgz" || name.EndsWith(".tar.gz")))
        {
            using var gz = new GZipStream(File.OpenRead(input), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, dest, true);
        }
        else if (e == ".gz")
        {
            using var gz = new GZipStream(File.OpenRead(input), CompressionMode.Decompress);
            using var o = File.Create(Path.Combine(dest, Path.GetFileNameWithoutExtension(input)));
            gz.CopyTo(o);
        }
        else
        {
            var z = Tools.Find("7z") ?? throw new FileNotFoundException("7-Zip is not installed");
            Proc.Run(z, new[] { "x", "-y", "-o" + dest, input });
            // .tar.xz / .tar.bz2: the first pass leaves one tar, open it too
            var inner = Directory.GetFiles(dest, "*.tar");
            if (inner.Length == 1 && Directory.GetFileSystemEntries(dest).Length == 1)
            {
                Proc.Run(z, new[] { "x", "-y", "-o" + dest, inner[0] });
                File.Delete(inner[0]);
            }
        }
        return dest;
    }

    private static string Pack(string input, string ext, string outDir)
    {
        input = input.TrimEnd('\\', '/');
        var isDir = Directory.Exists(input);
        var dest = Util.Out(input, outDir, ext);

        if (ext == ".7z")
        {
            Proc.Run(Tools.Find("7z")!, new[] { "a", "-t7z", "-y", dest, input });
            return dest;
        }
        if (ext == ".zip")
        {
            if (isDir) ZipFile.CreateFromDirectory(input, dest, CompressionLevel.Optimal, true);
            else
            {
                using var z = ZipFile.Open(dest, ZipArchiveMode.Create);
                z.CreateEntryFromFile(input, Path.GetFileName(input), CompressionLevel.Optimal);
            }
            return dest;
        }

        // .tar.gz: a single file is staged in a folder so TarFile can take it
        var src = input; string? stage = null;
        if (!isDir)
        {
            stage = Util.TempDir();
            File.Copy(input, Path.Combine(stage, Path.GetFileName(input)));
            src = stage;
        }
        try
        {
            using var fs = File.Create(dest);
            using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            TarFile.CreateFromDirectory(src, gz, isDir);
        }
        finally { if (stage is not null) try { Directory.Delete(stage, true); } catch { /* temp cleanup */ } }
        return dest;
    }
}

/// <summary>3D triangle meshes: STL, OBJ and PLY into each other, and pictures into relief models (STL).</summary>
public sealed class Model3dEngine : EngineBase
{
    private static readonly string[] Fmt = { ".stl", ".obj", ".ply" };
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public override string Id => "model3d";
    public override string Name => "3D models (built-in)";
    public override string Category => "Model3D";
    public override string Description => "STL, OBJ and PLY into each other, and pictures into 3D relief models for printing: brighter areas stand higher, on a flat base.";
    public override string Formats => "STL OBJ PLY ↔ each other · pictures → STL relief";

    public override bool CanHandle(string e, Preset p) =>
        (p.Mode == "relief" && Is(e, Img) && p.Extension == ".stl") || (p.Mode == "" && Is(e, Fmt) && Is(p.Extension, Fmt) && e != p.Extension);

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var tris = p.Mode == "relief" ? Relief(input) : Read(input);
        var dest = Util.Out(input, outDir, p.Extension);
        switch (p.Extension)
        {
            case ".stl": File.WriteAllBytes(dest, WriteStl(tris)); break;
            case ".obj": Util.WriteText(dest, WriteObj(tris)); break;
            default: Util.WriteText(dest, WritePly(tris)); break;
        }
        return new[] { dest };
    }

    private static List<(float X, float Y, float Z)> Read(string path)
    {
        var e = Util.Ext(path);
        var b = File.ReadAllBytes(path);
        return e switch { ".stl" => ReadStl(b), ".obj" => ReadObj(Encoding.UTF8.GetString(b)), _ => ReadPly(b) };
    }

    private static List<(float, float, float)> ReadStl(byte[] b)
    {
        var res = new List<(float, float, float)>();
        var head = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 512));
        var binaryLen = b.Length >= 84 ? 84L + 50L * BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(80)) : -1;
        if (head.TrimStart().StartsWith("solid") && binaryLen != b.Length)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(Encoding.UTF8.GetString(b), @"vertex\s+(\S+)\s+(\S+)\s+(\S+)"))
                res.Add((F(m.Groups[1].Value), F(m.Groups[2].Value), F(m.Groups[3].Value)));
            return res;
        }
        var n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(80));
        for (var i = 0; i < n; i++)
            for (var v = 0; v < 3; v++)
            {
                var o = 84 + 50 * i + 12 + 12 * v;
                res.Add((BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8)));
            }
        return res;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static List<(float, float, float)> ReadObj(string text)
    {
        var v = new List<(float, float, float)>(); var res = new List<(float, float, float)>();
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            if (t[0] == "v" && t.Length >= 4) v.Add((F(t[1]), F(t[2]), F(t[3])));
            else if (t[0] == "f" && t.Length >= 4)
            {
                var idx = t.Skip(1).Select(x => { var k = int.Parse(x.Split('/')[0]); return k < 0 ? v.Count + k : k - 1; }).ToList();
                for (var i = 1; i + 1 < idx.Count; i++) { res.Add(v[idx[0]]); res.Add(v[idx[i]]); res.Add(v[idx[i + 1]]); }
            }
        }
        return res;
    }

    private static List<(float, float, float)> ReadPly(byte[] b)
    {
        var headEnd = Encoding.ASCII.GetString(b).IndexOf("end_header", StringComparison.Ordinal);
        if (headEnd < 0) throw new InvalidDataException("Not a PLY file");
        var bodyStart = Array.IndexOf(b, (byte)'\n', headEnd) + 1;
        var header = Encoding.ASCII.GetString(b, 0, headEnd).Replace("\r", "").Split('\n');
        var binary = header.Any(l => l.StartsWith("format binary_little_endian"));
        if (header.Any(l => l.StartsWith("format binary_big_endian"))) throw new NotSupportedException("Big-endian PLY is not supported");

        int nv = 0, nf = 0; var vprops = new List<(string type, string name)>(); string listCount = "uchar", listIdx = "int"; var cur = "";
        foreach (var l in header)
        {
            var t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 3 && t[0] == "element") { cur = t[1]; if (cur == "vertex") nv = int.Parse(t[2]); else if (cur == "face") nf = int.Parse(t[2]); }
            else if (t.Length >= 3 && t[0] == "property" && cur == "vertex") vprops.Add((t[1], t[2]));
            else if (t.Length >= 5 && t[0] == "property" && t[1] == "list" && cur == "face") { listCount = t[2]; listIdx = t[3]; }
        }

        var verts = new List<(float, float, float)>(); var faces = new List<int[]>();
        if (!binary)
        {
            var lines = Encoding.ASCII.GetString(b, bodyStart, b.Length - bodyStart).Split('\n');
            var xi = vprops.FindIndex(p => p.name == "x"); var yi = vprops.FindIndex(p => p.name == "y"); var zi = vprops.FindIndex(p => p.name == "z");
            for (var i = 0; i < nv; i++) { var t = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries); verts.Add((F(t[xi]), F(t[yi]), F(t[zi]))); }
            for (var i = 0; i < nf; i++) { var t = lines[nv + i].Split(' ', StringSplitOptions.RemoveEmptyEntries); faces.Add(t.Skip(1).Take(int.Parse(t[0])).Select(int.Parse).ToArray()); }
        }
        else
        {
            static int Size(string t) => t switch { "char" or "uchar" or "int8" or "uint8" => 1, "short" or "ushort" or "int16" or "uint16" => 2, "double" or "float64" => 8, _ => 4 };
            var p = bodyStart;
            for (var i = 0; i < nv; i++)
            {
                float x = 0, y = 0, z = 0;
                foreach (var (type, name) in vprops)
                {
                    var v = type is "float" or "float32" ? BitConverter.ToSingle(b, p) : type is "double" or "float64" ? (float)BitConverter.ToDouble(b, p) : 0f;
                    if (name == "x") x = v; else if (name == "y") y = v; else if (name == "z") z = v;
                    p += Size(type);
                }
                verts.Add((x, y, z));
            }
            int ReadInt(string type) { var v = Size(type) switch { 1 => b[p], 2 => BitConverter.ToUInt16(b, p), _ => BitConverter.ToInt32(b, p) }; p += Size(type); return v; }
            for (var i = 0; i < nf; i++) { var c = ReadInt(listCount); var f = new int[c]; for (var k = 0; k < c; k++) f[k] = ReadInt(listIdx); faces.Add(f); }
        }

        var res = new List<(float, float, float)>();
        foreach (var f in faces)
            for (var i = 1; i + 1 < f.Length; i++) { res.Add(verts[f[0]]); res.Add(verts[f[i]]); res.Add(verts[f[i + 1]]); }
        return res;
    }

    private static byte[] WriteStl(List<(float X, float Y, float Z)> t)
    {
        var n = t.Count / 3;
        var o = new byte[84 + 50 * n];
        Encoding.ASCII.GetBytes("Mochi binary STL").CopyTo(o, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(80), (uint)n);
        for (var i = 0; i < n; i++)
        {
            var a = t[3 * i]; var b = t[3 * i + 1]; var c = t[3 * i + 2];
            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            var l = MathF.Sqrt(nx * nx + ny * ny + nz * nz); if (l > 0) { nx /= l; ny /= l; nz /= l; }
            var p = 84 + 50 * i;
            foreach (var f in new[] { nx, ny, nz, a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z })
            { BitConverter.GetBytes(f).CopyTo(o, p); p += 4; }
        }
        return o;
    }

    private static (List<(float, float, float)> v, List<int> f) Index(List<(float, float, float)> t)
    {
        var map = new Dictionary<(float, float, float), int>(); var v = new List<(float, float, float)>(); var f = new List<int>();
        foreach (var p in t)
        {
            if (!map.TryGetValue(p, out var i)) { i = v.Count; map[p] = i; v.Add(p); }
            f.Add(i);
        }
        return (v, f);
    }

    private static string FS(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);

    private static string WriteObj(List<(float, float, float)> t)
    {
        var (v, f) = Index(t);
        var sb = new StringBuilder("# Mochi\n");
        foreach (var p in v) sb.Append("v ").Append(FS(p.Item1)).Append(' ').Append(FS(p.Item2)).Append(' ').AppendLine(FS(p.Item3));
        for (var i = 0; i < f.Count; i += 3) sb.AppendLine($"f {f[i] + 1} {f[i + 1] + 1} {f[i + 2] + 1}");
        return sb.ToString();
    }

    private static string WritePly(List<(float, float, float)> t)
    {
        var (v, f) = Index(t);
        var sb = new StringBuilder($"ply\nformat ascii 1.0\nelement vertex {v.Count}\nproperty float x\nproperty float y\nproperty float z\nelement face {f.Count / 3}\nproperty list uchar int vertex_indices\nend_header\n");
        foreach (var p in v) sb.Append(FS(p.Item1)).Append(' ').Append(FS(p.Item2)).Append(' ').AppendLine(FS(p.Item3));
        for (var i = 0; i < f.Count; i += 3) sb.AppendLine($"3 {f[i]} {f[i + 1]} {f[i + 2]}");
        return sb.ToString();
    }

    /// <summary>Brightness becomes height; the model is a closed solid on a 1-unit base, 100 units wide.</summary>
    private static List<(float, float, float)> Relief(string path)
    {
        using var src = SKBitmap.Decode(path) ?? throw new InvalidDataException("Cannot read picture");
        var scale = Math.Min(1.0, 160.0 / Math.Max(src.Width, src.Height));
        var w = Math.Max(2, (int)(src.Width * scale)); var h = Math.Max(2, (int)(src.Height * scale));
        using var bmp = src.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
        var unit = 100f / w; var maxH = 15f;
        var z = new float[w, h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                var lum = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f * (c.Alpha / 255f);
                z[x, y] = 1 + lum * maxH;
            }

        var t = new List<(float, float, float)>();
        (float, float, float) P(int x, int y) => (x * unit, (h - 1 - y) * unit, z[x, y]);
        (float, float, float) B(int x, int y) => (x * unit, (h - 1 - y) * unit, 0);
        void Tri(params (float, float, float)[] p) => t.AddRange(p);

        for (var y = 0; y < h - 1; y++)
            for (var x = 0; x < w - 1; x++)
            { Tri(P(x, y), P(x, y + 1), P(x + 1, y)); Tri(P(x + 1, y), P(x, y + 1), P(x + 1, y + 1)); }
        Tri(B(0, 0), B(w - 1, 0), B(0, h - 1)); Tri(B(w - 1, 0), B(w - 1, h - 1), B(0, h - 1));
        for (var x = 0; x < w - 1; x++)   // top and bottom walls
        {
            Tri(P(x, 0), P(x + 1, 0), B(x, 0)); Tri(B(x, 0), P(x + 1, 0), B(x + 1, 0));
            Tri(P(x, h - 1), B(x, h - 1), P(x + 1, h - 1)); Tri(B(x, h - 1), B(x + 1, h - 1), P(x + 1, h - 1));
        }
        for (var y = 0; y < h - 1; y++)   // left and right walls
        {
            Tri(P(0, y), B(0, y), P(0, y + 1)); Tri(B(0, y), B(0, y + 1), P(0, y + 1));
            Tri(P(w - 1, y), P(w - 1, y + 1), B(w - 1, y)); Tri(B(w - 1, y), P(w - 1, y + 1), B(w - 1, y + 1));
        }
        return t;
    }
}

/// <summary>AutoCAD drawings: DWG ↔ DXF.</summary>
public sealed class CadEngine : EngineBase
{
    public override string Id => "cad";
    public override string Name => "CAD (built-in)";
    public override string Category => "Cad";
    public override string Description => "AutoCAD drawings: DWG to DXF and DXF to DWG, and both to SVG (lines, circles, arcs and polylines); from SVG they continue to PDF and PNG.";
    public override string Formats => "DWG ↔ DXF · DWG DXF → SVG (→ PDF PNG)";

    public override bool CanHandle(string e, Preset p) =>
        p.Mode == "" && ((e == ".dwg" && p.Extension == ".dxf") || (e == ".dxf" && p.Extension == ".dwg") || (Is(e, ".dwg", ".dxf") && p.Extension == ".svg"));

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        CadDocument doc;
        if (Util.Ext(input) == ".dwg") { using var r = new DwgReader(input); doc = r.Read(); }
        else { using var r = new DxfReader(input); doc = r.Read(); }

        var dest = Util.Out(input, outDir, p.Extension);
        if (p.Extension == ".svg") { Util.WriteText(dest, ToSvg(doc)); return new[] { dest }; }
        if (p.Extension == ".dwg") { using var w = new DwgWriter(dest, doc); w.Write(); }
        else { using var w = new DxfWriter(dest, doc, false); w.Write(); }
        return new[] { dest };
    }

    /// <summary>Model-space lines, circles, arcs and polylines as SVG paths, with Y flipped to screen direction.</summary>
    private static string ToSvg(CadDocument doc)
    {
        var shapes = new List<(string kind, double[] v)>();
        foreach (var e in doc.Entities)
            switch (e)
            {
                case ACadSharp.Entities.Line l: shapes.Add(("line", new[] { l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y })); break;
                case ACadSharp.Entities.Arc a: shapes.Add(("arc", new[] { a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle })); break;
                case ACadSharp.Entities.Circle c: shapes.Add(("circle", new[] { c.Center.X, c.Center.Y, c.Radius })); break;
                case ACadSharp.Entities.LwPolyline pl:
                    var pts = pl.Vertices.SelectMany(v => new[] { v.Location.X, v.Location.Y }).ToList();
                    pts.Insert(0, pl.IsClosed ? 1 : 0);
                    shapes.Add(("poly", pts.ToArray()));
                    break;
            }
        if (shapes.Count == 0) throw new InvalidDataException("No lines, circles, arcs or polylines in model space");

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x, double y) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        foreach (var (k, v) in shapes)
            switch (k)
            {
                case "line": Grow(v[0], v[1]); Grow(v[2], v[3]); break;
                case "circle": case "arc": Grow(v[0] - v[2], v[1] - v[2]); Grow(v[0] + v[2], v[1] + v[2]); break;
                default: for (var i = 1; i + 1 < v.Length; i += 2) Grow(v[i], v[i + 1]); break;
            }

        var w = Math.Max(maxX - minX, 1e-6); var h = Math.Max(maxY - minY, 1e-6);
        var pad = Math.Max(w, h) * 0.02; var stroke = Math.Max(w, h) / 600;
        string F(double d) => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        double X(double x) => x - minX + pad;
        double Y(double y) => maxY - y + pad;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w + 2 * pad)}\" height=\"{F(h + 2 * pad)}\" viewBox=\"0 0 {F(w + 2 * pad)} {F(h + 2 * pad)}\">");
        sb.AppendLine($"<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><g fill=\"none\" stroke=\"#000\" stroke-width=\"{F(stroke)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
        foreach (var (k, v) in shapes)
            switch (k)
            {
                case "line":
                    sb.AppendLine($"<line x1=\"{F(X(v[0]))}\" y1=\"{F(Y(v[1]))}\" x2=\"{F(X(v[2]))}\" y2=\"{F(Y(v[3]))}\"/>");
                    break;
                case "circle":
                    sb.AppendLine($"<circle cx=\"{F(X(v[0]))}\" cy=\"{F(Y(v[1]))}\" r=\"{F(v[2])}\"/>");
                    break;
                case "arc":
                    var sweep = ((v[4] - v[3]) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                    var sx = X(v[0] + v[2] * Math.Cos(v[3])); var sy = Y(v[1] + v[2] * Math.Sin(v[3]));
                    var ex = X(v[0] + v[2] * Math.Cos(v[4])); var ey = Y(v[1] + v[2] * Math.Sin(v[4]));
                    sb.AppendLine($"<path d=\"M{F(sx)} {F(sy)} A{F(v[2])} {F(v[2])} 0 {(sweep > Math.PI ? 1 : 0)} 0 {F(ex)} {F(ey)}\"/>");
                    break;
                default:
                    var d = new System.Text.StringBuilder();
                    for (var i = 1; i + 1 < v.Length; i += 2) d.Append(i == 1 ? 'M' : 'L').Append(F(X(v[i]))).Append(' ').Append(F(Y(v[i + 1])));
                    if (v[0] > 0) d.Append('Z');
                    sb.AppendLine($"<path d=\"{d}\"/>");
                    break;
            }
        sb.AppendLine("</g></svg>");
        return sb.ToString();
    }
}
