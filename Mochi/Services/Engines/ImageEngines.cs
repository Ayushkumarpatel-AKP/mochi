using SkiaSharp;
using Svg.Skia;

namespace Mochi.Services.Engines;

public sealed class ImageEngine : EngineBase
{
    private static readonly string[] In = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".wbmp" };
    private static readonly string[] Out = { ".png", ".jpg", ".jpeg", ".webp" };

    public override string Id => "skia";
    public override string Name => "Images (built-in)";
    public override string Category => "Image";
    public override string Description => "PNG, JPG, WEBP, BMP, GIF and ICO into PNG, JPG or WEBP. Quality, size and grayscale follow the preset.";
    public override string Formats => "PNG JPG WEBP BMP GIF ICO → PNG JPG WEBP";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && Is(e, In) && Is(p.Extension, Out);

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        using var src = SKBitmap.Decode(input) ?? throw new InvalidDataException("Cannot decode image");
        var fmt = p.Extension switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg,
        };
        var dest = Util.Out(input, outDir, p.Extension);
        Save(src, dest, fmt, p);
        return new[] { dest };
    }

    public static void Save(SKBitmap src, string dest, SKEncodedImageFormat fmt, Preset p)
    {
        var w = Math.Max(1, (int)(src.Width * p.Scale));
        var h = Math.Max(1, (int)(src.Height * p.Scale));
        using var dst = new SKBitmap(w, h);
        using (var c = new SKCanvas(dst))
        {
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            if (p.Gray)
                paint.ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                {
                    .299f, .587f, .114f, 0, 0, .299f, .587f, .114f, 0, 0,
                    .299f, .587f, .114f, 0, 0, 0, 0, 0, 1, 0
                });
            if (fmt == SKEncodedImageFormat.Jpeg) c.Clear(SKColors.White);   // JPG has no alpha
            c.DrawBitmap(src, new SKRect(0, 0, w, h), paint);
        }
        using var img = SKImage.FromBitmap(dst);
        using var data = img.Encode(fmt, p.Quality);
        using var fs = File.Create(dest);
        data.SaveTo(fs);
    }
}

public sealed class VectorEngine : EngineBase
{
    public override string Id => "vector";
    public override string Name => "Vector graphics (built-in)";
    public override string Category => "Vector";
    public override string Description => "SVG to PNG, JPG, WEBP and vector PDF.";
    public override string Formats => "SVG → PNG JPG WEBP PDF";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && e == ".svg" && Is(p.Extension, ".png", ".jpg", ".webp", ".pdf");

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        using var svg = new SKSvg();
        var pic = svg.Load(input) ?? throw new InvalidDataException("Cannot read SVG");
        var r = pic.CullRect;
        var dest = Util.Out(input, outDir, p.Extension);

        if (p.Extension == ".pdf")
        {
            using var fs = File.Create(dest);
            using var doc = SKDocument.CreatePdf(fs);
            using var canvas = doc.BeginPage(r.Width, r.Height);
            canvas.DrawPicture(pic);
            doc.EndPage();
            doc.Close();
            return new[] { dest };
        }

        var scale = p.Scale <= 0 ? 1 : p.Scale;
        var px = 2f * (float)scale;   // 2× so small icons stay sharp
        using var bmp = new SKBitmap(Math.Max(1, (int)(r.Width * px)), Math.Max(1, (int)(r.Height * px)));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(p.Extension == ".jpg" ? SKColors.White : SKColors.Transparent);
            c.Scale(px);
            c.DrawPicture(pic);
        }
        ImageEngine.Save(bmp, dest, p.Extension switch { ".png" => SKEncodedImageFormat.Png, ".webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Jpeg },
            new Preset("v", "", "", p.Extension, p.Quality));
        return new[] { dest };
    }
}

public sealed class MagickEngine : EngineBase
{
    private static readonly string[] In =
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif", ".psd", ".jp2",
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".tga", ".jxl", ".xcf", ".pcx", ".ppm", ".exr", ".hdr",
    };
    private static readonly string[] Out = { ".png", ".jpg", ".webp", ".bmp", ".gif", ".tiff", ".tif", ".ico", ".avif", ".heic", ".jp2" };

    public override string Id => "magick";
    public override string Name => "ImageMagick";
    public override string Category => "Image";
    public override string Description => "JPG, PNG, WEBP, AVIF, HEIC, TIFF, GIF, PSD, camera RAW and more. Quality, size and grayscale follow the preset.";
    public override string Formats => "PNG JPG WEBP AVIF HEIC TIFF GIF BMP ICO PSD RAW …";
    public override string? Tool => "magick";
    public override string? WingetId => "ImageMagick.ImageMagick";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && Is(e, In) && Is(p.Extension, Out) && e != p.Extension;

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var e = Util.Ext(input);
        var dest = Util.Out(input, outDir, p.Extension);
        var args = new List<string>();
        // multi-frame inputs: take the first frame unless the target is an animation too
        args.Add(Is(e, ".gif", ".psd", ".tif", ".tiff", ".ico", ".webp") && p.Extension != ".gif" ? input + "[0]" : input);
        args.Add("-auto-orient");
        if (p.Gray) args.AddRange(new[] { "-colorspace", "Gray" });
        if (Math.Abs(p.Scale - 1) > 0.001) args.AddRange(new[] { "-resize", $"{p.Scale * 100:0.##}%" });
        if (p.Extension == ".ico") args.AddRange(new[] { "-resize", "256x256>", "-define", "icon:auto-resize=256,128,64,48,32,16" });
        if (Is(p.Extension, ".jpg", ".webp", ".avif", ".heic", ".jp2")) args.AddRange(new[] { "-quality", p.Quality.ToString() });
        if (p.Extension == ".jpg") args.AddRange(new[] { "-background", "white", "-alpha", "remove" });
        args.Add(dest);
        Proc.Run(Exe, args);
        return new[] { dest };
    }
}

public sealed class GhostscriptEngine : EngineBase
{
    public override string Id => "ghostscript";
    public override string Name => "Ghostscript (EPS and PostScript)";
    public override string Category => "Vector";
    public override string Description => "EPS, PostScript and older Illustrator files to PDF and images; PDF to EPS and PS; shrinks big PDFs.";
    public override string Formats => "EPS PS AI → PDF PNG JPG · PDF → EPS PS · smaller PDF";
    public override string? Tool => "gs";
    public override string? WingetId => null;   // not in winget; installed from the Ghostscript GitHub release

    public override bool CanHandle(string e, Preset p)
    {
        if (p.Mode == "compress") return e == ".pdf";
        if (p.Mode != "") return false;
        return (Is(e, ".eps", ".ps", ".ai") && Is(p.Extension, ".pdf", ".png", ".jpg"))
            || (e == ".pdf" && Is(p.Extension, ".eps", ".ps"));
    }

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var compress = p.Mode == "compress";
        var dest = Util.Out(input, outDir, p.Extension, compress ? " (small)" : "");
        var dev = p.Extension switch { ".png" => "png16m", ".jpg" => "jpeg", ".eps" => "eps2write", ".ps" => "ps2write", _ => "pdfwrite" };
        var args = new List<string> { "-dBATCH", "-dNOPAUSE", "-dSAFER", "-q", "-sDEVICE=" + dev };
        if (dev is "png16m" or "jpeg") args.Add("-r150");
        if (compress) args.Add("-dPDFSETTINGS=/ebook");
        if (Util.Ext(input) == ".eps") args.Add("-dEPSCrop");
        args.Add("-sOutputFile=" + dest);
        args.Add(input);
        Proc.Run(Exe, args);
        return new[] { dest };
    }
}

public sealed class TesseractEngine : EngineBase
{
    private static readonly string[] In = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif" };

    public override string Id => "tesseract";
    public override string Name => "Tesseract (text recognition)";
    public override string Category => "Text";
    public override string Description => "Recognizes the text of scans and pictures (OCR): text file or searchable PDF.";
    public override string Formats => "PNG JPG TIFF BMP GIF WEBP → TXT, searchable PDF";
    public override string? Tool => "tesseract";
    public override string? WingetId => "UB-Mannheim.TesseractOCR";

    public override bool CanHandle(string e, Preset p) => p.Mode == "ocr" && Is(e, In) && Is(p.Extension, ".txt", ".pdf");

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, p.Extension);
        var baseName = Path.Combine(Path.GetDirectoryName(dest)!, Path.GetFileNameWithoutExtension(dest));
        var args = new List<string> { input, baseName };
        if (p.Extension == ".pdf") args.Add("pdf");
        Proc.Run(Exe, args);
        return new[] { dest };
    }
}
