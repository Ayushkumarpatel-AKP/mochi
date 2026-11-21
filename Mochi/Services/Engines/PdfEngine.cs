using PDFtoImage;
using UglyToad.PdfPig.Writer;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Mochi.Services.Engines;

/// <summary>PDF in and out: render pages to pictures, extract text, make PDFs from pictures, merge and split.</summary>
public sealed class PdfEngine : EngineBase
{
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public override string Id => "pdf";
    public override string Name => "PDF (built-in)";
    public override string Category => "Pdf";
    public override string Description => "Renders PDF pages to PNG and JPG, extracts text, makes PDFs from pictures, merges and splits PDFs.";
    public override string Formats => "PDF → PNG JPG TXT · pictures → PDF · merge · split";

    public override bool CanHandle(string e, Preset p)
    {
        if (e == ".pdf")
            return p.Mode switch
            {
                "" => Is(p.Extension, ".png", ".jpg", ".webp", ".txt"),
                "split" or "merge" => p.Extension == ".pdf",
                var m when m.StartsWith("rotate:") => p.Extension == ".pdf",
                _ => false,
            };
        return Is(e, Img) && p.Extension == ".pdf" && p.Mode is "" or "merge";
    }

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var e = Util.Ext(input);
        if (e != ".pdf") return new[] { ImagesToPdf(new[] { input }, Util.Out(input, outDir, ".pdf")) };
        if (p.Mode == "split") return Split(input, outDir);
        if (p.Mode.StartsWith("rotate:"))
        {
            var dest = Util.Out(input, outDir, ".pdf", " (rotated)");
            RotateSave(File.ReadAllBytes(input), dest, null, int.Parse(p.Mode[7..]));
            return new[] { dest };
        }
        if (p.Extension == ".txt") return new[] { ExtractText(input, outDir) };
        return Render(input, p, outDir);
    }

    public override IReadOnlyList<string> ConvertMany(IReadOnlyList<string> inputs, Preset p, string outDir)
    {
        var first = inputs[0];
        var dest = Util.Out(first, outDir, ".pdf", " (merged)");
        if (Util.Ext(first) != ".pdf") return new[] { ImagesToPdf(inputs, dest) };

        File.WriteAllBytes(dest, PdfMerger.Merge(inputs.Select(File.ReadAllBytes).ToList()));
        return new[] { dest };
    }

    private static IReadOnlyList<string> Render(string input, Preset p, string outDir)
    {
        var fmt = p.Extension switch { ".png" => SKEncodedImageFormat.Png, ".webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Jpeg };
        var bytes = File.ReadAllBytes(input);
        var outs = new List<string>();
        int pages;
        using (var counter = new MemoryStream(bytes)) pages = Conversion.GetPageCount(counter);
        using var fs = new MemoryStream(bytes);
        var i = 0;
        foreach (var bmp in Conversion.ToImages(fs, options: new RenderOptions(Dpi: 150)))
        {
            using (bmp)
            {
                i++;
                var dest = Util.Out(input, outDir, p.Extension, pages > 1 ? $" - page {i}" : "");
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(fmt, p.Quality);
                using var o = File.Create(dest);
                data.SaveTo(o);
                outs.Add(dest);
            }
        }
        return outs;
    }

    private static string ExtractText(string input, string outDir)
    {
        using var doc = PdfDocument_Open(input);
        var text = string.Join("\n\n", doc.GetPages().Select(pg => pg.Text));
        var dest = Util.Out(input, outDir, ".txt");
        Util.WriteText(dest, text);
        return dest;
    }

    private static UglyToad.PdfPig.PdfDocument PdfDocument_Open(string path) => UglyToad.PdfPig.PdfDocument.Open(path);

    private static IReadOnlyList<string> Split(string input, string outDir)
    {
        var bytes = File.ReadAllBytes(input);
        int count;
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(bytes)) count = doc.NumberOfPages;
        var outs = new List<string>();
        for (var i = 1; i <= count; i++)
        {
            var dest = Util.Out(input, outDir, ".pdf", $" - page {i}");
            File.WriteAllBytes(dest, PdfMerger.Merge(new List<byte[]> { bytes }, new List<IReadOnlyList<int>> { new[] { i } }));
            outs.Add(dest);
        }
        return outs;
    }

    /// <summary>Rotates pages of a PDF in place of copying them: every page by <paramref name="all"/> degrees, or page N by perPage[N].</summary>
    public static void RotateSave(byte[] pdf, string dest, Dictionary<int, int>? perPage, int all = 0)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(ms, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        for (var i = 0; i < doc.PageCount; i++)
        {
            var add = all + (perPage is not null && perPage.TryGetValue(i + 1, out var r) ? r : 0);
            if (add % 360 != 0) doc.Pages[i].Rotate = ((doc.Pages[i].Rotate + add) % 360 + 360) % 360;
        }
        doc.Save(dest);
    }

    /// <summary>One page per picture, sized to the picture. Pictures go through Skia so every format PDFsharp can't read still works.</summary>
    public static string ImagesToPdf(IReadOnlyList<string> images, string dest)
    {
        using var fs = File.Create(dest);
        using var doc = SKDocument.CreatePdf(fs);
        foreach (var f in images)
        {
            using var bmp = SKBitmap.Decode(f) ?? throw new InvalidDataException("Cannot read " + Path.GetFileName(f));
            // 72 points per inch: size the page so the picture prints at 150 dpi
            float w = bmp.Width * 72f / 150f, h = bmp.Height * 72f / 150f;
            using var canvas = doc.BeginPage(w, h);
            canvas.DrawBitmap(bmp, new SKRect(0, 0, w, h));
            doc.EndPage();
        }
        doc.Close();
        return dest;
    }
}
