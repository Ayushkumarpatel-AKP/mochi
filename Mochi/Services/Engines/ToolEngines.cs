using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Mochi.Services.Engines;

/// <summary>Windows that need the user (annotate, PDF editor) register here; the engines call them and wait.</summary>
public static class ToolHooks
{
    public static Func<string, Task<string?>>? Annotate { get; set; }
    public static Func<string, Task<string?>>? PdfEdit { get; set; }
    public static Func<string, Task<string?>>? Perspective { get; set; }
    public static Func<string, Task<string?>>? SheetStudio { get; set; }
    public static Func<string, Task<string?>>? RemoveBg { get; set; }
    public static Func<string, Task<string?>>? Erase { get; set; }
    public static Func<string, Task<string?>>? Privacy { get; set; }
    public static Func<string, Task<string?>>? Dupes { get; set; }
    public static Func<string, Task<string?>>? SmartSearch { get; set; }
    public static Func<string, Task<string?>>? Organize { get; set; }
    /// <summary>Runs an AI tool ("ai:summary", "ai:ask"…) on the files; returns the files it made (if any).</summary>
    public static Func<string, IReadOnlyList<string>, Task<IReadOnlyList<string>>>? Ai { get; set; }

    /// <summary>The tool the annotate window starts with (Pixelate or Blur for the "hide" preset).</summary>
    public static string? InitialTool { get; set; }
}

/// <summary>Opens the annotate and PDF-editor windows; the result is whatever file the user saves.</summary>
public sealed class UiToolsEngine : EngineBase
{
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public override string Id => "uitools";
    public override string Name => "Picture and PDF tools";
    public override string Category => "Image";
    public override string Description => "Draw arrows, boxes, highlights, text and numbers on a picture; straighten a photographed document by its four corners; remove the background of a picture with an offline AI subject mask or a colour key, then touch up with a brush; reorder, rotate, delete and add the pages of a PDF.";
    public override string Formats => "pictures → annotated, pixelated or straightened PNG · PDF → edited PDF · spreadsheet → printed PDF";

    public override bool CanHandle(string e, Preset p) =>
        (p.Mode.StartsWith("annotate") || p.Mode is "perspective" or "removebg" or "erase") && Is(e, Img)
        || (p.Mode == "privacy" && (Is(e, Img) || e == ".pdf"))
        || (p.Mode is "dupes" or "smartsearch" or "organize" && e == "")
        || (p.Mode == "pdfedit" && e == ".pdf")
        || (p.Mode == "sheetstudio" && Is(e, ".xlsx", ".xlsm", ".xls", ".ods", ".csv"));

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        if (p.Mode.StartsWith("annotate:")) ToolHooks.InitialTool = p.Mode[9..];
        var hook = p.Mode switch
        {
            var m when m.StartsWith("annotate") => ToolHooks.Annotate,
            "perspective" => ToolHooks.Perspective,
            "removebg" => ToolHooks.RemoveBg,
            "erase" => ToolHooks.Erase,
            "privacy" => ToolHooks.Privacy,
            "dupes" => ToolHooks.Dupes,
            "smartsearch" => ToolHooks.SmartSearch,
            "organize" => ToolHooks.Organize,
            "sheetstudio" => ToolHooks.SheetStudio,
            _ => ToolHooks.PdfEdit,
        };
        if (hook is null) throw new InvalidOperationException("This tool needs the app window");
        var saved = hook(input).GetAwaiter().GetResult();
        return saved is null ? Array.Empty<string>() : new[] { saved };
    }
}

/// <summary>Crops pictures to 1:1, 16:9, 4:3, 9:16, 21:9 around the centre, or trims uniform borders ("auto").</summary>
public sealed class CropEngine : EngineBase
{
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public override string Id => "crop";
    public override string Name => "Crop pictures (built-in)";
    public override string Category => "Image";
    public override string Description => "Crop pictures to 1:1, 16:9, 4:3, 9:16 or 21:9, or remove a uniform border found on its own.";
    public override string Formats => "PNG JPG WEBP BMP GIF → cropped PNG JPG WEBP";

    public override bool CanHandle(string e, Preset p) => p.Mode.StartsWith("crop:") && Is(e, Img);

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        using var src = SKBitmap.Decode(input) ?? throw new InvalidDataException("Cannot decode picture");
        var spec = p.Mode[5..];
        var r = spec == "auto" ? Border(src) : Ratio(src, spec);
        using var dst = new SKBitmap(r.Width, r.Height);
        using (var c = new SKCanvas(dst)) c.DrawBitmap(src, r, new SKRect(0, 0, r.Width, r.Height));

        var e = Util.Ext(input);
        var ext = e is ".jpg" or ".jpeg" ? ".jpg" : e == ".webp" ? ".webp" : ".png";
        var fmt = ext switch { ".jpg" => SKEncodedImageFormat.Jpeg, ".webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Png };
        var dest = Util.Out(input, outDir, ext, " (cropped)");
        ImageEngine.Save(dst, dest, fmt, new Preset("crop", "", "", ext, 95));
        return new[] { dest };
    }

    private static SKRectI Ratio(SKBitmap b, string spec)
    {
        var parts = spec.Split(':');
        var target = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
        int w = b.Width, h = b.Height;
        if ((double)w / h > target) w = (int)Math.Round(h * target); else h = (int)Math.Round(w / target);
        var x = (b.Width - w) / 2; var y = (b.Height - h) / 2;
        return new SKRectI(x, y, x + w, y + h);
    }

    /// <summary>Trims rows and columns that all match the corner colour.</summary>
    private static SKRectI Border(SKBitmap b)
    {
        var bg = b.GetPixel(0, 0);
        bool Same(SKColor c) => Math.Abs(c.Red - bg.Red) + Math.Abs(c.Green - bg.Green) + Math.Abs(c.Blue - bg.Blue) + Math.Abs(c.Alpha - bg.Alpha) < 36;
        bool RowBlank(int y) { for (var x = 0; x < b.Width; x++) if (!Same(b.GetPixel(x, y))) return false; return true; }
        bool ColBlank(int x, int y0, int y1) { for (var y = y0; y < y1; y++) if (!Same(b.GetPixel(x, y))) return false; return true; }

        int top = 0, bottom = b.Height;
        while (top < bottom - 1 && RowBlank(top)) top++;
        while (bottom > top + 1 && RowBlank(bottom - 1)) bottom--;
        int left = 0, right = b.Width;
        while (left < right - 1 && ColBlank(left, top, bottom)) left++;
        while (right > left + 1 && ColBlank(right - 1, top, bottom)) right--;
        return new SKRectI(left, top, right, bottom);
    }
}

/// <summary>Several PDF pages on each sheet (2, 4, 6, 8, 9). Ghostscript keeps the pages as vectors, so text stays sharp.</summary>
public sealed class NupEngine : EngineBase
{
    public override string Id => "nup";
    public override string Name => "Pages per sheet (Ghostscript)";
    public override string Category => "Pdf";
    public override string Description => "Puts 2, 4, 6, 8 or 9 PDF pages on each sheet. The pages stay vector, so text stays sharp.";
    public override string Formats => "PDF → PDF with 2 4 6 8 9 pages per sheet";
    public override string? Tool => "gs";
    public override string? WingetId => null;

    public override bool CanHandle(string e, Preset p) => p.Mode.StartsWith("nup:") && e == ".pdf";

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, ".pdf", " (" + p.Mode[4..].Replace("x", "-") + " per sheet)");
        Proc.Run(Exe, new[] { "-dBATCH", "-dNOPAUSE", "-dSAFER", "-q", "-sDEVICE=pdfwrite", "-sNupControl=" + p.Mode[4..], "-sOutputFile=" + dest, input });
        return new[] { dest };
    }
}

/// <summary>Speech to text with whisper.cpp (runs on this PC). Makes SRT captions from audio and video.</summary>
public sealed class WhisperEngine : EngineBase
{
    public override string Id => "whisper";
    public override string Name => "Whisper (captions)";
    public override string Category => "Subtitle";
    public override string Description => "Speech-to-text with OpenAI Whisper (whisper.cpp). Makes SRT captions from video and audio. Nothing leaves your PC.";
    public override string Formats => "MP4 MKV MOV WEBM MP3 WAV M4A FLAC … → SRT";
    public override string? Tool => "whisper";

    public override bool Ready => Tools.Find("whisper") is not null && Tools.Find("ffmpeg") is not null && Model() is not null;

    public static string? Model()
    {
        var w = Tools.Find("whisper");
        return w is null ? null : Directory.GetFiles(Path.GetDirectoryName(w)!, "ggml-*.bin").FirstOrDefault()
            ?? Directory.GetFiles(Path.GetDirectoryName(Path.GetDirectoryName(w)!)!, "ggml-*.bin").FirstOrDefault();
    }

    private static readonly string[] Media =
        { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".opus", ".aac", ".wma" };

    public override bool CanHandle(string e, Preset p) => p.Mode == "whisper" && Is(e, Media);

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var tmp = Util.TempDir();
        try
        {
            var wav = Path.Combine(tmp, "a.wav");
            Proc.Run(Tools.Find("ffmpeg")!, new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", input, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav });
            var baseName = Path.Combine(tmp, "out");
            Proc.Run(Exe, new[] { "-m", Model()!, "-f", wav, "-osrt", "-of", baseName, "-l", "auto" });
            var dest = Util.Out(input, outDir, ".srt");
            File.Copy(baseName + ".srt", dest);
            return new[] { dest };
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
    }
}

/// <summary>Translates subtitles, text, Markdown and Word documents with a local Ollama model. Only cue/paragraph text is sent, to 127.0.0.1.</summary>
public sealed class TranslateEngine : EngineBase
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public override string Id => "translate";
    public override string Name => "Translator (local Ollama)";
    public override string Category => "Text";
    public override string Description => "Translates SRT and WebVTT subtitles, text, Markdown and Word documents with an offline Ollama model. Pick or download the model in General.";
    public override string Formats => "SRT VTT TXT MD DOCX → the same format in another language";
    public override string? Tool => "ollama";
    public override string? WingetId => "Ollama.Ollama";

    public override bool CanHandle(string e, Preset p) => p.Mode.StartsWith("translate:") && Is(e, ".srt", ".vtt", ".txt", ".md", ".docx");

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        if (!Ollama.EnsureRunning()) throw new InvalidOperationException("Ollama will not start");
        var model = Ollama.Best() ?? throw new InvalidOperationException($"No model installed. Download one in General (recommended: {Ollama.Recommended}).");
        var lang = p.Mode[10..];
        var e = Util.Ext(input);
        var dest = Util.Out(input, outDir, e, " (" + lang + ")");
        string Tr(string s) => string.IsNullOrWhiteSpace(s) ? s : Ask(model, lang, s);

        switch (e)
        {
            case ".srt" or ".vtt": Util.WriteText(dest, SubtitleEngine.MapText(e, Util.ReadText(input), Tr)); break;
            case ".docx": DocumentEngine.WriteDocx(dest, DocumentEngine.ReadFile(e, input).Select(b => b.Kind == "table" ? b with { Rows = b.Rows!.Select(r => r.Select(Tr).ToList()).ToList() } : b with { Text = Tr(b.Text) })); break;
            case ".md": Util.WriteText(dest, TranslateMarkdown(Util.ReadText(input), Tr)); break;
            default: Util.WriteText(dest, string.Join("\n\n", Regex.Split(Util.ReadText(input).Replace("\r\n", "\n"), @"\n\s*\n").Select(Tr))); break;
        }
        return new[] { dest };
    }

    /// <summary>Line by line, keeping the Markdown marks (#, -, 1.) outside the text that is sent, so short headings are not mistaken for instructions.</summary>
    private static string TranslateMarkdown(string text, Func<string, string> tr)
    {
        var sb = new StringBuilder(); var inCode = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```")) { inCode = !inCode; sb.AppendLine(raw); continue; }
            if (inCode || string.IsNullOrWhiteSpace(raw)) { sb.AppendLine(raw); continue; }
            var m = Regex.Match(raw, @"^(\s*(?:#{1,6}\s+|[-*+]\s+|\d+[.)]\s+|>\s*)?)(.*)$");
            sb.AppendLine(m.Groups[1].Value + tr(m.Groups[2].Value));
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Ask(string model, string lang, string text)
    {
        var name = new CultureInfo(lang).EnglishName;
        var body = JsonSerializer.Serialize(new
        {
            model, stream = false, options = new { temperature = 0, num_predict = Math.Clamp(text.Length * 3, 48, 2048) },
            messages = new object[]
            {
                new { role = "system", content = $"You are a translation engine. Translate everything the user sends into {name}. Output only the translation: no quotes, no notes, no explanations. If the text is already in {name}, repeat it unchanged." },
                new { role = "user", content = text },
            },
        });
        using var res = Http.PostAsync("http://127.0.0.1:11434/api/chat", new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        res.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var reply = d.RootElement.GetProperty("message").GetProperty("content").GetString()!.Trim();
        // A small model sometimes rambles on a short line; a reply far longer than the source is not a translation.
        return reply.Length > text.Length * 4 + 60 ? text : reply;
    }
}

/// <summary>Kindle formats (MOBI, AZW3) and rare e-book formats through Calibre's ebook-convert.</summary>
public sealed class CalibreEngine : EngineBase
{
    private static readonly string[] In =
        { ".epub", ".mobi", ".azw3", ".azw", ".fb2", ".lit", ".lrf", ".pdb", ".rb", ".chm", ".prc", ".cbz", ".cbr", ".docx", ".txt", ".html", ".htm" };
    private static readonly string[] Out = { ".mobi", ".azw3", ".epub", ".pdf", ".docx", ".txt" };

    public override string Id => "calibre";
    public override string Name => "Calibre (Kindle and rare e-book formats)";
    public override string Category => "Ebook";
    public override string Description => "Saves MOBI and AZW3 for Kindle and converts rare e-book formats (LIT, LRF, PDB, CHM…).";
    public override string Formats => "EPUB MOBI AZW3 FB2 LIT LRF PDB CHM CBZ DOCX … → MOBI AZW3 EPUB PDF DOCX TXT";
    public override string? Tool => "ebook-convert";
    public override string? WingetId => "calibre.calibre";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && Is(e, In) && Is(p.Extension, Out) && e != p.Extension;

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, p.Extension);
        Proc.Run(Exe, new[] { input, dest });
        return new[] { dest };
    }
}

/// <summary>Pictures to SVG by tracing the outline of everything darker than mid-grey.</summary>
public sealed class TraceEngine : EngineBase
{
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public override string Id => "trace";
    public override string Name => "Trace to SVG (built-in)";
    public override string Category => "Vector";
    public override string Description => "Turns a picture into an SVG by tracing the outline of everything darker than mid-grey. Best for logos, line art and signatures.";
    public override string Formats => "PNG JPG WEBP BMP GIF → SVG";

    public override bool CanHandle(string e, Preset p) => p.Mode == "trace" && Is(e, Img);

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        using var src = SKBitmap.Decode(input) ?? throw new InvalidDataException("Cannot decode picture");
        var k = Math.Min(1.0, 600.0 / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
        using var bmp = src.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
        var on = new bool[w, h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                on[x, y] = c.Alpha > 128 && (0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue) < 128;
            }

        var d = new StringBuilder();
        foreach (var loop in Loops(on, w, h))
        {
            d.Append('M').Append(loop[0].X).Append(' ').Append(loop[0].Y);
            for (var i = 1; i < loop.Count; i++) d.Append('L').Append(loop[i].X).Append(' ').Append(loop[i].Y);
            d.Append('Z');
        }
        var dest = Util.Out(input, outDir, ".svg");
        Util.WriteText(dest, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{src.Width}\" height=\"{src.Height}\" viewBox=\"0 0 {w} {h}\">\n<path fill=\"#000\" fill-rule=\"evenodd\" d=\"{d}\"/>\n</svg>\n");
        return new[] { dest };
    }

    /// <summary>Boundary edges between dark and light pixels, joined into closed loops with straight runs merged.</summary>
    private static List<List<(int X, int Y)>> Loops(bool[,] on, int w, int h)
    {
        bool At(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && on[x, y];
        var next = new Dictionary<(int, int), List<(int, int)>>();
        void Edge((int, int) a, (int, int) b) { if (!next.TryGetValue(a, out var l)) next[a] = l = new(); l.Add(b); }

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!on[x, y]) continue;
                if (!At(x, y - 1)) Edge((x, y), (x + 1, y));
                if (!At(x + 1, y)) Edge((x + 1, y), (x + 1, y + 1));
                if (!At(x, y + 1)) Edge((x + 1, y + 1), (x, y + 1));
                if (!At(x - 1, y)) Edge((x, y + 1), (x, y));
            }

        var loops = new List<List<(int X, int Y)>>();
        while (next.Count > 0)
        {
            var start = next.Keys.First(); var cur = start; var pts = new List<(int X, int Y)>();
            do
            {
                pts.Add(cur);
                var outs = next[cur];
                var to = outs[^1]; outs.RemoveAt(outs.Count - 1);
                if (outs.Count == 0) next.Remove(cur);
                cur = to;
            } while (cur != start && next.ContainsKey(cur));

            // drop points that lie on a straight run
            var slim = new List<(int X, int Y)>();
            for (var i = 0; i < pts.Count; i++)
            {
                var a = pts[(i + pts.Count - 1) % pts.Count]; var b = pts[i]; var c = pts[(i + 1) % pts.Count];
                if ((b.X - a.X) * (c.Y - b.Y) != (b.Y - a.Y) * (c.X - b.X)) slim.Add(b);
            }
            if (slim.Count >= 3) loops.Add(slim);
        }
        return loops;
    }
}
