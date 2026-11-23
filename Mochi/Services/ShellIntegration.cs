using System.IO.Pipes;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace Mochi.Services;

/// <summary>"Convert with Mochi" in Explorer's right-click menu, for files and for folders. Written under the current user, so no administrator rights.</summary>
public static class ShellIntegration
{
    private static readonly string[] Roots = { @"Software\Classes\*\shell\Mochi", @"Software\Classes\Directory\shell\Mochi" };

    public static void Set(bool on)
    {
        var exe = Environment.ProcessPath!;
        foreach (var root in Roots)
        {
            if (!on) { Registry.CurrentUser.DeleteSubKeyTree(root, false); continue; }
            using var k = Registry.CurrentUser.CreateSubKey(root);
            k.SetValue("", Loc.TryGet("x.ctx_convert") ?? "Convert with Mochi");
            k.SetValue("Icon", exe);
            using var c = k.CreateSubKey("command");
            c.SetValue("", $"\"{exe}\" --donut \"%1\"");
        }
    }
}

/// <summary>One running Mochi. A second launch (or Explorer's "Convert with Mochi" on several files) hands its request to the first and exits.</summary>
public static class SingleInstance
{
    private const string PipeName = "Mochi-ipc";
    private static Mutex? _mutex;

    /// <summary>True when this process is the first one. MOCHI_MULTI=1 turns the check off (used by tests).</summary>
    public static bool TryBecomeFirst()
    {
        if (Environment.GetEnvironmentVariable("MOCHI_MULTI") == "1") return true;
        _mutex = new Mutex(true, @"Local\Mochi-single-instance", out var first);
        return first;
    }

    public static void Send(string line)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(3000);
            using var w = new StreamWriter(c) { AutoFlush = true };
            w.WriteLine(line);
        }
        catch { /* the first instance is not listening yet; nothing more to do */ }
    }

    public static void Listen(Action<string> onLine)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync();
                    using var r = new StreamReader(s);
                    if (await r.ReadLineAsync() is { } line) onLine(line);
                }
                catch { await Task.Delay(500); }
            }
        });
    }
}

/// <summary>Looks for a newer Mochi release on GitHub and can download and run its installer.</summary>
public static class Updater
{
    public sealed record Release(bool Newer, string Version, string? AssetUrl, string? Error, string? Notes = null);

    public static string Current => Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<Release> CheckAsync()
    {
        var url = Environment.GetEnvironmentVariable("MOCHI_UPDATE_URL") ?? $"https://api.github.com/repos/{App.Settings.UpdateRepo}/releases/latest";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mochi");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.SendAsync(req);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return new Release(false, Current, null, null);   // no release published yet
            res.EnsureSuccessStatusCode();
            using var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var tag = d.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            var asset = d.RootElement.TryGetProperty("assets", out var a)
                ? a.EnumerateArray().Select(x => x.GetProperty("browser_download_url").GetString()!).FirstOrDefault(u => u.Contains("Setup", StringComparison.OrdinalIgnoreCase) && u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                : null;
            var newer = Version.TryParse(tag, out var t) && Version.TryParse(Current, out var c) && t > c;
            var notes = d.RootElement.TryGetProperty("body", out var bd) ? bd.GetString() : null;
            return new Release(newer, tag, asset, null, notes);
        }
        catch (Exception ex) { return new Release(false, Current, null, ex.Message); }
    }

    public static async Task DownloadAndRunAsync(Release r)
    {
        if (r.AssetUrl is null) throw new InvalidOperationException("The release has no installer attached");
        var file = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(r.AssetUrl).LocalPath));
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mochi");
            await File.WriteAllBytesAsync(file, await http.GetByteArrayAsync(r.AssetUrl));
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, "/SILENT /CLOSEAPPLICATIONS") { UseShellExecute = true });
    }
}
