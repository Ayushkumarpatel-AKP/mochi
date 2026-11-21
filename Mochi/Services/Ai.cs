using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mochi.Services;

public sealed record AiProvider(string Id, string Name, string BaseUrl, string DefaultModel, string VisionModel, bool Online, string KeyUrl, string EnvVar);

/// <summary>One message of a conversation. Images are data: URLs and go with the text of this message.</summary>
public sealed record AiMsg(string Role, string Text, IReadOnlyList<string>? Images = null);

/// <summary>An error with a sentence the user can act on (never a raw HTTP dump).</summary>
public sealed class AiException : Exception
{
    public bool NeedsSetup { get; }
    public int Status { get; init; }                 // the HTTP status, 0 when the server could not be reached
    public TimeSpan? RetryAfter { get; init; }       // how long the provider asked us to wait (rate limits)
    public string Detail { get; init; } = "";         // the provider's own wording: for logs, never shown to the user
    public AiException(string message, bool needsSetup = false) : base(message) => NeedsSetup = needsSetup;
}

/// <summary>
/// The AI connection. Grok (xAI), OpenAI, Gemini and Ollama all speak the same "chat completions" protocol, so one client covers them;
/// "custom" is any other server that does. Keys are stored encrypted for the current Windows user, never as plain text.
/// </summary>
public static class Ai
{
    public static readonly AiProvider[] Providers =
    {
        new("groq", "Groq (fast, free tier)", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", "meta-llama/llama-4-scout-17b-16e-instruct", true, "https://console.groq.com/keys", "GROQ_API_KEY"),
        new("grok", "Grok (xAI)", "https://api.x.ai/v1", "grok-4", "grok-4", true, "https://console.x.ai", "XAI_API_KEY"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", "gpt-4o-mini", true, "https://platform.openai.com/api-keys", "OPENAI_API_KEY"),
        new("gemini", "Google Gemini (free tier)", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.0-flash", "gemini-2.0-flash", true, "https://aistudio.google.com/apikey", "GEMINI_API_KEY"),
        new("ollama", "Ollama (on this PC, offline)", "http://127.0.0.1:11434/v1", "", "", false, "https://ollama.com", ""),
        new("custom", "Other (OpenAI-compatible)", "", "", "", true, "", ""),
    };

    /// <summary>Which provider a key belongs to, from its prefix (gsk_ = Groq, xai- = Grok, AIza = Gemini, sk- = OpenAI); null if unknown.</summary>
    public static AiProvider? Detect(string key)
    {
        key = key.Trim();
        var id = key.StartsWith("gsk_") ? "groq" : key.StartsWith("xai-") ? "grok" : key.StartsWith("AIza") ? "gemini"
            : key.StartsWith("sk-") && !key.StartsWith("sk-ant-") && !key.StartsWith("sk-or-") ? "openai" : null;
        return id is null ? null : Find(id);
    }

    // The best model first, per provider, for text and for pictures: the first pattern that matches an offered model wins.
    private static readonly Dictionary<string, (string[] Text, string[] Vision)> Ranking = new()
    {
        ["groq"] = (new[] { @"^llama-3\.3-70b-versatile$", "gpt-oss-120b", "kimi-k2", "llama-4-maverick", @"qwen/qwen3\.[5-9]", "qwen.*32b", @"llama-3\.1-8b-instant", "gpt-oss-20b" },
            new[] { "llama-4-maverick", "llama-4-scout", @"qwen/qwen3\.[5-9]", "qwen.*vl" }),
        ["grok"] = (new[] { "^grok-4$", "^grok-4", "^grok-3$", "^grok-3", "grok" }, new[] { "^grok-4$", "^grok-4", "vision" }),
        ["openai"] = (new[] { @"^gpt-4\.1-mini$", "^gpt-4o-mini$", @"^gpt-4\.1$", "^gpt-4o$", "^gpt-5-mini", "^gpt-" }, new[] { "^gpt-4o-mini$", @"^gpt-4\.1-mini$", "^gpt-4o$", @"^gpt-4\.1$" }),
        ["gemini"] = (new[] { @"gemini-2\.5-flash$", @"gemini-2\.0-flash$", @"gemini-1\.5-flash", "gemini" }, new[] { @"gemini-2\.5-flash$", @"gemini-2\.0-flash$", @"gemini-1\.5-flash", "gemini" }),
        ["ollama"] = (Array.Empty<string>(), new[] { "llava", "vision", "minicpm-v", "moondream", "gemma3" }),
    };

    /// <summary>The best of the offered models for this provider (pictures or text), or null when none of the known good ones is offered.</summary>
    public static string? Pick(AiProvider p, IReadOnlyList<string> offered, bool vision)
    {
        if (!Ranking.TryGetValue(p.Id, out var r)) return null;
        foreach (var pattern in vision ? r.Vision : r.Text)
            if (offered.FirstOrDefault(m => Regex.IsMatch(m, pattern, RegexOptions.IgnoreCase)) is { } hit) return hit;
        return null;
    }

    /// <summary>How much of a file's text is sent: free tiers allow only a few thousand tokens per minute, so Groq gets less.</summary>
    public static int MaxChars => Provider.Id == "groq" ? 16000 : 60000;
    public static int MaxImages => Provider.Id == "groq" ? 5 : 8;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static AiProvider Provider => Providers.FirstOrDefault(p => p.Id == App.Settings.AiProvider) ?? Providers[0];
    public static AiProvider Find(string id) => Providers.FirstOrDefault(p => p.Id == id) ?? Providers[0];

    // ---------------------------------------------------------------- key and model

    public static string Key(AiProvider p)
    {
        if (App.Settings.AiKeys.TryGetValue(p.Id, out var enc) && enc.Length > 0)
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser)); }
            catch { /* another Windows user or a damaged value: treat as no key */ }
        }
        return p.EnvVar.Length > 0 ? Environment.GetEnvironmentVariable(p.EnvVar)?.Trim() ?? "" : "";
    }

    public static void SetKey(AiProvider p, string key)
    {
        key = key.Trim();
        if (key.Length == 0) App.Settings.AiKeys.Remove(p.Id);
        else App.Settings.AiKeys[p.Id] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        App.Settings.Save();
    }

    public static string BaseUrl(AiProvider p) => (p.Id == "custom" ? App.Settings.AiBaseUrl : p.BaseUrl).Trim().TrimEnd('/');

    public static string Model(AiProvider p, bool vision = false)
    {
        var chosen = vision ? App.Settings.AiVisionModels : App.Settings.AiModels;
        if (chosen.TryGetValue(p.Id, out var m) && m.Trim().Length > 0) return m.Trim();
        if (vision && p.VisionModel.Length > 0) return p.VisionModel;
        if (p.Id == "ollama") return Ollama.Best() ?? Ollama.Recommended;
        return p.DefaultModel;
    }

    public static void SetModel(AiProvider p, string model, bool vision = false)
    {
        var d = vision ? App.Settings.AiVisionModels : App.Settings.AiModels;
        if (model.Trim().Length == 0) d.Remove(p.Id); else d[p.Id] = model.Trim();
        App.Settings.Save();
    }

    private static bool IsLocalUrl(string url) => url.Contains("127.0.0.1") || url.Contains("localhost");

    /// <summary>True when a request can be sent now: a key is saved (Ollama and local servers need none) and, for "other", an address.</summary>
    public static bool IsConfigured
    {
        get
        {
            var p = Provider;
            if (p.Id == "custom") return BaseUrl(p).Length > 0 && (Key(p).Length > 0 || IsLocalUrl(BaseUrl(p)));
            return !p.Online || Key(p).Length > 0;
        }
    }

    /// <summary>Online providers receive the file's words or picture, so the user agrees once first.</summary>
    public static bool NeedsConsent => Provider.Online && !(Provider.Id == "custom" && IsLocalUrl(BaseUrl(Provider))) && !App.Settings.AiConsent;

    /// <summary>AI tools exist in the toolbars only while an AI is connected.</summary>
    public static bool Hidden(Preset p) => p.Category == "Ai" && !IsConfigured;

    private static readonly string[] ToolIds = { "ai-summary", "ai-ask", "ai-explain", "ai-rename", "ai-command" };
    private const string GroupId = "ai";

    /// <summary>Once an AI is connected, the "✦ AI" slice (a group of the five tools) joins the donut, once. The user can remove or move it afterwards.</summary>
    public static void SeedUi()
    {
        var s = App.Settings;
        if (!IsConfigured || s.AiDonutPlaced) return;
        if (!s.Groups.Any(g => g.Id == GroupId)) s.Groups.Add(new DonutGroup { Id = GroupId, Name = "✦ AI", Members = ToolIds.ToList() });
        if (!s.DonutIds.Contains("g:" + GroupId) && !s.DonutBackIds.Contains("g:" + GroupId))
        {
            // a free slot on the front, else on the back; if both are full the AI slice takes the last front slot (that preset stays in the library)
            if (s.DonutIds.Count < 8) s.DonutIds.Add("g:" + GroupId);
            else if (s.DonutBackIds.Count < 8) s.DonutBackIds.Add("g:" + GroupId);
            else s.DonutIds[^1] = "g:" + GroupId;
        }
        s.AiDonutPlaced = true;
        s.Save();
    }

    public static string ModelLabel => Provider.Name.Split(' ')[0] + " · " + Model(Provider);

    // ---------------------------------------------------------------- requests

    private static HttpRequestMessage Build(AiProvider p, string path, HttpMethod method, string? json)
    {
        var url = BaseUrl(p);
        if (url.Length == 0) throw new AiException("This AI has no server address yet. Open Settings → AI and fill it in.", true);
        var req = new HttpRequestMessage(method, url + path);
        var key = Key(p);
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private static string Body(string model, IReadOnlyCollection<AiMsg> msgs, bool stream, double temperature)
    {
        var list = new List<object>();
        foreach (var m in msgs)
        {
            if (m.Images is { Count: > 0 })
            {
                var parts = new List<object> { new { type = "text", text = m.Text } };
                parts.AddRange(m.Images.Select(i => (object)new { type = "image_url", image_url = new { url = i } }));
                list.Add(new { role = m.Role, content = parts });
            }
            else list.Add(new { role = m.Role, content = m.Text });
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["model"] = model, ["messages"] = list, ["stream"] = stream, ["temperature"] = temperature });
    }

    // ---------------------------------------------------------------- fallback when a free tier runs out

    private sealed record Route(AiProvider P, string Model)
    {
        public string Label => P.Name.Split(' ')[0] + " · " + Model;
    }

    private static readonly Dictionary<string, DateTime> Cooling = new();       // "provider|model" → when it may be tried again
    private static readonly Dictionary<string, List<string>> Offered = new();   // provider → models seen at the last connection test
    private static string? _note;

    /// <summary>"Groq · x is over its free limit, so … answered instead": set when a backup had to answer; whoever shows it clears it.</summary>
    public static string? TakeNote() { var n = _note; _note = null; return n; }

    public static void RememberModels(AiProvider p, IEnumerable<string> models) => Offered[p.Id] = models.ToList();

    private static bool Cooled(Route r) => Cooling.TryGetValue(r.P.Id + "|" + r.Model, out var t) && t > DateTime.UtcNow;

    /// <summary>A model that just failed is left alone for a while, so the next request goes straight to one that works.</summary>
    private static void Rest(Route r, AiException e)
    {
        var span = e.Status switch
        {
            429 => e.RetryAfter is { } ra ? TimeSpan.FromSeconds(Math.Clamp(ra.TotalSeconds, 5, 6 * 3600)) : TimeSpan.FromSeconds(90),
            401 or 403 or 404 => TimeSpan.FromMinutes(10),
            >= 500 or 0 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.Zero,   // too large / a bad request: another request may be fine
        };
        if (span > TimeSpan.Zero) Cooling[r.P.Id + "|" + r.Model] = DateTime.UtcNow + span;
    }

    /// <summary>The chosen model first; then, when fallback is on, the provider's other good models (each has its own limit), every other connected AI, and Ollama on this PC.</summary>
    private static async Task<List<Route>> RoutesAsync(bool vision, CancellationToken ct)
    {
        var p = Provider;
        var list = new List<Route> { new(p, Model(p, vision)) };
        if (!App.Settings.AiFallback) return list;

        if (Ranking.TryGetValue(p.Id, out var rank))
        {
            if (!Offered.TryGetValue(p.Id, out var offered))
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(4000);
                    offered = await ModelsAsync(p, cts.Token); Offered[p.Id] = offered;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { offered = new(); }
                catch (AiException) { offered = new(); }
            }
            foreach (var pattern in vision ? rank.Vision : rank.Text)
                foreach (var m in offered.Where(m => Regex.IsMatch(m, pattern, RegexOptions.IgnoreCase)))
                    if (!list.Any(r => r.P.Id == p.Id && r.Model == m)) list.Add(new Route(p, m));
        }
        foreach (var q in Providers.Where(x => x.Id != p.Id && x.Id != "custom"))
            if (q.Id == "ollama" ? Tools.Find("ollama") is not null : Key(q).Length > 0) list.Add(new Route(q, Model(q, vision)));

        var fresh = list.Where(r => !Cooled(r)).ToList();
        return fresh.Count > 0 ? fresh : list;   // all resting: try them anyway rather than fail without trying
    }

    /// <summary>Why a model was skipped, as a short phrase for the note under the answer.</summary>
    private static string Reason(AiException e) => e.Status switch
    {
        429 => "free limit is over",
        413 => "file was too big for it",
        401 or 403 => "key is not working",
        404 => "model is not available",
        _ => "had a problem",
    };

    private static string NoteFor(Route failed, AiException e, Route used) =>
        failed.P.Id == used.P.Id
            ? $"{failed.P.Name.Split(' ')[0]}: {failed.Model} {Reason(e)}, so {used.Model} answered instead."
            : $"{failed.P.Name.Split(' ')[0]} {Reason(e)}, so {used.P.Name.Split(' ')[0]} answered instead.";

    private static AiException Combine(List<(Route R, AiException E)> failed)
    {
        var first = failed[0].E;
        if (failed.Count == 1) return first;
        return new AiException(first.Message + "\n" + "Mochi also tried backup models, but none of them worked either.", first.NeedsSetup) { Status = first.Status, RetryAfter = first.RetryAfter };
    }

    /// <summary>Sends the conversation and hands each piece of the answer to <paramref name="onDelta"/> as it arrives. Returns the whole answer.</summary>
    public static async Task<string> StreamAsync(IEnumerable<AiMsg> msgsIn, Action<string> onDelta, CancellationToken ct, double temperature = 0.4)
    {
        var msgs = msgsIn.ToList();
        if (!IsConfigured) throw new AiException("No AI is connected yet. Open Settings → AI and paste your API key.", true);
        _note = null;
        var failed = new List<(Route R, AiException E)>();
        foreach (var r in await RoutesAsync(msgs.Any(m => m.Images is { Count: > 0 }), ct))
        {
            var started = false;
            try
            {
                var text = await StreamOnce(r, msgs, d => { started = true; onDelta(d); }, ct, temperature);
                if (failed.Count > 0) _note = NoteFor(failed[0].R, failed[0].E, r);
                return text;
            }
            catch (AiException ex) when (!started)   // nothing shown yet: it is safe to try the next one
            {
                failed.Add((r, ex)); Rest(r, ex);
            }
        }
        throw Combine(failed);
    }

    private static async Task<string> StreamOnce(Route r, List<AiMsg> msgs, Action<string> onDelta, CancellationToken ct, double temperature)
    {
        var p = r.P;
        await Prepare(p, ct);
        using var req = Build(p, "/chat/completions", HttpMethod.Post, Body(r.Model, msgs, true, temperature));
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw Unreachable(p, ex); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw await Failed(p, r.Model, res);
            var all = new StringBuilder();
            using var rd = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
            string? line;
            while ((line = await rd.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (!doc.RootElement.TryGetProperty("choices", out var ch) || ch.GetArrayLength() == 0) continue;
                    if (ch[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var piece = c.GetString() ?? "";
                        if (piece.Length == 0) continue;
                        all.Append(piece); onDelta(piece);
                    }
                }
                catch (JsonException) { /* a keep-alive or partial line */ }
            }
            if (all.Length == 0) throw new AiException("The AI sent back no answer. Please try again.");
            return all.ToString();
        }
    }

    /// <summary>One answer in one piece, for jobs that need structured output (renaming, planning).</summary>
    public static async Task<string> CompleteAsync(IEnumerable<AiMsg> msgsIn, CancellationToken ct, double temperature = 0.2)
    {
        var msgs = msgsIn.ToList();
        if (!IsConfigured) throw new AiException("No AI is connected yet. Open Settings → AI and paste your API key.", true);
        _note = null;
        var failed = new List<(Route R, AiException E)>();
        foreach (var r in await RoutesAsync(msgs.Any(m => m.Images is { Count: > 0 }), ct))
        {
            try
            {
                var text = await CompleteOnce(r, msgs, ct, temperature);
                if (failed.Count > 0) _note = NoteFor(failed[0].R, failed[0].E, r);
                return text;
            }
            catch (AiException ex) { failed.Add((r, ex)); Rest(r, ex); }
        }
        throw Combine(failed);
    }

    private static async Task<string> CompleteOnce(Route r, List<AiMsg> msgs, CancellationToken ct, double temperature)
    {
        var p = r.P;
        await Prepare(p, ct);
        using var req = Build(p, "/chat/completions", HttpMethod.Post, Body(r.Model, msgs, false, temperature));
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw Unreachable(p, ex); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw await Failed(p, r.Model, res);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            if (text.Trim().Length == 0) throw new AiException("The AI sent back no answer. Please try again.");
            return text;
        }
    }

    /// <summary>The model names the provider offers; also proves the key works.</summary>
    public static async Task<List<string>> ModelsAsync(AiProvider p, CancellationToken ct)
    {
        await Prepare(p, ct);
        using var req = Build(p, "/models", HttpMethod.Get, null);
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw Unreachable(p, ex); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw await Failed(p, Model(p), res);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var arr = root.TryGetProperty("data", out var d) ? d : root.TryGetProperty("models", out var m) ? m : default;
            if (arr.ValueKind != JsonValueKind.Array) return new();
            return arr.EnumerateArray()
                .Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : x.TryGetProperty("name", out var n) ? n.GetString() : null)
                .OfType<string>().Select(s => s.StartsWith("models/") ? s[7..] : s)
                .Where(s => !Regex.IsMatch(s, "embed|whisper|tts|dall|image-gen|moderation|imagine|guard|playai|orpheus|safeguard|transcribe|audio|realtime", RegexOptions.IgnoreCase))
                .OrderBy(s => s).ToList();
        }
    }

    /// <summary>The first JSON object in an answer, even when the model wrapped it in a code fence or a sentence.</summary>
    public static JsonDocument? ParseJson(string text)
    {
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonDocument.Parse(text[start..(end + 1)]); } catch (JsonException) { return null; }
    }

    // ---------------------------------------------------------------- failures in plain words

    private static async Task Prepare(AiProvider p, CancellationToken ct)
    {
        if (p.Id == Provider.Id && !IsConfigured) throw new AiException("Connect an AI first: add an API key in Settings → AI.", true);
        if (p.Id == "ollama" && !await Task.Run(Ollama.EnsureRunning, ct))
            throw new AiException("Ollama is not running on this PC. Start it, or use an online AI in Settings → AI.", true);
    }

    private static AiException Unreachable(AiProvider p, Exception ex) =>
        new("Can't connect to the AI. Please check your internet and try again.") { Status = 0, Detail = ex.Message };

    private static async Task<AiException> Failed(AiProvider p, string model, HttpResponseMessage res)
    {
        string detail = "";
        try
        {
            var body = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e))
                detail = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            else if (root.TryGetProperty("message", out var mm)) detail = mm.GetString() ?? "";
        }
        catch { /* no readable body */ }
        if (detail.Length > 240) detail = detail[..240] + "…";
        var status = (int)res.StatusCode;
        var wait = res.Headers.RetryAfter?.Delta;
        AiException Make(string message, bool setup = false) => new(message, setup) { Status = status, RetryAfter = wait, Detail = detail };

        // some providers answer a wrong key with 400 instead of 401
        var badKey = "Your API key is not working. Open Settings → AI and check it, or paste a new one.";
        if (res.StatusCode == HttpStatusCode.BadRequest && Regex.IsMatch(detail, "api key|api-key|authenticat|unauthori", RegexOptions.IgnoreCase))
            return new AiException(badKey, true) { Status = 401, Detail = detail };

        // the provider's own wording stays in Detail (for the log); the user gets one plain sentence and what to do
        var minutes = wait is { } w && w.TotalMinutes >= 1.5 ? $" Try again in about {Math.Ceiling(w.TotalMinutes)} minutes." : " Try again in a few minutes.";
        return res.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Make(badKey, true),
            HttpStatusCode.NotFound => Make("This AI model is not available. Open Settings → AI and press “Test and save” so Mochi picks one that works.", true),
            HttpStatusCode.RequestEntityTooLarge => Make("This file is too big for your free plan. Try a smaller file."),
            HttpStatusCode.TooManyRequests => Make("The free limit is over for now." + minutes + " Tip: add a second AI key in Settings → AI and Mochi will use it as a backup."),
            HttpStatusCode.BadRequest => Make("This AI could not handle the request (maybe it can't read pictures). Try another file, or another model in Settings → AI."),
            _ when status >= 500 => Make("The AI service is having trouble right now. Please try again in a moment."),
            _ => Make("Something went wrong with the AI. Please try again."),
        };
    }

}
