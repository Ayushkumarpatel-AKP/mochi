using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;

namespace Mochi.Services.Engines;

/// <summary>Asks the user for a few values (password, page range, clip times) through a small dialog; remembers the answer briefly so a batch asks once.</summary>
public static class Prompts
{
    public static Func<string, string[], string[], bool[], Task<string[]?>>? Hook { get; set; }
    private static (string Key, DateTime At, string[] Vals)? _last;

    public static string[] Ask(string key, string title, string[] labels, string[] defaults, bool[]? secret = null)
    {
        if (_last is { } l && l.Key == key && DateTime.UtcNow - l.At < TimeSpan.FromSeconds(45)) return l.Vals;
        if (Hook is null) throw new InvalidOperationException("This needs the app window");
        var v = Hook(title, labels, defaults, secret ?? new bool[labels.Length]).GetAwaiter().GetResult()
                ?? throw new OperationCanceledException("Cancelled");
        _last = (key, DateTime.UtcNow, v);
        return v;
    }
}

/// <summary>Video and audio presets on top of FFmpeg: fit a size, compress, mute, flip, rotate, merge, cut a clip, pictures to GIF.</summary>
public sealed class FfmpegPlusEngine : EngineBase
{
    private static readonly string[] Video = { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".m4v", ".ts", ".3gp", ".mpg", ".mpeg" };
    private static readonly string[] Audio = { ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".opus", ".aac", ".wma", ".aiff", ".amr" };
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public override string Id => "ffmpeg-plus";
    public override string Name => "Video and audio presets (FFmpeg)";
    public override string Category => "Video";
    public override string Description => "Video that fits Discord, WhatsApp or e-mail limits, compress video and audio, mute, flip, rotate, merge videos or audio, cut a clip, and pictures into a GIF animation.";
    public override string Formats => "video → size-limited MP4 · compress · mute · flip · rotate · merge · clip · pictures → GIF";
    public override string? Tool => "ffmpeg";
    public override string? WingetId => "Gyan.FFmpeg";

    public override bool CanHandle(string e, Preset p)
    {
        var vid = Is(e, Video); var aud = Is(e, Audio);
        var m = p.Mode;
        if (m.StartsWith("fit:") || m is "mute") return vid;
        if (m == "compress") return (vid && p.Extension == ".mp4") || (aud && p.Extension == ".mp3");
        if (m.StartsWith("flip:") || m.StartsWith("irot:")) return vid;
        if (m == "merge-av") return (vid && p.Extension == ".mp4") || (aud && p.Extension == ".mp3");
        if (m == "clip") return vid || aud;
        if (m == "gifanim") return Is(e, Img);
        return false;
    }

    // ---- helpers
    private (double Seconds, bool HasAudio, int W, int H) Probe(string input)
    {
        var log = Proc.RunCapture(Exe, new[] { "-hide_banner", "-i", input }).Err;
        var d = Regex.Match(log, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        var sec = d.Success ? int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        var v = Regex.Match(log, @"Video:.*?(\d{2,5})x(\d{2,5})");
        return (sec, log.Contains("Audio:"), v.Success ? int.Parse(v.Groups[1].Value) : 0, v.Success ? int.Parse(v.Groups[2].Value) : 0);
    }

    private static string[] AudioCodec(string ext) => ext switch
    {
        ".mp3" => new[] { "-c:a", "libmp3lame", "-q:a", "2" },
        ".m4a" or ".aac" => new[] { "-c:a", "aac", "-b:a", "192k" },
        ".wav" => new[] { "-c:a", "pcm_s16le" },
        ".flac" => new[] { "-c:a", "flac" },
        ".ogg" => new[] { "-c:a", "libvorbis", "-q:a", "5" },
        ".opus" => new[] { "-c:a", "libopus", "-b:a", "128k" },
        _ => new[] { "-c:a", "libmp3lame", "-q:a", "2" },
    };

    private static readonly string[] X264 = { "-c:v", "libx264", "-crf", "21", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" };

    private void Run(IEnumerable<string> args) => Proc.Run(Exe, new[] { "-y", "-hide_banner", "-loglevel", "error" }.Concat(args));

    // ---- one input
    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var e = Util.Ext(input); var m = p.Mode;
        if (m.StartsWith("fit:")) return new[] { Fit(input, p, outDir) };
        if (m == "gifanim") return ConvertMany(new[] { input }, p, outDir);

        if (m == "clip") return new[] { Clip(input, outDir) };
        string dest;
        switch (m)
        {
            case "compress" when p.Extension == ".mp3":
                dest = Util.Out(input, outDir, ".mp3", " (small)");
                Run(new[] { "-i", input, "-vn", "-c:a", "libmp3lame", "-b:a", "96k", "-ac", "2", dest });
                return new[] { dest };
            case "compress":
                dest = Util.Out(input, outDir, ".mp4", " (small)");
                Run(new[] { "-i", input, "-vf", "scale=-2:'min(1080,ih)'", "-c:v", "libx264", "-crf", "28", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", dest });
                return new[] { dest };
            case "mute":
                dest = Util.Out(input, outDir, Is(e, ".mp4", ".mov", ".mkv", ".webm") ? e : ".mp4", " (muted)");
                Run(new[] { "-i", input, "-c:v", "copy", "-an", dest });
                return new[] { dest };
            default:   // flip and rotate
                var ext = Is(e, ".mp4", ".mov", ".mkv") ? e : ".mp4";
                var vf = m switch
                {
                    "flip:h" => "hflip", "flip:v" => "vflip", "irot:90" => "transpose=1", "irot:270" => "transpose=2", _ => "hflip,vflip",
                };
                dest = Util.Out(input, outDir, ext, m.StartsWith("flip") ? " (flipped)" : " (rotated)");
                Run(new[] { "-i", input, "-vf", vf }.Concat(X264).Append(dest));
                return new[] { dest };
        }
    }

    /// <summary>Two-pass H.264 aimed at a file size, scaled down to a height limit.</summary>
    private string Fit(string input, Preset p, string outDir)
    {
        var parts = p.Mode.Split(':');
        var mb = double.Parse(parts[1], CultureInfo.InvariantCulture); var maxH = int.Parse(parts[2]);
        var info = Probe(input);
        if (info.Seconds <= 0) throw new InvalidOperationException("Cannot read the video length");
        var audio = info.HasAudio ? 96_000 : 0;
        var vb = Math.Max(80_000, (int)(mb * 1024 * 1024 * 8 * 0.90 / info.Seconds) - audio);
        var dest = Util.Out(input, outDir, ".mp4", $" ({mb:0.#} MB)");
        var tmp = Util.TempDir();
        try
        {
            var log = Path.Combine(tmp, "pass");
            var vf = $"scale=-2:'min({maxH},ih)'";
            Run(new[] { "-i", input, "-vf", vf, "-c:v", "libx264", "-b:v", vb.ToString(), "-pass", "1", "-passlogfile", log, "-an", "-f", "null", "NUL" });
            var a = new List<string> { "-i", input, "-vf", vf, "-c:v", "libx264", "-b:v", vb.ToString(), "-maxrate", (vb * 13 / 10).ToString(), "-bufsize", (vb * 2).ToString(), "-pass", "2", "-passlogfile", log, "-pix_fmt", "yuv420p" };
            if (info.HasAudio) a.AddRange(new[] { "-c:a", "aac", "-b:a", "96k" }); else a.Add("-an");
            a.AddRange(new[] { "-movflags", "+faststart", dest });
            Run(a);
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
        return dest;
    }

    private string Clip(string input, string outDir)
    {
        var e = Util.Ext(input);
        var v = Prompts.Ask("clip", Loc.T("x.clip.title"), new[] { Loc.T("x.clip.start"), Loc.T("x.clip.end"), Loc.T("x.clip.fast") }, new[] { "0:00", "", "no" });
        var vid = Is(e, Video);
        var ext = vid ? (Is(e, ".mp4", ".mov", ".mkv", ".webm") ? e : ".mp4") : (Is(e, ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".opus") ? e : ".mp3");
        var dest = Util.Out(input, outDir, ext, " (clip)");
        var a = new List<string> { "-i", input, "-ss", Clock(v[0]) };
        if (!string.IsNullOrWhiteSpace(v[1])) a.AddRange(new[] { "-to", Clock(v[1]) });
        if (v[2].Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) || v[2].Trim() == "1") a.AddRange(new[] { "-c", "copy" });
        else a.AddRange(vid ? X264 : AudioCodec(ext));
        a.Add(dest);
        Run(a);
        return dest;
    }

    /// <summary>"90", "1:30" or "0:01:30.5" as seconds for FFmpeg.</summary>
    private static string Clock(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return "0";
        var parts = s.Split(':').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return parts.Aggregate(0.0, (acc, x) => acc * 60 + x).ToString(CultureInfo.InvariantCulture);
    }

    // ---- several inputs
    public override IReadOnlyList<string> ConvertMany(IReadOnlyList<string> inputs, Preset p, string outDir)
    {
        if (p.Mode == "gifanim")
        {
            var tmp = Util.TempDir();
            try
            {
                var list = Path.Combine(tmp, "list.txt");
                var sb = new StringBuilder();
                foreach (var f in inputs) sb.Append("file '").Append(Path.GetFullPath(f).Replace('\\', '/').Replace("'", "'\\''")).AppendLine("'").AppendLine("duration 0.6");
                sb.Append("file '").Append(Path.GetFullPath(inputs[^1]).Replace('\\', '/').Replace("'", "'\\''")).AppendLine("'");
                File.WriteAllText(list, sb.ToString());
                var gif = Util.Out(inputs[0], outDir, ".gif", " (animation)");
                Run(new[] { "-f", "concat", "-safe", "0", "-i", list, "-vf", "scale=480:-1:flags=lanczos:force_original_aspect_ratio=decrease,pad=480:ih:(ow-iw)/2:0,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0", gif });
                return new[] { gif };
            }
            finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
        }

        // merge: every piece is brought to the first one's size, frame rate and sound layout, then joined
        var audioOnly = Is(Util.Ext(inputs[0]), Audio);
        var infos = inputs.Select(Probe).ToList();
        var keepAudio = infos.All(i => i.HasAudio);
        int W = infos[0].W / 2 * 2, H = infos[0].H / 2 * 2;
        var a = new List<string>();
        foreach (var f in inputs) { a.Add("-i"); a.Add(f); }
        var fc = new StringBuilder();
        for (var i = 0; i < inputs.Count; i++)
        {
            if (!audioOnly) fc.Append($"[{i}:v]scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30[v{i}];");
            if (keepAudio || audioOnly) fc.Append($"[{i}:a]aresample=44100,aformat=channel_layouts=stereo[a{i}];");
        }
        for (var i = 0; i < inputs.Count; i++) fc.Append(audioOnly ? $"[a{i}]" : keepAudio ? $"[v{i}][a{i}]" : $"[v{i}]");
        fc.Append($"concat=n={inputs.Count}:v={(audioOnly ? 0 : 1)}:a={(keepAudio || audioOnly ? 1 : 0)}");
        fc.Append(audioOnly ? "[a]" : keepAudio ? "[v][a]" : "[v]");
        a.AddRange(new[] { "-filter_complex", fc.ToString() });
        if (!audioOnly) a.AddRange(new[] { "-map", "[v]" });
        if (keepAudio || audioOnly) a.AddRange(new[] { "-map", "[a]" });
        var dest = Util.Out(inputs[0], outDir, audioOnly ? ".mp3" : ".mp4", " (merged)");
        if (audioOnly) a.AddRange(new[] { "-c:a", "libmp3lame", "-q:a", "2" });
        else { a.AddRange(new[] { "-c:v", "libx264", "-crf", "21", "-preset", "medium", "-pix_fmt", "yuv420p" }); if (keepAudio) a.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" }); a.AddRange(new[] { "-movflags", "+faststart" }); }
        a.Add(dest);
        Run(a);
        return new[] { dest };
    }
}

/// <summary>Picture presets in Skia: flip, rotate, resize to a width, shrink under a file size, remove metadata, watermark, stack into a long image.</summary>
public sealed class ImageToolsEngine : EngineBase
{
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public override string Id => "imagetools";
    public override string Name => "Picture tools (built-in)";
    public override string Category => "Image";
    public override string Description => "Flip and rotate pictures, resize to a width (Instagram, HD), shrink under 100 KB to 1 MB for upload forms, remove metadata (EXIF, location), add a text watermark, and stack pictures into one long image.";
    public override string Formats => "PNG JPG WEBP BMP GIF → flipped · rotated · resized · under N KB · clean · watermarked · long image";

    public override bool CanHandle(string e, Preset p) =>
        Is(e, Img) && (p.Mode.StartsWith("flip:") || p.Mode.StartsWith("irot:") || p.Mode.StartsWith("width:") || p.Mode.StartsWith("under:") ||
                       p.Mode is "strip" or "watermark" or "longimg");

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        using var bmp = LoadOriented(input);
        SKBitmap? made = null;
        try
        {
            var m = p.Mode; var suffix = "";
            if (m == "flip:h" || m == "flip:v")
            {
                made = new SKBitmap(bmp.Width, bmp.Height);
                using var c = new SKCanvas(made);
                if (m == "flip:h") { c.Translate(bmp.Width, 0); c.Scale(-1, 1); } else { c.Translate(0, bmp.Height); c.Scale(1, -1); }
                c.DrawBitmap(bmp, 0, 0);
                suffix = " (flipped)";
            }
            else if (m.StartsWith("irot:")) { made = Rotate(bmp, int.Parse(m[5..])); suffix = " (rotated)"; }
            else if (m.StartsWith("width:"))
            {
                var w = int.Parse(m[6..]);
                made = bmp.Width > w ? bmp.Resize(new SKImageInfo(w, Math.Max(1, (int)((long)bmp.Height * w / bmp.Width))), SKFilterQuality.High) : bmp.Copy();
                suffix = $" ({w}px)";
            }
            else if (m == "watermark")
            {
                var v = Prompts.Ask("watermark", Loc.T("x.wm.title"), new[] { Loc.T("x.wm.text") }, new[] { "© " + Environment.UserName });
                made = bmp.Copy();
                using var c = new SKCanvas(made);
                var size = Math.Max(14, made.Width / 22f);
                using var shadow = new SKPaint { TextSize = size, IsAntialias = true, Color = new SKColor(0, 0, 0, 150) };
                using var white = new SKPaint { TextSize = size, IsAntialias = true, Color = new SKColor(255, 255, 255, 215) };
                var x = made.Width - white.MeasureText(v[0]) - size; var y = made.Height - size;
                c.DrawText(v[0], x + 2, y + 2, shadow); c.DrawText(v[0], x, y, white);
                suffix = " (watermark)";
            }
            else if (m == "strip") { made = bmp.Copy(); suffix = " (clean)"; }

            var ext = p.Extension.Length > 0 ? p.Extension : Util.Ext(input) is ".jpg" or ".jpeg" ? ".jpg" : Util.Ext(input) == ".webp" ? ".webp" : ".png";
            var dest = Util.Out(input, outDir, ext, suffix);
            var src = made ?? bmp;
            if (m.StartsWith("under:")) { UnderKb(src, dest, int.Parse(m[6..])); return new[] { dest }; }
            var fmt = ext switch { ".png" => SKEncodedImageFormat.Png, ".webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Jpeg };
            ImageEngine.Save(src, dest, fmt, new Preset("t", "", "", ext, 92));
            return new[] { dest };
        }
        finally { made?.Dispose(); }
    }

    public override IReadOnlyList<string> ConvertMany(IReadOnlyList<string> inputs, Preset p, string outDir)
    {
        var bmps = inputs.Select(LoadOriented).ToList();
        try
        {
            var w = bmps.Max(b => b.Width);
            var scaled = bmps.Select(b => b.Width == w ? b : b.Resize(new SKImageInfo(w, (int)((long)b.Height * w / b.Width)), SKFilterQuality.High)).ToList();
            using var all = new SKBitmap(w, scaled.Sum(b => b.Height));
            using (var c = new SKCanvas(all))
            {
                c.Clear(SKColors.White);
                var y = 0;
                foreach (var b in scaled) { c.DrawBitmap(b, 0, y); y += b.Height; }
            }
            var dest = Util.Out(inputs[0], outDir, ".png", " (long)");
            ImageEngine.Save(all, dest, SKEncodedImageFormat.Png, new Preset("l", "", "", ".png", 100));
            foreach (var s in scaled.Where(s => !bmps.Contains(s))) s.Dispose();
            return new[] { dest };
        }
        finally { foreach (var b in bmps) b.Dispose(); }
    }

    /// <summary>Decodes a picture and applies its EXIF orientation, so phone photos are not sideways.</summary>
    public static SKBitmap LoadOriented(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("Cannot decode " + Path.GetFileName(path));
        var bmp = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Cannot decode " + Path.GetFileName(path));
        var deg = codec.EncodedOrigin switch { SKEncodedOrigin.RightTop => 90, SKEncodedOrigin.BottomRight => 180, SKEncodedOrigin.LeftBottom => 270, _ => 0 };
        if (deg == 0) return bmp;
        var r = Rotate(bmp, deg); bmp.Dispose();
        return r;
    }

    private static SKBitmap Rotate(SKBitmap b, int deg)
    {
        var swap = deg % 180 != 0;
        var r = new SKBitmap(swap ? b.Height : b.Width, swap ? b.Width : b.Height);
        using var c = new SKCanvas(r);
        c.Translate(r.Width / 2f, r.Height / 2f);
        c.RotateDegrees(deg);
        c.Translate(-b.Width / 2f, -b.Height / 2f);
        c.DrawBitmap(b, 0, 0);
        return r;
    }

    /// <summary>JPEG that fits an upload limit: lower the quality step by step, then the size, until it is small enough.</summary>
    private static void UnderKb(SKBitmap src, string dest, int kb)
    {
        var limit = kb * 1024L;
        var bmp = src; var own = false;
        for (var round = 0; round < 12; round++)
        {
            using var flat = new SKBitmap(bmp.Width, bmp.Height);
            using (var c = new SKCanvas(flat)) { c.Clear(SKColors.White); c.DrawBitmap(bmp, 0, 0); }
            using var img = SKImage.FromBitmap(flat);
            int lo = 25, hi = 92; byte[]? best = null;
            while (lo <= hi)
            {
                var q = (lo + hi) / 2;
                using var d = img.Encode(SKEncodedImageFormat.Jpeg, q);
                if (d.Size <= limit) { best = d.ToArray(); lo = q + 1; } else hi = q - 1;
            }
            if (best is not null) { File.WriteAllBytes(dest, best); if (own) bmp.Dispose(); return; }
            var next = bmp.Resize(new SKImageInfo((int)(bmp.Width * 0.8), (int)(bmp.Height * 0.8)), SKFilterQuality.High);
            if (own) bmp.Dispose();
            bmp = next; own = true;
        }
        throw new InvalidOperationException($"Cannot get this picture under {kb} KB");
    }
}

/// <summary>The short video inside an Android Motion Photo (a JPEG with an MP4 attached at the end).</summary>
public sealed class MotionPhotoEngine : EngineBase
{
    public override string Id => "motion";
    public override string Name => "Motion Photo (built-in)";
    public override string Category => "Video";
    public override string Description => "Takes the short video out of an Android Motion Photo (Google Pixel, Samsung).";
    public override string Formats => "Motion Photo JPG → MP4";

    public override bool CanHandle(string e, Preset p) => p.Mode == "motion" && Is(e, ".jpg", ".jpeg");

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var b = File.ReadAllBytes(input);
        var at = -1;
        for (var i = b.Length - 12; i >= 4; i--)
            if (b[i] == 'f' && b[i + 1] == 't' && b[i + 2] == 'y' && b[i + 3] == 'p')
            {
                var size = (b[i - 4] << 24) | (b[i - 3] << 16) | (b[i - 2] << 8) | b[i - 1];
                if (size is >= 16 and < 256) { at = i - 4; break; }
            }
        if (at < 0) throw new InvalidDataException("No video found in this photo (it is not a Motion Photo)");
        var dest = Util.Out(input, outDir, ".mp4");
        File.WriteAllBytes(dest, b[at..]);
        return new[] { dest };
    }
}

/// <summary>Macro-enabled Office files and templates (DOCM, XLSM, PPTM, DOTX…) into plain DOCX, XLSX and PPTX; macros are removed.</summary>
public sealed class OoxmlEngine : EngineBase
{
    private static readonly Dictionary<string, string> Family = new()
    {
        [".docm"] = ".docx", [".dotx"] = ".docx", [".dotm"] = ".docx",
        [".xlsm"] = ".xlsx", [".xltx"] = ".xlsx", [".xltm"] = ".xlsx", [".xlsb"] = "",
        [".pptm"] = ".pptx", [".potx"] = ".pptx", [".potm"] = ".pptx", [".ppsx"] = ".pptx", [".ppsm"] = ".pptx",
    };

    public override string Id => "ooxml";
    public override string Name => "Office Open XML (built-in)";
    public override string Category => "Document";
    public override string Description => "Macro-enabled files and templates (DOCM, XLSM, PPTM, DOTX…) into DOCX, XLSX and PPTX. Macros are removed.";
    public override string Formats => "DOCM DOTX DOTM XLSM XLTX PPTM POTX PPSX → DOCX XLSX PPTX";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && Family.TryGetValue(e, out var f) && f != "" && f == p.Extension;

    private static readonly (string From, string To)[] Types =
    {
        ("application/vnd.ms-word.document.macroEnabled.main+xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
        ("application/vnd.ms-word.template.macroEnabledTemplate.main+xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
        ("application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
        ("application/vnd.ms-excel.sheet.macroEnabled.main+xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
        ("application/vnd.ms-excel.template.macroEnabled.main+xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
        ("application/vnd.openxmlformats-officedocument.spreadsheetml.template.main+xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
        ("application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
        ("application/vnd.ms-powerpoint.template.macroEnabled.main+xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
        ("application/vnd.ms-powerpoint.slideshow.macroEnabled.main+xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
        ("application/vnd.openxmlformats-officedocument.presentationml.template.main+xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
        ("application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
    };

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, p.Extension);
        using var src = ZipFile.OpenRead(input);
        using var dst = ZipFile.Open(dest, ZipArchiveMode.Create);
        foreach (var en in src.Entries)
        {
            var n = en.FullName;
            if (n.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) || n.EndsWith("vbaData.xml", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("vbaProjectSignature.bin", StringComparison.OrdinalIgnoreCase)) continue;
            var o = dst.CreateEntry(n, CompressionLevel.Optimal);
            using var i = en.Open(); using var w = o.Open();
            if (n == "[Content_Types].xml" || n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                string text;
                using (var sr = new StreamReader(i, Encoding.UTF8)) text = sr.ReadToEnd();
                foreach (var (from, to) in Types) text = text.Replace(from, to);
                text = Regex.Replace(text, @"<(Override|Relationship)\b[^>]*vba[^>]*/>", "", RegexOptions.IgnoreCase);
                var b = new UTF8Encoding(false).GetBytes(text);
                w.Write(b, 0, b.Length);
            }
            else i.CopyTo(w);
        }
        return new[] { dest };
    }
}

/// <summary>EPUB to Kobo's KEPUB: every sentence is wrapped in a koboSpan so Kobo e-readers can track reading position and stats.</summary>
public sealed class KepubEngine : EngineBase
{
    public override string Id => "kepub";
    public override string Name => "KEPUB for Kobo (built-in)";
    public override string Category => "Ebook";
    public override string Description => "EPUB to KEPUB for Kobo e-readers: sentences get Kobo's reading-position markers.";
    public override string Formats => "EPUB → KEPUB (.kepub.epub)";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && e == ".epub" && p.Extension == ".kepub.epub";

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, ".kepub.epub");
        using var src = ZipFile.OpenRead(input);
        using var dst = ZipFile.Open(dest, ZipArchiveMode.Create);
        // mimetype must be the first entry and stored uncompressed
        var mt = src.GetEntry("mimetype");
        if (mt is not null) { using var w = dst.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(); using var r = mt.Open(); r.CopyTo(w); }

        var part = 0;
        foreach (var en in src.Entries.Where(x => x.FullName != "mimetype" && !x.FullName.EndsWith("/")))
        {
            using var i = en.Open(); using var w = dst.CreateEntry(en.FullName, CompressionLevel.Optimal).Open();
            if (Util.Ext(en.Name) is ".xhtml" or ".html" or ".htm")
            {
                var doc = XDocument.Load(i);
                Spans(doc, ++part);
                var xml = new XDeclaration("1.0", "utf-8", null) + "\n" + doc.ToString(SaveOptions.DisableFormatting);
                var b = new UTF8Encoding(false).GetBytes(xml);
                w.Write(b, 0, b.Length);
            }
            else i.CopyTo(w);
        }
        return new[] { dest };
    }

    private static void Spans(XDocument doc, int part)
    {
        var ns = doc.Root!.Name.Namespace;
        var para = 0;
        foreach (var block in doc.Descendants().Where(x => x.Name.LocalName is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li").ToList())
        {
            para++; var sent = 0;
            foreach (var t in block.DescendantNodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value) && t.Parent?.Name.LocalName != "span" || t.Parent?.Attribute("class")?.Value != "koboSpan").ToList())
            {
                var pieces = Regex.Split(t.Value, @"(?<=[.!?…])\s+").Where(s => s.Length > 0).ToList();
                var nodes = pieces.Select((s, k) => (object)new XElement(ns + "span", new XAttribute("class", "koboSpan"), new XAttribute("id", $"kobo.{part}.{para}.{++sent}"), s + (k < pieces.Count - 1 ? " " : (t.Value.EndsWith(" ") ? " " : "")))).ToList();
                t.ReplaceWith(nodes);
            }
        }
    }
}

/// <summary>3D formats through Assimp: GLB, glTF, 3MF, FBX and Collada in and out, next to the STL, OBJ and PLY the built-in engine handles.</summary>
public sealed class AssimpEngine : EngineBase
{
    private static readonly string[] Modern = { ".glb", ".gltf", ".3mf", ".fbx", ".dae" };
    private static readonly string[] In = { ".glb", ".gltf", ".3mf", ".fbx", ".dae", ".3ds", ".stl", ".obj", ".ply" };
    private static readonly Dictionary<string, string> OutId = new()
    {
        [".glb"] = "glb2", [".gltf"] = "gltf2", [".3mf"] = "3mf", [".fbx"] = "fbx", [".dae"] = "collada", [".stl"] = "stlb", [".obj"] = "obj", [".ply"] = "plyb",
    };

    public override string Id => "assimp";
    public override string Name => "3D models: GLB, glTF, 3MF, FBX (Assimp)";
    public override string Category => "Model3D";
    public override string Description => "3D models: GLB, glTF, 3MF, FBX and Collada into each other and into STL, OBJ and PLY; FBX can be read into any of them.";
    public override string Formats => "GLB GLTF 3MF FBX DAE 3DS STL OBJ PLY ↔ GLB GLTF 3MF FBX DAE STL OBJ PLY";

    public override bool CanHandle(string e, Preset p) =>
        p.Mode == "" && Is(e, In) && OutId.ContainsKey(p.Extension) && e != p.Extension && (Is(e, Modern) || Is(p.Extension, Modern));

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        using var ctx = new Assimp.AssimpContext();
        var scene = ctx.ImportFile(input, Assimp.PostProcessSteps.Triangulate | Assimp.PostProcessSteps.JoinIdenticalVertices | Assimp.PostProcessSteps.GenerateNormals);
        var dest = Util.Out(input, outDir, p.Extension);
        if (!ctx.ExportFile(scene, dest, OutId[p.Extension])) throw new InvalidOperationException("Assimp could not write " + p.Extension);
        return new[] { dest };
    }
}
