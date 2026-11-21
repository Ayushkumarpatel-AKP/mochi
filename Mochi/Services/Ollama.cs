using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Mochi.Services;

/// <summary>The local Ollama server: start it, list its models, pick the best one, pull new ones.</summary>
public static class Ollama
{
    public sealed record ModelInfo(string Name, long Size)
    {
        public override string ToString() => $"{Name}  ({Size / 1024 / 1024} MB)";
    }

    public const string Recommended = "llama3.2";   // 3B: the smallest model that translates short lines reliably

    private const string Base = "http://127.0.0.1:11434";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(2) };
    private static DateTime _startTried = DateTime.MinValue;

    public static bool IsUp()
    {
        try
        {
            using var cts = new CancellationTokenSource(700);
            return Http.GetAsync(Base + "/api/version", cts.Token).GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>True when the server answers; starts it (hidden) when Ollama is installed but not running.</summary>
    public static bool EnsureRunning()
    {
        if (IsUp()) return true;
        var exe = Tools.Find("ollama");
        if (exe is null || DateTime.UtcNow - _startTried < TimeSpan.FromSeconds(20)) return false;
        _startTried = DateTime.UtcNow;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "serve") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { return false; }
        for (var i = 0; i < 40; i++) { Thread.Sleep(250); if (IsUp()) return true; }
        return false;
    }

    public static List<ModelInfo> Models()
    {
        try
        {
            using var cts = new CancellationTokenSource(3000);
            var json = Http.GetStringAsync(Base + "/api/tags", cts.Token).GetAwaiter().GetResult();
            using var d = JsonDocument.Parse(json);
            return d.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => new ModelInfo(m.GetProperty("name").GetString()!, m.TryGetProperty("size", out var s) ? s.GetInt64() : 0))
                .Where(m => !m.Name.Contains("cloud"))   // cloud models forward text over the internet
                .OrderByDescending(m => m.Size).ToList();
        }
        catch { return new(); }
    }

    /// <summary>The model the user chose if it is installed, otherwise the biggest installed one (bigger translates better).</summary>
    public static string? Best()
    {
        var all = Models();
        var want = App.Settings.OllamaModel;
        return all.FirstOrDefault(m => m.Name == want || m.Name == want + ":latest")?.Name ?? all.FirstOrDefault()?.Name;
    }

    /// <summary>Downloads a model; <paramref name="status"/> gets lines like "pulling… 42%".</summary>
    public static async Task PullAsync(string tag, Action<string> status)
    {
        if (!EnsureRunning()) throw new InvalidOperationException("Ollama is not installed or will not start");
        var body = JsonSerializer.Serialize(new { name = tag, stream = true });
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/api/pull") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        using var rd = new StreamReader(await res.Content.ReadAsStreamAsync());
        string? line;
        while ((line = await rd.ReadLineAsync()) is not null)
        {
            using var d = JsonDocument.Parse(line);
            var r = d.RootElement;
            if (r.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.GetString());
            var st = r.TryGetProperty("status", out var s) ? s.GetString() : "";
            if (r.TryGetProperty("total", out var t) && r.TryGetProperty("completed", out var c) && t.GetInt64() > 0)
                st += $" {c.GetInt64() * 100 / t.GetInt64()}%";
            status(st ?? "");
        }
    }
}
