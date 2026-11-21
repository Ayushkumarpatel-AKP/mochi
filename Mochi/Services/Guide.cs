using System.Text.Json;
using Avalonia.Platform;
using Mochi.Views;

namespace Mochi.Services;

/// <summary>One page of a guide: a Mochi sticker, a title, a sentence, a few short points and an optional tip.</summary>
public sealed record GuideSlide(string Mascot, string Title, string Body, string[] Bullets, string? Tip, string? Target = null);

/// <summary>A set of pages that the on-screen guide (GuideOverlay) shows one after the other.</summary>
public sealed record GuideDeck(string Title, List<GuideSlide> Slides);

/// <summary>
/// The setup guide for new users and the "What's new" pages for updates. Both are plain JSON in Assets/Guide, so adding a page for a new
/// feature never means touching code: copy Assets/Guide/whatsnew/_template.json to Assets/Guide/whatsnew/&lt;version&gt;.json and fill it in
/// (see docs/ADDING-A-FEATURE.md).
/// </summary>
public static class Guide
{
    private const string Base = "avares://Mochi/Assets/Guide/";

    /// <summary>The version in the project file (Version in Mochi.csproj).</summary>
    public static string AppVersion => typeof(Guide).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>The welcome tour that a new user sees the first time Mochi opens.</summary>
    public static GuideDeck Welcome() => Load(Base + "welcome.json") ?? new("Welcome to Mochi", new() { new("hello", "Hi, I'm Mochi!", "Drop a file and I will show you what it can become.", Array.Empty<string>(), null) });

    /// <summary>
    /// The pages for every version after <paramref name="since"/> up to this one, oldest first (a user who skips a few updates sees them all).
    /// With <paramref name="since"/> empty it is just the current version's page, or a short "you are up to date" page if there is none.
    /// </summary>
    public static GuideDeck WhatsNew(string? since = null, bool fallback = true)
    {
        var all = Versions().OrderBy(v => v.Version).ToList();
        var current = Version.TryParse(AppVersion, out var c) ? c : new Version(0, 0);
        List<(Version Version, string Name)> pick;
        if (string.IsNullOrEmpty(since) || !Version.TryParse(since, out var seen))
            pick = all.Where(v => v.Version == current).ToList() is { Count: > 0 } now ? now : all.Where(v => v.Version <= current).TakeLast(1).ToList();
        else
            pick = all.Where(v => v.Version > seen && v.Version <= current).ToList();

        var slides = new List<GuideSlide>();
        foreach (var (v, name) in pick)
            if (Load(Base + "whatsnew/" + name + ".json") is { } d) slides.AddRange(d.Slides);
        if (slides.Count == 0 && fallback)
            slides.Add(new("cheer", "You're up to date", $"This is Mochi {AppVersion}. New things will show up here after each update.", Array.Empty<string>(), null));
        return new($"What's new in Mochi {AppVersion}", slides);
    }

    /// <summary>Shown when Mochi opens: the tour once for a new user, then each update's pages once.</summary>
    public static void ShowAtStartup()
    {
        var s = App.Settings;
        if (!s.GuideSeen)
        {
            s.GuideSeen = true; s.LastSeenVersion = AppVersion; s.Save();
            App.Main?.ShowGuide(Welcome(), "Let's go");
            return;
        }
        if (s.LastSeenVersion == AppVersion) return;
        var since = s.LastSeenVersion;
        s.LastSeenVersion = AppVersion; s.Save();
        var deck = WhatsNew(since, fallback: false);
        if (deck.Slides.Count > 0) App.Main?.ShowGuide(deck, "Got it");
    }

    public static void ShowTour() => App.Main?.ShowGuide(Welcome(), "Let's go");
    public static void ShowWhatsNew() => App.Main?.ShowGuide(WhatsNew(), "Got it");

    private static IEnumerable<(Version Version, string Name)> Versions()
    {
        IEnumerable<Uri> uris;
        try { uris = AssetLoader.GetAssets(new Uri(Base + "whatsnew/"), null).ToList(); }
        catch { yield break; }
        foreach (var u in uris)
        {
            var name = Path.GetFileNameWithoutExtension(u.AbsolutePath);
            if (name.StartsWith('_') || !u.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (Version.TryParse(name, out var v)) yield return (v, name);
        }
    }

    private static GuideDeck? Load(string uri)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri(uri));
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var slides = new List<GuideSlide>();
            foreach (var sl in root.GetProperty("slides").EnumerateArray())
                slides.Add(new GuideSlide(
                    sl.TryGetProperty("mascot", out var m) ? m.GetString() ?? "hello" : "hello",
                    sl.GetProperty("title").GetString() ?? "",
                    sl.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    sl.TryGetProperty("bullets", out var bl) ? bl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>(),
                    sl.TryGetProperty("tip", out var t) ? t.GetString() : null,
                    sl.TryGetProperty("target", out var tg) ? tg.GetString() : null));
            return new GuideDeck(root.TryGetProperty("title", out var title) ? title.GetString() ?? "Mochi" : "Mochi", slides);
        }
        catch { return null; }
    }
}
