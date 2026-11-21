using ClosedXML.Excel;
using PDFtoImage;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Writer;

namespace Mochi.Services.Engines;

/// <summary>More from PDFs: Word, Markdown, Excel (tables) and PowerPoint out; password protect and unlock, page ranges, metadata removal.</summary>
public sealed class PdfExtraEngine : EngineBase
{
    public override string Id => "pdf-extra";
    public override string Name => "PDF conversions and security (built-in)";
    public override string Category => "Pdf";
    public override string Description => "PDF to Word (text), Markdown, Excel (tables found in the text) and PowerPoint (each page a slide, page text in the notes); protect with a password, remove a password, pull out pages by range, and remove document metadata.";
    public override string Formats => "PDF → DOCX MD XLSX PPTX · protect · unlock · page range · remove metadata";

    public override bool CanHandle(string e, Preset p)
    {
        if (e != ".pdf") return false;
        return p.Mode switch
        {
            "" => p.Extension is ".docx" or ".md" or ".xlsx" || (p.Extension == ".pptx" && Tools.Find("pandoc") is not null),
            "protect" or "unlock" or "range" or "strip" => true,
            _ => false,
        };
    }

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        switch (p.Mode)
        {
            case "protect": return new[] { Protect(input, outDir) };
            case "unlock": return new[] { Unlock(input, outDir) };
            case "range": return new[] { Range(input, outDir) };
            case "strip": return new[] { Strip(input, outDir) };
        }
        return p.Extension switch
        {
            ".docx" => new[] { ToDocx(input, outDir) },
            ".md" => new[] { ToMarkdown(input, outDir) },
            ".xlsx" => new[] { ToXlsx(input, outDir) },
            _ => new[] { ToPptx(input, outDir) },
        };
    }

    // ---------------------------------------------------------------- text out
    private static List<Block> Blocks(string input)
    {
        using var doc = PdfDocument_Open(input);
        var res = new List<Block>();
        foreach (var page in doc.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page, addDoubleNewline: true).Replace("\r\n", "\n");
            foreach (var para in System.Text.RegularExpressions.Regex.Split(text, @"\n\s*\n").Select(s => s.Trim()).Where(s => s.Length > 0))
                res.Add(new Block("p", para.Replace("\n", " ")));
        }
        return res;
    }

    private static UglyToad.PdfPig.PdfDocument PdfDocument_Open(string path) => UglyToad.PdfPig.PdfDocument.Open(path);

    private static string ToDocx(string input, string outDir)
    {
        var dest = Util.Out(input, outDir, ".docx");
        DocumentEngine.WriteDocx(dest, Blocks(input));
        return dest;
    }

    private static string ToMarkdown(string input, string outDir)
    {
        var dest = Util.Out(input, outDir, ".md");
        Util.WriteText(dest, DocumentEngine.ToMarkdown(Blocks(input)));
        return dest;
    }

    // ---------------------------------------------------------------- tables to Excel
    /// <summary>Rows are words sharing a baseline; a cell break is a wide gap; a table is a run of rows that split into a similar number of cells.</summary>
    private static string ToXlsx(string input, string outDir)
    {
        using var doc = PdfDocument_Open(input);
        using var wb = new XLWorkbook();
        var n = 0;
        foreach (var page in doc.GetPages())
        {
            var rows = page.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3)).OrderByDescending(g => g.Key)
                .Select(g => Cells(g.OrderBy(w => w.BoundingBox.Left).ToList())).ToList();
            var run = new List<List<string>>();
            void Flush()
            {
                if (run.Count >= 3)
                {
                    var ws = wb.AddWorksheet($"P{page.Number} T{++n}".Length > 31 ? "Table" + n : $"P{page.Number} T{n}");
                    for (var r = 0; r < run.Count; r++)
                        for (var c = 0; c < run[r].Count; c++)
                        {
                            var v = run[r][c];
                            if (double.TryParse(v.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) ws.Cell(r + 1, c + 1).Value = d;
                            else ws.Cell(r + 1, c + 1).Value = v;
                        }
                    ws.Columns().AdjustToContents();
                }
                run.Clear();
            }
            foreach (var r in rows)
            {
                if (r.Count >= 2 && (run.Count == 0 || Math.Abs(run[^1].Count - r.Count) <= 1)) run.Add(r); else { Flush(); if (r.Count >= 2) run.Add(r); }
            }
            Flush();
        }
        if (n == 0) throw new InvalidOperationException("No tables found in this PDF");
        var dest = Util.Out(input, outDir, ".xlsx");
        wb.SaveAs(dest);
        return dest;
    }

    private static List<string> Cells(List<UglyToad.PdfPig.Content.Word> words)
    {
        var cells = new List<string>(); var sb = new StringBuilder(); double? lastRight = null;
        foreach (var w in words)
        {
            var size = Math.Max(4, w.BoundingBox.Height);
            if (lastRight is not null && w.BoundingBox.Left - lastRight > size * 1.2) { cells.Add(sb.ToString().Trim()); sb.Clear(); }
            sb.Append(w.Text).Append(' ');
            lastRight = w.BoundingBox.Right;
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    // ---------------------------------------------------------------- pages as slides (through Pandoc)
    private static string ToPptx(string input, string outDir)
    {
        var tmp = Util.TempDir();
        try
        {
            var bytes = File.ReadAllBytes(input);
            var md = new StringBuilder();
            using var ms = new MemoryStream(bytes);
            var i = 0;
            using var pdf = PdfDocument_Open(input);
            var texts = pdf.GetPages().Select(pg => pg.Text).ToList();
            foreach (var bmp in Conversion.ToImages(ms, options: new PDFtoImage.RenderOptions(Dpi: 130)))
            {
                using (bmp)
                {
                    var png = Path.Combine(tmp, $"p{++i}.png");
                    using var d = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(png, d.ToArray());
                    md.AppendLine($"![](p{i}.png)").AppendLine();
                    md.AppendLine("::: notes").AppendLine(i - 1 < texts.Count ? texts[i - 1] : "").AppendLine(":::").AppendLine();
                }
            }
            var mdPath = Path.Combine(tmp, "slides.md");
            Util.WriteText(mdPath, md.ToString());
            var dest = Util.Out(input, outDir, ".pptx");
            Proc.Run(Tools.Find("pandoc")!, new[] { mdPath, "-o", dest, "--slide-level=1" }, tmp);
            return dest;
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
    }

    // ---------------------------------------------------------------- security
    private static string Protect(string input, string outDir)
    {
        var v = Prompts.Ask("pdf-protect", Loc.T("x.pw.protect"), new[] { Loc.T("x.pw.password") }, new[] { "" }, new[] { true });
        if (string.IsNullOrEmpty(v[0])) throw new InvalidOperationException(Loc.T("x.pw.empty"));
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var h = doc.SecurityHandler;
        h.UserPassword = v[0];
        h.OwnerPassword = v[0] + "·owner";
        h.SetEncryptionToV4UsingAES();
        var dest = Util.Out(input, outDir, ".pdf", " (protected)");
        doc.Save(dest);
        return dest;
    }

    private static string Unlock(string input, string outDir)
    {
        var v = Prompts.Ask("pdf-unlock", Loc.T("x.pw.unlock"), new[] { Loc.T("x.pw.password") }, new[] { "" }, new[] { true });
        // the opening password is enough to read every page, so copy them into a new file that has no password
        using var src = PdfReader.Open(input, v[0], PdfDocumentOpenMode.Import);
        using var doc = new PdfSharp.Pdf.PdfDocument();
        foreach (var page in src.Pages) doc.AddPage(page);
        var dest = Util.Out(input, outDir, ".pdf", " (unlocked)");
        doc.Save(dest);
        return dest;
    }

    /// <summary>"1-3, 5, 8-" as page numbers.</summary>
    private static string Range(string input, string outDir)
    {
        var bytes = File.ReadAllBytes(input);
        int count; using (var d = UglyToad.PdfPig.PdfDocument.Open(bytes)) count = d.NumberOfPages;
        var v = Prompts.Ask("pdf-range", Loc.T("x.rg.title"), new[] { Loc.T("x.rg.pages") }, new[] { "1-" + Math.Min(3, count) });
        var pages = new List<int>();
        foreach (var part in v[0].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = part.Split('-');
            var a = int.Parse(ab[0]); var b = ab.Length == 1 ? a : (ab[1].Length == 0 ? count : int.Parse(ab[1]));
            for (var k = a; k <= b; k++) if (k >= 1 && k <= count) pages.Add(k);
        }
        if (pages.Count == 0) throw new InvalidOperationException(Loc.T("x.rg.none"));
        var dest = Util.Out(input, outDir, ".pdf", " (pages)");
        File.WriteAllBytes(dest, PdfMerger.Merge(new List<byte[]> { bytes }, new List<IReadOnlyList<int>> { pages }));
        return dest;
    }

    private static string Strip(string input, string outDir)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.Info.Title = ""; doc.Info.Author = ""; doc.Info.Subject = ""; doc.Info.Keywords = ""; doc.Info.Creator = ""; doc.Info.Elements.SetString("/Producer", "");
        doc.Internals.Catalog.Elements.Remove("/Metadata");   // the XMP packet too
        var dest = Util.Out(input, outDir, ".pdf", " (clean)");
        doc.Save(dest);
        return dest;
    }
}
