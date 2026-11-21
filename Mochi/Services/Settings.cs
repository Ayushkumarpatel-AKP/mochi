using System.Text.Json;

namespace Mochi.Services;

public sealed class WatchEntry
{
    public string Path { get; set; } = "";
    public string PresetId { get; set; } = "png";
    public bool Enabled { get; set; } = true;
}

public sealed class CustomPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = ".png";
    public int Quality { get; set; } = 90;
    public double Scale { get; set; } = 1;
    public bool Gray { get; set; }
}

/// <summary>One slice that holds several presets: rest on it and they fan out around it.</summary>
public sealed class DonutGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
}

public sealed class HistoryEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Failed { get; set; }
    public List<string> Outputs { get; set; } = new();
}

/// <summary>A donut for a kind of file: used when every picked file has one of its extensions. An empty list is the catch-all.</summary>
public sealed class DonutProfile
{
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();
    public List<string> PresetIds { get; set; } = new();
    public List<string> BackPresetIds { get; set; } = new();   // the other face of this donut
    public bool Builtin { get; set; }                          // made by the app, so a newer version may refresh it
}

public sealed class Settings
{
    public string Mode { get; set; } = "System";      // System | Light | Dark | Auto (dark between AutoDarkFrom and AutoDarkTo)
    public string ThemePreset { get; set; } = "mochi";   // see ThemeEngine.Themes
    public bool AccentCycle { get; set; }              // "Living accent": the accent colour drifts slowly through the rainbow
    public double BgTint { get; set; }                 // 0..1, how much of the accent colours the backgrounds
    public bool Oled { get; set; }                     // pure black background in dark looks
    public string Material { get; set; } = "None";     // None | Mica | Acrylic (main window)
    public string Density { get; set; } = "Comfortable";   // Compact | Comfortable | Spacious
    public string UiFont { get; set; } = "";           // "" = the bundled Noto Sans
    public double TextScale { get; set; } = 1;
    public string AutoDarkFrom { get; set; } = "19:00";
    public string AutoDarkTo { get; set; } = "07:00";
    public bool SoundEffects { get; set; } = true;     // a soft chime when a job is done
    public double SoundVolume { get; set; } = 0.6;
    public string Accent { get; set; } = "#F0456B";
    public bool CosyApplied { get; set; }                      // the one-time move from the old blue accent to the cosy berry one
    public double Softness { get; set; } = 18;
    public string Language { get; set; } = "en";
    public bool ReduceMotion { get; set; }

    public List<string> DonutIds { get; set; } = new() { "png", "jpg", "doc-pdf", "pdf-png", "txt", "mp3", "mp4", "zip" };
    public double DonutOpacity { get; set; } = 0.95;
    public bool TriggersEnabled { get; set; } = true;
    public string TriggerModifier { get; set; } = "Alt or Shift";   // Alt | Ctrl | Shift | Alt or Shift
    public bool CloseToTray { get; set; } = true;
    public List<string> DisabledFields { get; set; } = new();
    public List<WatchEntry> Watch { get; set; } = new();
    public List<string> EngineOrder { get; set; } = new();
    public List<CustomPreset> CustomPresets { get; set; } = new();
    public List<DonutProfile> Profiles { get; set; } = new();
    public string OllamaModel { get; set; } = "llama3.2";
    public string LastSeenVersion { get; set; } = "";
    public bool ProfilesSeeded { get; set; }
    public int ProfilesVersion { get; set; }
    public List<string> DonutBackIds { get; set; } = new() { "turn-90", "flip-h", "width-1080", "under-200", "strip", "watermark", "pdf-protect", "long-img" };
    public List<DonutGroup> Groups { get; set; } = new();
    public string OutputFolder { get; set; } = "";             // empty: next to the original file
    public bool OpenFolderWhenDone { get; set; }
    public List<HistoryEntry> History { get; set; } = new();
    public double WindowScale { get; set; } = 1;
    public bool ContextMenu { get; set; }
    public string ToolbarStyle { get; set; } = "panel";        // "panel" (Quick Panel) or "donut"
    public List<string> PinnedPresets { get; set; } = new();
    public bool PinsSeeded { get; set; }
    public List<string> RecentPresets { get; set; } = new();
    public List<string> SearchFolders { get; set; } = new();   // folders kept ready for search by meaning
    public bool SearchFoldersSeeded { get; set; }
    public bool BackgroundIndex { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    public string UpdateRepo { get; set; } = "Ayushkumarpatel-AKP/mochi";
    public Dictionary<string, string> ToolPaths { get; set; } = new();
    public string AiProvider { get; set; } = "groq";             // groq | grok | openai | gemini | ollama | custom
    public Dictionary<string, string> AiKeys { get; set; } = new();     // provider → API key, encrypted for this Windows user (DPAPI)
    public Dictionary<string, string> AiModels { get; set; } = new();   // provider → model the user chose ("" = the provider's default)
    public Dictionary<string, string> AiVisionModels { get; set; } = new();   // provider → model used when a picture is sent
    public string AiBaseUrl { get; set; } = "";                  // only for the custom provider
    public string AiLanguage { get; set; } = "";                 // "" = answer in the language of the file
    public bool AiDonutPlaced { get; set; }                      // the AI slice has been added to the donut
    public bool AiFallback { get; set; } = true;                 // when the chosen AI hits its limit or fails, try its other models, other connected AIs, then Ollama
    public bool AiConsent { get; set; }                          // the user agreed that files are sent to the chosen online provider

    private static readonly string PathFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi", "settings.json");

    public static Settings Load()
    {
        try
        {
            var st = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathFile)) ?? new();
            if (!st.CosyApplied) { if (st.Accent == "#0A84FF") st.Accent = "#F0456B"; st.CosyApplied = true; }
            return st;
        }
        catch { return new() { CosyApplied = true }; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}
