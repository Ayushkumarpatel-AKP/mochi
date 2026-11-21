using System.Buffers.Binary;
using System.Text;

namespace Mochi.Services.Engines;

/// <summary>ESRI Shapefile (.shp + .shx + .dbf) reading and writing for points, lines and polygon outlines (WGS 84).</summary>
public static class Shapefile
{
    public static List<GeoEngine.Feat> Read(string shpPath)
    {
        var b = File.ReadAllBytes(shpPath);
        var names = Names(Path.ChangeExtension(shpPath, ".dbf"));
        var res = new List<GeoEngine.Feat>();
        var p = 100; var rec = 0;
        while (p + 8 <= b.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 4)) * 2;
            var s = p + 8; p = s + len;
            if (len < 4) { rec++; continue; }
            var name = rec < names.Count ? names[rec] : "";
            rec++;
            var type = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(s));
            double D(int o) => BitConverter.ToDouble(b, s + o);
            switch (type)
            {
                case 1 or 11 or 21:
                    res.Add(new GeoEngine.Feat(name, "Point", new() { (D(4), D(12)) }));
                    break;
                case 8 or 18 or 28:
                {
                    var n = BitConverter.ToInt32(b, s + 36);
                    for (var i = 0; i < n; i++) res.Add(new GeoEngine.Feat(name, "Point", new() { (D(40 + 16 * i), D(48 + 16 * i)) }));
                    break;
                }
                case 3 or 13 or 23 or 5 or 15 or 25:
                {
                    int parts = BitConverter.ToInt32(b, s + 36), n = BitConverter.ToInt32(b, s + 40);
                    var starts = Enumerable.Range(0, parts).Select(i => BitConverter.ToInt32(b, s + 44 + 4 * i)).ToList();
                    var pts0 = s + 44 + 4 * parts;
                    for (var i = 0; i < parts; i++)
                    {
                        var from = starts[i]; var to = i + 1 < parts ? starts[i + 1] : n;
                        var pts = new List<(double, double)>();
                        for (var k = from; k < to; k++) pts.Add((BitConverter.ToDouble(b, pts0 + 16 * k), BitConverter.ToDouble(b, pts0 + 16 * k + 8)));
                        res.Add(new GeoEngine.Feat(name, type is 3 or 13 or 23 ? "LineString" : "Polygon", pts));
                    }
                    break;
                }
            }
        }
        return res;
    }

    /// <summary>Names from the first text column called NAME (or the first text column) of the .dbf next to the .shp.</summary>
    private static List<string> Names(string dbf)
    {
        var res = new List<string>();
        if (!File.Exists(dbf)) return res;
        var b = File.ReadAllBytes(dbf);
        var n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        int head = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)), rl = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(10));
        int pos = 1, col = -1, colLen = 0, colOff = 0;
        for (var f = 32; f < head - 1 && b[f] != 0x0D; f += 32)
        {
            var fname = Encoding.ASCII.GetString(b, f, 11).TrimEnd('\0');
            int len = b[f + 16];
            if (b[f + 11] == (byte)'C' && (col < 0 || fname.Equals("NAME", StringComparison.OrdinalIgnoreCase))) { col = f; colLen = len; colOff = pos; }
            pos += len;
        }
        if (col < 0) return res;
        for (var i = 0; i < n; i++) res.Add(Encoding.UTF8.GetString(b, head + i * rl + colOff, colLen).Trim());
        return res;
    }

    /// <summary>Writes one shapefile per geometry type found (points, lines, polygons), returning every .shp written.</summary>
    public static List<string> Write(List<GeoEngine.Feat> feats, string dest)
    {
        var outs = new List<string>();
        var groups = feats.GroupBy(f => f.Type).ToList();
        foreach (var g in groups)
        {
            var path = groups.Count == 1 ? dest : Path.Combine(Path.GetDirectoryName(dest)!, Path.GetFileNameWithoutExtension(dest) + " - " + g.Key.ToLowerInvariant() + "s.shp");
            var free = Util.Unique(path);
            WriteOne(g.ToList(), g.Key, free);
            outs.Add(free);
        }
        return outs;
    }

    private static void WriteOne(List<GeoEngine.Feat> f, string type, string path)
    {
        var shape = type == "Point" ? 1 : type == "Polygon" ? 5 : 3;
        var recs = new List<byte[]>();
        foreach (var x in f)
        {
            var pts = x.Pts;
            if (shape == 5)
            {
                pts = pts.ToList();
                if (pts[0] != pts[^1]) pts.Add(pts[0]);
                double area = 0;
                for (var i = 0; i + 1 < pts.Count; i++) area += pts[i].Lon * pts[i + 1].Lat - pts[i + 1].Lon * pts[i].Lat;
                if (area > 0) pts.Reverse();   // outer rings run clockwise
            }
            var m = new MemoryStream(); var w = new BinaryWriter(m);
            w.Write(shape);
            if (shape == 1) { w.Write(pts[0].Lon); w.Write(pts[0].Lat); }
            else
            {
                w.Write(pts.Min(p => p.Lon)); w.Write(pts.Min(p => p.Lat)); w.Write(pts.Max(p => p.Lon)); w.Write(pts.Max(p => p.Lat));
                w.Write(1); w.Write(pts.Count); w.Write(0);
                foreach (var p in pts) { w.Write(p.Lon); w.Write(p.Lat); }
            }
            recs.Add(m.ToArray());
        }

        var all = f.SelectMany(x => x.Pts).ToList();
        double minX = all.Min(p => p.Lon), minY = all.Min(p => p.Lat), maxX = all.Max(p => p.Lon), maxY = all.Max(p => p.Lat);

        byte[] Header(int fileLen)
        {
            var h = new byte[100];
            BinaryPrimitives.WriteInt32BigEndian(h, 9994);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(24), fileLen / 2);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), 1000);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(32), shape);
            foreach (var (v, o) in new[] { (minX, 36), (minY, 44), (maxX, 52), (maxY, 60) }) BitConverter.GetBytes(v).CopyTo(h, o);
            return h;
        }

        var shpLen = 100 + recs.Sum(r => 8 + r.Length);
        using (var shp = new BinaryWriter(File.Create(path)))
        using (var shx = new BinaryWriter(File.Create(Path.ChangeExtension(path, ".shx"))))
        {
            shp.Write(Header(shpLen));
            shx.Write(Header(100 + 8 * recs.Count));
            var off = 100;
            for (var i = 0; i < recs.Count; i++)
            {
                void BE(BinaryWriter w, int v) { var t = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(t, v); w.Write(t); }
                BE(shp, i + 1); BE(shp, recs[i].Length / 2); shp.Write(recs[i]);
                BE(shx, off / 2); BE(shx, recs[i].Length / 2);
                off += 8 + recs[i].Length;
            }
        }

        using (var d = new BinaryWriter(File.Create(Path.ChangeExtension(path, ".dbf"))))
        {
            d.Write((byte)3); d.Write((byte)126); d.Write((byte)1); d.Write((byte)1);
            d.Write((uint)f.Count); d.Write((ushort)(32 + 32 + 1)); d.Write((ushort)81);
            d.Write(new byte[20]);
            var nm = new byte[32]; Encoding.ASCII.GetBytes("NAME").CopyTo(nm, 0); nm[11] = (byte)'C'; nm[16] = 80; d.Write(nm);
            d.Write((byte)0x0D);
            foreach (var x in f)
            {
                d.Write((byte)' ');
                var s = new byte[80]; Array.Fill(s, (byte)' ');
                var t = Encoding.UTF8.GetBytes(x.Name); Array.Copy(t, s, Math.Min(80, t.Length));
                d.Write(s);
            }
            d.Write((byte)0x1A);
        }
        File.WriteAllText(Path.ChangeExtension(path, ".prj"),
            "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]");
    }
}
