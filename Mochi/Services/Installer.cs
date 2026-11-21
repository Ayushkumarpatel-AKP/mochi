using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace Mochi.Services;

/// <summary>Installs external engines: winget for most, the GitHub release for Ghostscript (it is not in winget).</summary>
public static class Installer
{
    public static bool CanInstall(IEngine e) => e.WingetId is not null || e.Id is "ghostscript" or "whisper";

    public static async Task InstallAsync(IEngine e, Action<string> status)
    {
        if (e.WingetId is not null)
        {
            status(Loc.T("x.installing"));
            await RunAsync("winget", new[] { "install", "--id", e.WingetId, "-e", "--silent", "--accept-package-agreements", "--accept-source-agreements" });
        }
        else if (e.Id == "whisper")
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mochi");
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi", "engines", "whisper");
            Directory.CreateDirectory(dir);
            status("Downloading whisper.cpp from GitHub…");
            var zip = Path.Combine(dir, "w.zip");
            await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync("https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/whisper-bin-x64.zip"));
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, true);
            File.Delete(zip);
            status("Downloading the speech model (148 MB)…");
            await using var src = await http.GetStreamAsync("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin");
            await using var dst = File.Create(Path.Combine(dir, "ggml-base.bin"));
            await src.CopyToAsync(dst);
        }
        else if (e.Id == "ghostscript")
        {
            status("Looking up the latest Ghostscript on GitHub…");
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mochi");
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/ArtifexSoftware/ghostpdl-downloads/releases/latest"));
            var url = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString()!)
                .First(u => u.EndsWith("w64.exe", StringComparison.OrdinalIgnoreCase));
            var setup = Path.Combine(Path.GetTempPath(), "gs-setup.exe");
            status("Downloading Ghostscript…");
            await File.WriteAllBytesAsync(setup, await http.GetByteArrayAsync(url));
            status("Installing…");
            await RunAsync(setup, new[] { "/S" });
        }
        Tools.Reset();
    }

    private static async Task RunAsync(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start " + exe);
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 && p.ExitCode != unchecked((int)0x8A150101))   // 0x8A150101: winget "already installed"
            throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exe)} exited with {p.ExitCode}");
    }
}
