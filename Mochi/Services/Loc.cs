using System.Text.Json;
using Avalonia.Platform;

namespace Mochi.Services;

/// <summary>Loads Filee's own i18n json (Assets/i18n) plus this app's extra strings (Assets/i18n-x); English fills any gap.</summary>
public static class Loc
{
    private static Dictionary<string, string> _map = new();

    /// <summary>Raised after the language changes, so windows that are already open can relabel themselves.</summary>
    public static event Action? Changed;

    public static void Load(string lang)
    {
        _map = Read("i18n", "en");
        foreach (var kv in Read("i18n-x", "en")) _map[kv.Key] = kv.Value;
        if (lang != "en")
        {
            foreach (var kv in Read("i18n", lang)) _map[kv.Key] = kv.Value;
            // the extra strings exist for fewer languages; Portuguese (Portugal) shares Brazil's
            foreach (var kv in Read("i18n-x", lang == "pt-PT" ? "pt-BR" : lang)) _map[kv.Key] = kv.Value;
        }
        Changed?.Invoke();
    }

    private static Dictionary<string, string> Read(string folder, string lang)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://Mochi/Assets/{folder}/{lang}.json"));
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
            if (folder == "i18n") foreach (var k in map.Keys.ToList()) map[k] = map[k].Replace("Filee", Brand.Name);   // Filee's strings name their own app
            return map;
        }
        catch { return new(); }
    }

    public static string? TryGet(string key) => _map.TryGetValue(key, out var s) ? s : null;

    public static string T(string key, params object[] args)
    {
        var v = _map.TryGetValue(key, out var s) ? s : key[(key.LastIndexOf('.') + 1)..];
        return args.Length == 0 ? v : string.Format(v, args);
    }

    public static IEnumerable<KeyValuePair<string, string>> Prefix(string p) =>
        _map.Where(kv => kv.Key.StartsWith(p)).OrderBy(kv => kv.Key);
}
