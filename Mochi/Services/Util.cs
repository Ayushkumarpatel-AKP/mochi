using System.Diagnostics;
using System.Text;

namespace Mochi.Services;

public static class Util
{
    public static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var d = Path.GetDirectoryName(path)!;
        var n = Path.GetFileNameWithoutExtension(path);
        var e = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var c = Path.Combine(d, $"{n} ({i}){e}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
    }

    /// <summary>A free output path "name[suffix].ext" in outDir (created if missing).</summary>
    public static string Out(string input, string outDir, string ext, string suffix = "")
    {
        Directory.CreateDirectory(outDir);
        var name = Path.GetFileNameWithoutExtension(input.TrimEnd('\\', '/'));
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return Unique(Path.Combine(outDir, name + suffix + ext));
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Mochi", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Reads text as UTF-8 (with BOM detection) and falls back to CP949 for old Korean files.</summary>
    public static string ReadText(string path)
    {
        var b = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(949).GetString(b); }
            catch { return Encoding.Latin1.GetString(b); }
        }
    }

    public static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
}

/// <summary>Finds external programs (FFmpeg, ImageMagick…) in the user's override, PATH, or the usual install folders.</summary>
public static class Tools
{
    private static readonly Dictionary<string, string?> Cache = new();

    public static void Reset() => Cache.Clear();

    public static string? Find(string key)
    {
        if (Cache.TryGetValue(key, out var hit)) return hit;
        return Cache[key] = Locate(key);
    }

    private static string? Locate(string key)
    {
        if (App.Settings.ToolPaths.TryGetValue(key, out var own) && File.Exists(own)) return own;

        var exe = key switch
        {
            "ffmpeg" => "ffmpeg.exe", "magick" => "magick.exe", "soffice" => "soffice.exe", "pandoc" => "pandoc.exe",
            "gs" => "gswin64c.exe", "tesseract" => "tesseract.exe", "7z" => "7z.exe", "rhwp" => "rhwp.exe",
            "whisper" => "whisper-cli.exe", "ebook-convert" => "ebook-convert.exe", "ollama" => "ollama.exe", _ => key + ".exe"
        };

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var dirs = new List<string>();
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            dirs.Add(p.Trim('"'));
        dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));

        switch (key)
        {
            case "soffice": dirs.Add(Path.Combine(pf, "LibreOffice", "program")); dirs.Add(Path.Combine(pf86, "LibreOffice", "program")); break;
            case "pandoc": dirs.Add(Path.Combine(local, "Pandoc")); dirs.Add(Path.Combine(pf, "Pandoc")); break;
            case "tesseract": dirs.Add(Path.Combine(pf, "Tesseract-OCR")); break;
            case "7z": dirs.Add(Path.Combine(pf, "7-Zip")); dirs.Add(Path.Combine(pf, "Filee", "engines", "7zip")); break;
            case "rhwp": dirs.Add(Path.Combine(pf, "Filee", "engines", "rhwp")); break;
            case "magick": dirs.AddRange(Glob(pf, "ImageMagick*")); break;
            case "ollama": dirs.Add(Path.Combine(local, "Programs", "Ollama")); break;
            case "ebook-convert": dirs.Add(Path.Combine(pf, "Calibre2")); dirs.Add(Path.Combine(pf86, "Calibre2")); break;
            case "whisper":
                var wdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi", "engines", "whisper");
                if (Directory.Exists(wdir))
                    foreach (var f in Directory.EnumerateFiles(wdir, "whisper-cli.exe", SearchOption.AllDirectories)) return f;
                break;
            case "gs": foreach (var g in Glob(Path.Combine(pf, "gs"), "gs*")) dirs.Add(Path.Combine(g, "bin")); break;
        }
        if (key == "ffmpeg")
        {
            var pk = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pk))
                foreach (var f in Directory.EnumerateFiles(pk, "ffmpeg.exe", SearchOption.AllDirectories)) return f;
        }

        foreach (var d in dirs)
        {
            try { var f = Path.Combine(d, exe); if (File.Exists(f)) return f; } catch { /* bad PATH entry */ }
        }
        return null;
    }

    private static IEnumerable<string> Glob(string root, string pattern) =>
        Directory.Exists(root) ? Directory.GetDirectories(root, pattern) : Array.Empty<string>();
}

public static class Proc
{
    /// <summary>Runs a program and returns what it wrote to stdout and stderr (FFmpeg logs to stderr).</summary>
    public static (string Out, string Err) RunCapture(string exe, IEnumerable<string> args, int timeoutMs = 10 * 60 * 1000)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start " + exe);
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException(Path.GetFileName(exe) + " timed out"); }
        p.WaitForExit();
        return (o.Result, e.Result);
    }

    public static string Run(string exe, IEnumerable<string> args, string? workDir = null, int timeoutMs = 30 * 60 * 1000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start " + exe);
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        var stdout = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException(Path.GetFileName(exe) + " timed out"); }
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            var tail = string.Join(" ", err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)).Trim();
            throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exe)} failed ({p.ExitCode}): {tail}");
        }
        return stdout.Result;
    }
}

public static class Csv
{
    public static List<List<string>> Parse(string text, char sep = ',')
    {
        var rows = new List<List<string>>();
        var row = new List<string>(); var f = new StringBuilder(); var q = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; } else q = false; }
                else f.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == sep) { row.Add(f.ToString()); f.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(f.ToString()); f.Clear(); rows.Add(row); row = new();
            }
            else f.Append(c);
        }
        if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); rows.Add(row); }
        return rows;
    }

    public static string Write(IEnumerable<IEnumerable<string>> rows, char sep = ',')
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append(string.Join(sep, r.Select(v => v.Contains(sep) || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
                ? "\"" + v.Replace("\"", "\"\"") + "\"" : v))).Append("\r\n");
        return sb.ToString();
    }
}
