using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;
using ExcelDataReader;
using YamlDotNet.Serialization;

namespace Mochi.Services.Engines;

/// <summary>JSON, JSON Lines, YAML, TOML, XML and CSV into each other. Comments are dropped.</summary>
public sealed class DataEngine : EngineBase
{
    private static readonly string[] Fmt = { ".json", ".jsonl", ".yaml", ".yml", ".toml", ".xml", ".csv" };

    public override string Id => "data";
    public override string Name => "Data (built-in)";
    public override string Category => "Data";
    public override string Description => "JSON, JSON Lines, YAML, TOML, XML and CSV into each other. Comments are dropped. CSV holds tables (a list of records).";
    public override string Formats => "JSON JSONL YAML TOML XML CSV ↔ each other";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && Is(e, Fmt) && Is(p.Extension, Fmt) && Norm(e) != Norm(p.Extension);

    private static string Norm(string e) => e == ".yml" ? ".yaml" : e;

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var data = Read(Util.Ext(input), Util.ReadText(input));
        var dest = Util.Out(input, outDir, p.Extension);
        Util.WriteText(dest, Write(p.Extension, data));
        return new[] { dest };
    }

    // ---- reading into plain .NET values: Dictionary<string,object?>, List<object?>, string, long, double, bool
    private static object? Read(string ext, string text) => ext switch
    {
        ".json" => FromJson(JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })),
        ".jsonl" => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0)
                        .Select(l => FromJson(JsonNode.Parse(l))).ToList(),
        ".yaml" or ".yml" => Plain(new DeserializerBuilder().Build().Deserialize<object>(text)),
        ".toml" => Plain(Tomlyn.TomlSerializer.Deserialize<Dictionary<string, object?>>(text)),
        ".xml" => FromXml(XElement.Parse(text)),
        _ => FromCsv(text),
    };

    private static object? FromJson(JsonNode? n) => n switch
    {
        null => null,
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => FromJson(kv.Value)),
        JsonArray a => a.Select(FromJson).ToList(),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetValue<long>(out var l) ? l : v.GetValue<double>(),
            _ => null,
        },
        _ => null,
    };

    /// <summary>Normalizes YAML/TOML values: maps → string-keyed dictionaries, lists → List, "true"/"12" scalars → typed.</summary>
    private static object? Plain(object? o) => o switch
    {
        null => null,
        string s => Scalar(s),
        IDictionary d => d.Keys.Cast<object>().ToDictionary(k => k.ToString()!, k => Plain(d[k])),
        IEnumerable en and not string => en.Cast<object?>().Select(Plain).ToList(),
        DateTime dt => dt.ToString("o"),
        DateTimeOffset dto => dto.ToString("o"),
        _ => o,
    };

    private static object Scalar(string s)
    {
        if (bool.TryParse(s, out var b)) return b;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && !(s.Length > 1 && s[0] == '0')) return l;
        if (s.Contains('.') && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return s;
    }

    private static object? FromXml(XElement e)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var a in e.Attributes()) dict["@" + a.Name.LocalName] = a.Value;
        foreach (var g in e.Elements().GroupBy(x => x.Name.LocalName))
        {
            var items = g.Select(FromXml).ToList();
            dict[g.Key] = items.Count == 1 ? items[0] : items;
        }
        if (dict.Count == 0) return Scalar(e.Value);
        if (!e.HasElements && e.Value.Length > 0) dict["#text"] = e.Value;
        return dict;
    }

    private static object? FromCsv(string text)
    {
        var rows = Csv.Parse(text);
        if (rows.Count == 0) return new List<object?>();
        var head = rows[0];
        return rows.Skip(1).Select(r => (object?)head.Select((h, i) => (h, v: i < r.Count ? r[i] : ""))
            .ToDictionary(x => x.h, x => (object?)Scalar(x.v))).ToList();
    }

    // ---- writing
    private static string Write(string ext, object? data) => ext switch
    {
        ".json" => JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        ".jsonl" => string.Join("\n", (data as IEnumerable<object?> ?? new[] { data }).Select(x => JsonSerializer.Serialize(x, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))) + "\n",
        ".yaml" => new SerializerBuilder().Build().Serialize(data ?? ""),
        ".toml" => ToToml(DropNulls(data is Dictionary<string, object?> ? data : new Dictionary<string, object?> { ["items"] = data }) as Dictionary<string, object?> ?? new()),
        ".xml" => new XDocument(new XDeclaration("1.0", "utf-8", null), ToXml("root", data)).ToString(),
        _ => ToCsv(data),
    };

    // Own TOML writer: plain values first, then [tables] and [[arrays of tables]].
    private static string ToToml(Dictionary<string, object?> root)
    {
        var sb = new StringBuilder();
        static string Key(string k) => Regex.IsMatch(k, @"^[A-Za-z0-9_-]+$") ? k : "\"" + k.Replace("\"", "\\\"") + "\"";
        static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
        static string Val(object? v) => v switch
        {
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            long or int => System.Convert.ToString(v, CultureInfo.InvariantCulture)!,
            List<object?> l => "[" + string.Join(", ", l.Select(Val)) + "]",
            Dictionary<string, object?> d => "{ " + string.Join(", ", d.Select(kv => Key(kv.Key) + " = " + Val(kv.Value))) + " }",
            _ => Str(v?.ToString() ?? ""),
        };
        static bool IsTableList(object? v) => v is List<object?> l && l.Count > 0 && l.All(x => x is Dictionary<string, object?>);

        void Emit(Dictionary<string, object?> t, string path)
        {
            foreach (var kv in t.Where(kv => kv.Value is not Dictionary<string, object?> && !IsTableList(kv.Value)))
                sb.AppendLine(Key(kv.Key) + " = " + Val(kv.Value));
            foreach (var kv in t.Where(kv => kv.Value is Dictionary<string, object?>))
            {
                var p = path.Length == 0 ? Key(kv.Key) : path + "." + Key(kv.Key);
                sb.AppendLine().AppendLine("[" + p + "]");
                Emit((Dictionary<string, object?>)kv.Value!, p);
            }
            foreach (var kv in t.Where(kv => IsTableList(kv.Value)))
            {
                var p = path.Length == 0 ? Key(kv.Key) : path + "." + Key(kv.Key);
                foreach (var item in (List<object?>)kv.Value!)
                {
                    sb.AppendLine().AppendLine("[[" + p + "]]");
                    Emit((Dictionary<string, object?>)item!, p);
                }
            }
        }
        Emit(root, "");
        return sb.ToString();
    }

    private static object? DropNulls(object? o) => o switch
    {
        Dictionary<string, object?> d => d.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => DropNulls(kv.Value)),
        List<object?> l => l.Where(x => x is not null).Select(DropNulls).ToList(),
        _ => o,
    };

    private static string Tag(string s)
    {
        var t = Regex.Replace(s, @"[^\w.-]", "_");
        return t.Length == 0 || char.IsDigit(t[0]) ? "_" + t : t;
    }

    private static XElement ToXml(string name, object? v)
    {
        var el = new XElement(Tag(name));
        switch (v)
        {
            case Dictionary<string, object?> d:
                foreach (var kv in d)
                {
                    if (kv.Key.StartsWith('@')) el.SetAttributeValue(Tag(kv.Key[1..]), kv.Value);
                    else if (kv.Key == "#text") el.Add(new XText(Convert(kv.Value)));
                    else if (kv.Value is List<object?> l) foreach (var x in l) el.Add(ToXml(kv.Key, x));
                    else el.Add(ToXml(kv.Key, kv.Value));
                }
                break;
            case List<object?> l:
                foreach (var x in l) el.Add(ToXml("item", x));
                break;
            default:
                el.Value = Convert(v);
                break;
        }
        return el;
    }

    private static string Convert(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string ToCsv(object? data)
    {
        var rows = data switch
        {
            List<object?> l => l,
            null => new List<object?>(),
            _ => new List<object?> { data },
        };
        var cols = rows.OfType<Dictionary<string, object?>>().SelectMany(d => d.Keys).Distinct().ToList();
        if (cols.Count == 0) return Csv.Write(rows.Select(r => new[] { Cell(r) }));
        var lines = new List<IEnumerable<string>> { cols };
        foreach (var r in rows.OfType<Dictionary<string, object?>>())
            lines.Add(cols.Select(c => r.TryGetValue(c, out var v) ? Cell(v) : ""));
        return Csv.Write(lines);
    }

    private static string Cell(object? v) => v is Dictionary<string, object?> or List<object?> ? JsonSerializer.Serialize(v) : Convert(v);
}

/// <summary>XLSX, XLS, CSV and TSV into each other (XLS is read only). One CSV per sheet.</summary>
public sealed class SheetEngine : EngineBase
{
    public override string Id => "spreadsheet";
    public override string Name => "Spreadsheets (built-in)";
    public override string Category => "Spreadsheet";
    public override string Description => "XLSX, XLS, CSV and TSV into each other (XLS is read only), and any sheet to JSON. One CSV per sheet.";
    public override string Formats => "XLSX XLS CSV TSV → XLSX CSV TSV JSON";

    private sealed record Sheet(string Name, List<List<string>> Rows);

    public override bool CanHandle(string e, Preset p)
    {
        if (p.Mode != "" || e == p.Extension) return false;
        var o = p.Extension;
        return Is(e, ".xlsx", ".xls") ? Is(o, ".xlsx", ".csv", ".tsv", ".json")
             : e == ".tsv" ? Is(o, ".xlsx", ".csv", ".json")
             : e == ".csv" && o is ".xlsx" or ".tsv";
    }

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var sheets = Load(input);
        var outs = new List<string>();
        switch (p.Extension)
        {
            case ".xlsx":
                var dest = Util.Out(input, outDir, ".xlsx");
                using (var wb = new XLWorkbook())
                {
                    foreach (var s in sheets)
                    {
                        var ws = wb.AddWorksheet(SheetName(s.Name, wb));
                        for (var r = 0; r < s.Rows.Count; r++)
                            for (var c = 0; c < s.Rows[r].Count; c++)
                            {
                                var v = s.Rows[r][c];
                                if (v.Length == 0) continue;
                                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !(v.Length > 1 && v[0] == '0' && !v.StartsWith("0.")))
                                    ws.Cell(r + 1, c + 1).Value = d;
                                else ws.Cell(r + 1, c + 1).Value = v;
                            }
                    }
                    wb.SaveAs(dest);
                }
                outs.Add(dest);
                break;
            case ".json":
                var rows = sheets[0].Rows;
                var head = rows.Count > 0 ? rows[0] : new List<string>();
                var arr = rows.Skip(1).Select(r => head.Select((h, i) => (h, v: i < r.Count ? r[i] : "")).ToDictionary(x => x.h, x => x.v)).ToList();
                var j = Util.Out(input, outDir, ".json");
                Util.WriteText(j, JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                outs.Add(j);
                break;
            default:
                var sep = p.Extension == ".tsv" ? '\t' : ',';
                foreach (var s in sheets)
                {
                    var f = Util.Out(input, outDir, p.Extension, sheets.Count > 1 ? " - " + s.Name : "");
                    Util.WriteText(f, Csv.Write(s.Rows, sep));
                    outs.Add(f);
                }
                break;
        }
        return outs;
    }

    private static string SheetName(string n, XLWorkbook wb)
    {
        var s = Regex.Replace(string.IsNullOrWhiteSpace(n) ? "Sheet" : n, @"[\\/*?:\[\]]", "_");
        s = s.Length > 31 ? s[..31] : s;
        var b = s; var i = 2;
        while (wb.Worksheets.Contains(s)) s = (b.Length > 28 ? b[..28] : b) + " " + i++;
        return s;
    }

    private static List<Sheet> Load(string path)
    {
        var e = Util.Ext(path);
        if (e is ".csv" or ".tsv") return new() { new Sheet("Sheet1", Csv.Parse(Util.ReadText(path), e == ".tsv" ? '\t' : ',')) };

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var rd = ExcelReaderFactory.CreateReader(fs);
        var res = new List<Sheet>();
        do
        {
            var rows = new List<List<string>>();
            while (rd.Read())
            {
                var r = new List<string>();
                for (var i = 0; i < rd.FieldCount; i++)
                    r.Add(rd.GetValue(i) switch
                    {
                        null => "",
                        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).Replace(" 00:00:00", ""),
                        double d => d.ToString("0.###############", CultureInfo.InvariantCulture),
                        bool b => b ? "TRUE" : "FALSE",
                        var o => o.ToString() ?? "",
                    });
                while (r.Count > 0 && r[^1].Length == 0) r.RemoveAt(r.Count - 1);
                rows.Add(r);
            }
            while (rows.Count > 0 && rows[^1].Count == 0) rows.RemoveAt(rows.Count - 1);
            res.Add(new Sheet(rd.Name, rows));
        } while (rd.NextResult());
        return res;
    }
}

/// <summary>Geospatial vector data: GeoJSON, KML/KMZ, GPX and CSV (WKT). Points, lines and polygon outlines; WGS 84 lon/lat.</summary>
public sealed class GeoEngine : EngineBase
{
    public sealed record Feat(string Name, string Type, List<(double Lon, double Lat)> Pts);

    public override string Id => "geo";
    public override string Name => "Geospatial vector data (built-in)";
    public override string Category => "Geo";
    public override string Description => "GeoJSON, KML, KMZ, GPX and Shapefile into each other and into CSV (WKT). Points, lines and polygon outlines; results are WGS 84 longitude/latitude.";
    public override string Formats => "GEOJSON KML KMZ GPX SHP → GEOJSON KML GPX SHP CSV";

    public override bool CanHandle(string e, Preset p) =>
        p.Mode == "" && Is(e, ".geojson", ".kml", ".kmz", ".gpx", ".shp") && Is(p.Extension, ".geojson", ".kml", ".gpx", ".csv", ".shp") && e != p.Extension;

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var f = Read(input);
        var dest = Util.Out(input, outDir, p.Extension);
        if (p.Extension == ".shp")
        {
            if (f.Count == 0) throw new InvalidDataException("Nothing to write");
            return Shapefile.Write(f, dest);
        }
        Util.WriteText(dest, Write(p.Extension, f));
        return new[] { dest };
    }

    private static List<Feat> Read(string path)
    {
        var e = Util.Ext(path);
        if (e == ".shp") return Shapefile.Read(path);
        if (e == ".geojson")
        {
            var res = new List<Feat>();
            void Geom(JsonNode? g, string name)
            {
                var type = g?["type"]?.GetValue<string>();
                var c = g?["coordinates"];
                switch (type)
                {
                    case "Point": res.Add(new Feat(name, "Point", new() { Pt(c) })); break;
                    case "LineString": res.Add(new Feat(name, "LineString", c!.AsArray().Select(Pt).ToList())); break;
                    case "Polygon": res.Add(new Feat(name, "Polygon", c![0]!.AsArray().Select(Pt).ToList())); break;
                    case "MultiPoint": foreach (var x in c!.AsArray()) res.Add(new Feat(name, "Point", new() { Pt(x) })); break;
                    case "MultiLineString": foreach (var x in c!.AsArray()) res.Add(new Feat(name, "LineString", x!.AsArray().Select(Pt).ToList())); break;
                    case "MultiPolygon": foreach (var x in c!.AsArray()) res.Add(new Feat(name, "Polygon", x![0]!.AsArray().Select(Pt).ToList())); break;
                    case "GeometryCollection": foreach (var x in g!["geometries"]!.AsArray()) Geom(x, name); break;
                }
            }
            void Node(JsonNode? n)
            {
                switch (n?["type"]?.GetValue<string>())
                {
                    case "FeatureCollection": foreach (var x in n["features"]!.AsArray()) Node(x); break;
                    case "Feature": Geom(n["geometry"], n["properties"]?["name"]?.ToString() ?? ""); break;
                    default: Geom(n, ""); break;
                }
            }
            Node(JsonNode.Parse(Util.ReadText(path)));
            return res;
        }

        string text;
        if (e == ".kmz")
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(path);
            var en = z.Entries.FirstOrDefault(x => x.Name.EndsWith(".kml", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("No KML in KMZ");
            using var sr = new StreamReader(en.Open(), Encoding.UTF8);
            text = sr.ReadToEnd();
        }
        else text = Util.ReadText(path);

        var doc = XDocument.Parse(text);
        var feats = new List<Feat>();
        static string N(XElement x) => x.Name.LocalName;
        static List<(double, double)> Coords(string s) =>
            s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split(',')).Where(a => a.Length >= 2)
             .Select(a => (double.Parse(a[0], CultureInfo.InvariantCulture), double.Parse(a[1], CultureInfo.InvariantCulture))).ToList();

        if (e == ".gpx")
        {
            foreach (var w in doc.Descendants().Where(x => N(x) == "wpt"))
                feats.Add(new Feat(w.Elements().FirstOrDefault(x => N(x) == "name")?.Value ?? "", "Point", new() { LL(w) }));
            foreach (var t in doc.Descendants().Where(x => N(x) is "trk" or "rte"))
                feats.Add(new Feat(t.Elements().FirstOrDefault(x => N(x) == "name")?.Value ?? "", "LineString",
                    t.Descendants().Where(x => N(x) is "trkpt" or "rtept").Select(LL).ToList()));
            return feats;
        }

        foreach (var pm in doc.Descendants().Where(x => N(x) == "Placemark"))
        {
            var name = pm.Elements().FirstOrDefault(x => N(x) == "name")?.Value ?? "";
            foreach (var g in pm.Descendants().Where(x => N(x) is "Point" or "LineString" or "LinearRing"))
            {
                var c = g.Elements().FirstOrDefault(x => N(x) == "coordinates")?.Value;
                if (c is null) continue;
                var pts = Coords(c);
                var type = N(g) == "Point" ? "Point" : N(g) == "LinearRing" ? "Polygon" : "LineString";
                feats.Add(new Feat(name, type, pts.Select(t => (t.Item1, t.Item2)).ToList()));
            }
        }
        return feats;
    }

    private static (double, double) Pt(JsonNode? n) => (n![0]!.GetValue<double>(), n[1]!.GetValue<double>());
    private static (double, double) LL(XElement x) =>
        (double.Parse(x.Attribute("lon")!.Value, CultureInfo.InvariantCulture), double.Parse(x.Attribute("lat")!.Value, CultureInfo.InvariantCulture));

    private static string F(double d) => d.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string Write(string ext, List<Feat> f)
    {
        var sb = new StringBuilder();
        switch (ext)
        {
            case ".geojson":
                var features = new JsonArray();
                foreach (var x in f)
                {
                    JsonNode geom = x.Type switch
                    {
                        "Point" => new JsonObject { ["type"] = "Point", ["coordinates"] = new JsonArray(x.Pts[0].Lon, x.Pts[0].Lat) },
                        "Polygon" => new JsonObject { ["type"] = "Polygon", ["coordinates"] = new JsonArray(new JsonArray(x.Pts.Select(t => (JsonNode)new JsonArray(t.Lon, t.Lat)).ToArray())) },
                        _ => new JsonObject { ["type"] = "LineString", ["coordinates"] = new JsonArray(x.Pts.Select(t => (JsonNode)new JsonArray(t.Lon, t.Lat)).ToArray()) },
                    };
                    features.Add(new JsonObject { ["type"] = "Feature", ["properties"] = new JsonObject { ["name"] = x.Name }, ["geometry"] = geom });
                }
                return new JsonObject { ["type"] = "FeatureCollection", ["features"] = features }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            case ".kml":
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>");
                foreach (var x in f)
                {
                    var c = string.Join(" ", x.Pts.Select(t => $"{F(t.Lon)},{F(t.Lat)},0"));
                    var g = x.Type switch
                    {
                        "Point" => $"<Point><coordinates>{c}</coordinates></Point>",
                        "Polygon" => $"<Polygon><outerBoundaryIs><LinearRing><coordinates>{c}</coordinates></LinearRing></outerBoundaryIs></Polygon>",
                        _ => $"<LineString><coordinates>{c}</coordinates></LineString>",
                    };
                    sb.AppendLine($"<Placemark><name>{Esc(x.Name)}</name>{g}</Placemark>");
                }
                return sb.Append("</Document></kml>\n").ToString();
            case ".gpx":
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<gpx version=\"1.1\" creator=\"Mochi\" xmlns=\"http://www.topografix.com/GPX/1/1\">");
                foreach (var x in f)
                    if (x.Type == "Point") sb.AppendLine($"<wpt lat=\"{F(x.Pts[0].Lat)}\" lon=\"{F(x.Pts[0].Lon)}\"><name>{Esc(x.Name)}</name></wpt>");
                    else sb.AppendLine($"<trk><name>{Esc(x.Name)}</name><trkseg>" + string.Concat(x.Pts.Select(t => $"<trkpt lat=\"{F(t.Lat)}\" lon=\"{F(t.Lon)}\"/>")) + "</trkseg></trk>");
                return sb.Append("</gpx>\n").ToString();
            default:
                return Csv.Write(new[] { new[] { "name", "type", "wkt" } }.Concat(f.Select(x =>
                {
                    var c = string.Join(", ", x.Pts.Select(t => $"{F(t.Lon)} {F(t.Lat)}"));
                    return new[] { x.Name, x.Type, x.Type switch { "Point" => $"POINT ({c})", "Polygon" => $"POLYGON (({c}))", _ => $"LINESTRING ({c})" } };
                })));
        }
    }
}
