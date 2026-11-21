using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mochi.Services.Engines;

public sealed class FfmpegEngine : EngineBase
{
    private static readonly string[] Audio = { ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".opus", ".aac", ".wma", ".aiff", ".amr" };
    private static readonly string[] Video = { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".m4v", ".ts", ".3gp", ".mpg", ".mpeg" };

    public override string Id => "ffmpeg";
    public override string Name => "FFmpeg (video and audio)";
    public override string Category => "Video";
    public override string Description => "Video and audio: MP4, MOV, MKV, WEBM, MP3, M4A, WAV, FLAC and more. Also extracts sound and subtitles, and makes GIF animations and still pictures.";
    public override string Formats => "MP4 MOV MKV WEBM AVI · MP3 M4A WAV FLAC OGG · GIF · sound · subtitles · stills";
    public override string? Tool => "ffmpeg";
    public override string? WingetId => "Gyan.FFmpeg";

    public override bool CanHandle(string e, Preset p)
    {
        if (p.Mode.StartsWith("crop:")) return Is(e, Video);
        var media = Is(e, Audio) || Is(e, Video) || e == ".gif";
        if (!media || (e == ".gif" && Is(p.Extension, Audio))) return false;   // a GIF has no sound
        return p.Mode switch
        {
            "" => (Is(p.Extension, Audio) || Is(p.Extension, ".mp4", ".webm", ".mkv", ".mov", ".avi")) && e != p.Extension
                  && !(Is(e, Audio) && !Is(p.Extension, Audio)),
            "gif" => !Is(e, Audio),
            "audio" => !Is(e, Audio) && e != ".gif",
            "frame" => !Is(e, Audio),
            "subs" => Is(e, ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".ts"),
            _ => false,
        };
    }

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        if (p.Mode.StartsWith("crop:")) return new[] { Crop(input, p.Mode[5..], outDir) };
        var dest = Util.Out(input, outDir, p.Extension);
        var a = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input };
        switch (p.Mode)
        {
            case "gif":
                a.AddRange(new[] { "-vf", "fps=12,scale=480:-1:flags=lanczos", "-loop", "0" });
                break;
            case "audio":
                a.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" });
                break;
            case "frame":
                a.AddRange(new[] { "-frames:v", "1" });
                break;
            case "subs":
                a.AddRange(new[] { "-map", "0:s:0" });
                break;
            default:
                a.AddRange(Codec(p.Extension));
                break;
        }
        a.Add(dest);
        Proc.Run(Exe, a);
        return new[] { dest };
    }

    /// <summary>Centre crop to an aspect ratio, or the black bars found by cropdetect.</summary>
    private string Crop(string input, string spec, string outDir)
    {
        var e = Util.Ext(input);
        var ext = Is(e, ".mp4", ".mov", ".mkv", ".webm") ? e : ".mp4";
        string filter;
        if (spec == "auto")
        {
            var log = Proc.RunCapture(Exe, new[] { "-hide_banner", "-ss", "1", "-i", input, "-t", "6", "-vf", "cropdetect=24:16:0", "-f", "null", "-" }).Err;
            var m = Regex.Matches(log, @"crop=(\d+:\d+:\d+:\d+)");
            if (m.Count == 0) throw new InvalidOperationException("No black bars found");
            filter = "crop=" + m[^1].Groups[1].Value;
        }
        else
        {
            var r = spec.Split(':');
            var ratio = (double.Parse(r[0], CultureInfo.InvariantCulture) / double.Parse(r[1], CultureInfo.InvariantCulture)).ToString("0.######", CultureInfo.InvariantCulture);
            filter = $"crop=w='trunc(if(gt(iw/ih,{ratio}),ih*{ratio},iw)/2)*2':h='trunc(if(gt(iw/ih,{ratio}),ih,iw/{ratio})/2)*2'";
        }
        var dest = Util.Out(input, outDir, ext, " (cropped)");
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input, "-vf", filter };
        args.AddRange(Codec(ext == ".webm" ? ".webm" : ".mp4"));
        args.Add(dest);
        Proc.Run(Exe, args);
        return dest;
    }

    private static string[] Codec(string ext) => ext switch
    {
        ".mp3" => new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
        ".m4a" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
        ".wav" => new[] { "-vn", "-c:a", "pcm_s16le" },
        ".flac" => new[] { "-vn", "-c:a", "flac" },
        ".ogg" => new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
        ".opus" => new[] { "-vn", "-c:a", "libopus", "-b:a", "128k" },
        ".aac" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
        ".webm" => new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-c:a", "libopus" },
        ".avi" => new[] { "-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame" },
        _ => new[] { "-c:v", "libx264", "-crf", "23", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart" },
    };
}

/// <summary>Subtitles into each other: SRT, WebVTT, ASS/SSA, LRC, MicroDVD, saved as UTF-8.</summary>
public sealed class SubtitleEngine : EngineBase
{
    private sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    public override string Id => "subtitle";
    public override string Name => "Subtitles (built-in)";
    public override string Category => "Subtitle";
    public override string Description => "Subtitles: SRT, ASS/SSA, WebVTT, LRC and MicroDVD into each other, saved as UTF-8. Older Korean files (CP949) are read correctly.";
    public override string Formats => "SRT ASS SSA VTT LRC SUB → SRT VTT ASS LRC TXT";

    public override bool CanHandle(string e, Preset p) =>
        p.Mode == "" && Is(e, ".srt", ".vtt", ".ass", ".ssa", ".lrc", ".sub") && Is(p.Extension, ".srt", ".vtt", ".ass", ".lrc", ".txt") && e != p.Extension;

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var cues = Parse(Util.Ext(input), Util.ReadText(input));
        var dest = Util.Out(input, outDir, p.Extension);
        Util.WriteText(dest, Write(p.Extension, cues));
        return new[] { dest };
    }

    /// <summary>Same subtitle format out as in, with every cue's text passed through <paramref name="map"/> (used by translation).</summary>
    public static string MapText(string ext, string text, Func<string, string> map) =>
        Write(ext, Parse(ext, text).Select(c => c with { Text = map(c.Text) }).ToList());

    private static List<Cue> Parse(string ext, string text)
    {
        var cues = new List<Cue>();
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        switch (ext)
        {
            case ".srt":
            case ".vtt":
                foreach (var block in Regex.Split(text, @"\n\s*\n"))
                {
                    var lines = block.Split('\n').Where(l => l.Length > 0).ToList();
                    var t = lines.FindIndex(l => l.Contains("-->"));
                    if (t < 0) continue;
                    var m = Regex.Match(lines[t], @"([\d:.,]+)\s*-->\s*([\d:.,]+)");
                    if (!m.Success) continue;
                    cues.Add(new Cue(Time(m.Groups[1].Value), Time(m.Groups[2].Value), string.Join("\n", lines.Skip(t + 1))));
                }
                break;
            case ".ass":
            case ".ssa":
                foreach (var l in text.Split('\n').Where(l => l.StartsWith("Dialogue:")))
                {
                    var f = l[9..].Split(',', 10);
                    if (f.Length < 10) continue;
                    var body = Regex.Replace(f[9], @"\{[^}]*\}", "").Replace("\\N", "\n").Replace("\\n", "\n");
                    cues.Add(new Cue(Time(f[1]), Time(f[2]), body));
                }
                break;
            case ".lrc":
                var lrc = new List<(TimeSpan t, string s)>();
                foreach (var l in text.Split('\n'))
                {
                    var m = Regex.Match(l, @"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$");
                    if (m.Success) lrc.Add((TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)), m.Groups[3].Value.Trim()));
                }
                for (var i = 0; i < lrc.Count; i++)
                    cues.Add(new Cue(lrc[i].t, i + 1 < lrc.Count ? lrc[i + 1].t : lrc[i].t + TimeSpan.FromSeconds(4), lrc[i].s));
                break;
            case ".sub":   // MicroDVD {start}{end}text at 23.976 fps
                foreach (var l in text.Split('\n'))
                {
                    var m = Regex.Match(l, @"^\{(\d+)\}\{(\d+)\}(.*)$");
                    if (m.Success) cues.Add(new Cue(TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value) / 23.976), TimeSpan.FromSeconds(int.Parse(m.Groups[2].Value) / 23.976), m.Groups[3].Value.Replace("|", "\n")));
                }
                break;
        }
        return cues;
    }

    private static TimeSpan Time(string s)
    {
        var m = Regex.Match(s.Trim().Replace(',', '.'), @"^(?:(\d+):)?(\d+):(\d+)(?:\.(\d+))?$");
        if (!m.Success) return TimeSpan.Zero;
        var frac = m.Groups[4].Success ? double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        return new TimeSpan(0, m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) + TimeSpan.FromSeconds(frac);
    }

    private static string Stamp(TimeSpan t, char ms) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{ms}{t.Milliseconds:000}";

    private static string Write(string ext, List<Cue> cues)
    {
        var sb = new StringBuilder();
        switch (ext)
        {
            case ".srt":
                for (var i = 0; i < cues.Count; i++)
                    sb.AppendLine($"{i + 1}\n{Stamp(cues[i].Start, ',')} --> {Stamp(cues[i].End, ',')}\n{cues[i].Text}\n");
                break;
            case ".vtt":
                sb.AppendLine("WEBVTT\n");
                foreach (var c in cues) sb.AppendLine($"{Stamp(c.Start, '.')} --> {Stamp(c.End, '.')}\n{c.Text}\n");
                break;
            case ".ass":
                sb.AppendLine("[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour\nStyle: Default,Arial,20,&H00FFFFFF\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
                foreach (var c in cues)
                    sb.AppendLine($"Dialogue: 0,{(int)c.Start.TotalHours}:{c.Start:mm\\:ss}.{c.Start.Milliseconds / 10:00},{(int)c.End.TotalHours}:{c.End:mm\\:ss}.{c.End.Milliseconds / 10:00},Default,,0,0,0,,{c.Text.Replace("\n", "\\N")}");
                break;
            case ".lrc":
                foreach (var c in cues) sb.AppendLine($"[{(int)c.Start.TotalMinutes:00}:{c.Start.Seconds:00}.{c.Start.Milliseconds / 10:00}]{c.Text.Replace("\n", " ")}");
                break;
            default:
                foreach (var c in cues) sb.AppendLine(c.Text);
                break;
        }
        return sb.ToString();
    }
}
