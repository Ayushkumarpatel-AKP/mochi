using PDFtoImage;
using SkiaSharp;

namespace Mochi.Services;

/// <summary>What the AI is shown of one file: its words, or its picture(s) when it has no words (a photo, a scanned PDF).</summary>
public sealed record AiSource(string Path, string Name, string Ext, string? Text, IReadOnlyList<string> Images, bool Truncated)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Images.Count == 0;
    public bool IsPicture => Images.Count > 0 && string.IsNullOrWhiteSpace(Text);
}

/// <summary>Turns files into something a chat model can read: text from PDF, Word, Excel, PowerPoint, web and plain files; pictures as small JPEGs.</summary>
public static class AiFiles
{
    public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public static bool IsImage(string ext) => ImageExts.Contains(ext);
    public static bool CanRead(string ext) => IsImage(ext) || TextExtractor.HasText(ext);

    /// <param name="maxChars">How many characters of text to keep.</param>
    /// <param name="imageSide">Longest side of pictures sent (smaller = cheaper and faster).</param>
    public static AiSource Load(string path, int maxChars = 60000, int imageSide = 1568)
    {
        var ext = Util.Ext(path);
        var name = System.IO.Path.GetFileName(path);
        if (IsImage(ext))
            return new AiSource(path, name, ext, null, new[] { ImageUrl(path, imageSide) }, false);

        var text = TextExtractor.Read(path, maxChars + 1, deep: true);
        var truncated = text is not null && text.Length > maxChars;
        if (truncated) text = text![..maxChars];

        // a scanned PDF has no words to read: show the model the first pages as pictures instead
        if (ext == ".pdf" && (text is null || text.Length < 200))
        {
            var pages = PdfPages(path, imageSide);
            if (pages.Count > 0) return new AiSource(path, name, ext, text, pages, false);
        }
        return new AiSource(path, name, ext, text, Array.Empty<string>(), truncated);
    }

    /// <summary>A short look at a file for naming it: a little text, or a small picture.</summary>
    public static AiSource Peek(string path) => Load(path, 900, 512);

    private static IReadOnlyList<string> PdfPages(string path, int side)
    {
        var list = new List<string>();
        try
        {
            using var fs = File.OpenRead(path);
            foreach (var bmp in Conversion.ToImages(fs, options: new RenderOptions(Dpi: 110)))
            {
                using (bmp) list.Add(Encode(bmp, side));
                if (list.Count >= 3) break;
            }
        }
        catch { /* protected or damaged: nothing to show */ }
        return list;
    }

    private static string ImageUrl(string path, int side)
    {
        using var bmp = SKBitmap.Decode(path) ?? throw new InvalidDataException("Mochi can't read this picture: " + System.IO.Path.GetFileName(path));
        return Encode(bmp, side);
    }

    /// <summary>A white-backed JPEG no larger than <paramref name="side"/>, as a data: URL.</summary>
    private static string Encode(SKBitmap src, int side)
    {
        var k = Math.Min(1.0, side / (double)Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
        using var dst = new SKBitmap(w, h);
        using (var c = new SKCanvas(dst))
        {
            c.Clear(SKColors.White);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
            c.DrawBitmap(src, new SKRect(0, 0, w, h), paint);
        }
        using var img = SKImage.FromBitmap(dst);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        return "data:image/jpeg;base64," + Convert.ToBase64String(data.ToArray());
    }
}
