using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mochi.Services;

namespace Mochi.ViewModels;

/// <summary>Live language, the user's own presets, per-file-type donut profiles and the What's New window.</summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- language
    /// <summary>Switches every visible string to the chosen language without a restart.</summary>
    public void ApplyLanguage()
    {
        Loc.Load(App.Settings.Language);
        T.Refresh();

        var key = Selected?.Key ?? "home";
        Nav.Clear();
        foreach (var k in Keys) Nav.Add(new NavItem(k, Loc.T("nav." + k)));
        Selected = Nav.First(n => n.Key == key);

        FieldRows.Clear();
        foreach (var c in Categories) FieldRows.Add(new FieldRow(c, !App.Settings.DisabledFields.Contains(c)));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(TriggerHint));
        OnPropertyChanged(nameof(WhatsNewLabel));
        OnPropertyChanged(nameof(CustomSaveLabel));
        OnPropertyChanged(nameof(ProfileHint));
        LoadProfiles();
        ProfileIndex = 0;
        RefreshPresets();
        RefreshEngines();
    }

    // ---------------------------------------------------------------- what's new
    public Action? ShowWhatsNew { get; set; }
    [RelayCommand] private void WhatsNew() => ShowWhatsNew?.Invoke();

    // ---------------------------------------------------------------- your own presets
    public ObservableCollection<CustomPreset> CustomRows { get; } = new(App.Settings.CustomPresets);

    public string[] TargetExtensions { get; } =
        PresetCatalog.All.Where(p => p.Mode == "" && p.Extension.Length > 0).Select(p => p.Extension).Distinct().OrderBy(e => e).ToArray();

    [ObservableProperty] private string _customName = "";
    [ObservableProperty] private string _customExt = ".jpg";
    [ObservableProperty] private double _customQuality = 85;
    [ObservableProperty] private double _customScale = 100;
    [ObservableProperty] private bool _customGray;
    [ObservableProperty] private string? _editingId;
    [ObservableProperty] private string _customNote = "";

    public string CustomSaveLabel => Loc.T(EditingId is null ? "x.add_preset" : "x.save_changes");
    partial void OnEditingIdChanged(string? value) => OnPropertyChanged(nameof(CustomSaveLabel));

    [RelayCommand]
    private void SaveCustom()
    {
        var name = CustomName.Trim();
        if (name.Length == 0) { CustomNote = Loc.T("x.name_needed"); return; }

        var c = EditingId is null ? null : App.Settings.CustomPresets.FirstOrDefault(x => x.Id == EditingId);
        if (c is null)
        {
            c = new CustomPreset { Id = "c-" + Guid.NewGuid().ToString("N")[..8] };
            App.Settings.CustomPresets.Add(c);
        }
        c.Name = name; c.Extension = CustomExt; c.Quality = (int)CustomQuality; c.Scale = CustomScale / 100; c.Gray = CustomGray;
        App.Settings.Save();
        PresetCatalog.Reload();
        ReloadCustom();
        RefreshPresets();
        EditingId = null; CustomName = ""; CustomNote = Loc.T("x.preset_saved");
    }

    [RelayCommand]
    private void EditCustom(CustomPreset c)
    {
        EditingId = c.Id; CustomName = c.Name; CustomExt = c.Extension;
        CustomQuality = c.Quality; CustomScale = c.Scale * 100; CustomGray = c.Gray; CustomNote = "";
    }

    [RelayCommand]
    private void DeleteCustom(CustomPreset c)
    {
        App.Settings.CustomPresets.Remove(c);
        App.Settings.DonutIds.Remove(c.Id);
        foreach (var p in App.Settings.Profiles) p.PresetIds.Remove(c.Id);
        foreach (var w in App.Settings.Watch.Where(w => w.PresetId == c.Id)) w.PresetId = "png";
        App.Settings.Save();
        PresetCatalog.Reload();
        if (EditingId == c.Id) { EditingId = null; CustomName = ""; }
        ReloadCustom();
        RefreshPresets();
    }

    private void ReloadCustom()
    {
        CustomRows.Clear();
        foreach (var c in App.Settings.CustomPresets) CustomRows.Add(c);
    }

    // ---------------------------------------------------------------- translation model (Ollama)
    public ObservableCollection<string> OllamaModels { get; } = new();
    [ObservableProperty] private string _pullTag = Ollama.Recommended;
    [ObservableProperty] private string _pullStatus = "";
    [ObservableProperty] private bool _pullBusy;

    public string? OllamaModel
    {
        get => App.Settings.OllamaModel;
        set
        {
            if (string.IsNullOrEmpty(value) || value == App.Settings.OllamaModel) return;
            App.Settings.OllamaModel = value;
            App.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Lists the installed models without starting Ollama; opening the page is not a reason to launch a server.</summary>
    [RelayCommand]
    private async Task RefreshModels()
    {
        var models = await Task.Run(() => Ollama.IsUp() ? Ollama.Models().Select(m => m.Name).ToList() : new List<string>());
        OllamaModels.Clear();
        foreach (var m in models) OllamaModels.Add(m);
        PullStatus = models.Count == 0 ? Loc.T("x.model.none") : "";
        var best = Ollama.Best();
        if (best is not null && best != App.Settings.OllamaModel && !models.Contains(App.Settings.OllamaModel)) OllamaModel = best;
        OnPropertyChanged(nameof(OllamaModel));
    }

    [RelayCommand]
    private async Task PullModel()
    {
        var tag = PullTag.Trim();
        if (tag.Length == 0) return;
        PullBusy = true;
        try
        {
            await Task.Run(() => Ollama.PullAsync(tag, s => Dispatcher.UIThread.Post(() => PullStatus = Loc.T("x.model.pulling", tag) + " " + s)));
            App.Settings.OllamaModel = tag; App.Settings.Save();
            PullStatus = "✓ " + tag;
        }
        catch (Exception ex) { PullStatus = ex.Message; }
        PullBusy = false;
        await RefreshModels();
    }

    partial void OnSelectedChanged(NavItem? value)
    {
        if (value?.Key == "general") _ = RefreshModels();
    }

    // ---------------------------------------------------------------- donut profiles
    public ObservableCollection<string> ProfileChoices { get; } = new();

    [ObservableProperty] private int _profileIndex;

    partial void OnProfileIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomProfile));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileExtensions));
        OnPropertyChanged(nameof(ProfileHint));
        RefreshPresets();
    }

    public bool IsCustomProfile => ProfileIndex > 0;
    public string ProfileHint => Loc.T(IsCustomProfile ? "x.profile_hint_custom" : "x.profile_hint_default");

    private DonutProfile? CurrentProfile =>
        ProfileIndex > 0 && ProfileIndex - 1 < App.Settings.Profiles.Count ? App.Settings.Profiles[ProfileIndex - 1] : null;

    /// <summary>The preset ids of the donut being edited: the default one, or a profile's.</summary>
    public List<string> CurrentIds => FaceIndex == 0
        ? CurrentProfile?.PresetIds ?? App.Settings.DonutIds
        : CurrentProfile?.BackPresetIds ?? App.Settings.DonutBackIds;

    /// <summary>The ids of both faces of the donut being edited, for the preview.</summary>
    public List<string> FrontIds => CurrentProfile?.PresetIds ?? App.Settings.DonutIds;
    public List<string> BackIds => CurrentProfile is { BackPresetIds.Count: > 0 } p ? p.BackPresetIds : App.Settings.DonutBackIds;

    public string ProfileName
    {
        get => CurrentProfile?.Name ?? "";
        set
        {
            if (CurrentProfile is not { } p) return;
            p.Name = value; App.Settings.Save();
            var i = ProfileIndex;
            if (i > 0 && i < ProfileChoices.Count && ProfileChoices[i] != value) { ProfileChoices[i] = value; ProfileIndex = i; }
        }
    }

    public string ProfileExtensions
    {
        get => string.Join(", ", CurrentProfile?.Extensions ?? new());
        set
        {
            if (CurrentProfile is not { } p) return;
            p.Extensions = value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToList();
            App.Settings.Save();
        }
    }

    private void LoadProfiles()
    {
        SeedProfiles();
        ProfileChoices.Clear();
        ProfileChoices.Add(Loc.T("profile.mixed"));
        foreach (var p in App.Settings.Profiles) ProfileChoices.Add(p.Name);
    }

    [RelayCommand]
    private void NewProfile()
    {
        App.Settings.Profiles.Add(new DonutProfile { Name = Loc.T("toolbar.profile_new") });
        App.Settings.Save();
        ProfileChoices.Add(App.Settings.Profiles[^1].Name);
        ProfileIndex = ProfileChoices.Count - 1;
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (CurrentProfile is not { } p) return;
        App.Settings.Profiles.Remove(p);
        App.Settings.Save();
        ProfileChoices.RemoveAt(ProfileIndex);
        ProfileIndex = 0;
    }

    /// <summary>The first profile whose extensions cover every picked file, or null.</summary>
    public static DonutProfile? ProfileFor(IEnumerable<string> files)
    {
        var exts = files.Select(f => Directory.Exists(f) ? "" : Path.GetExtension(f).TrimStart('.').ToLowerInvariant()).Distinct().ToList();
        return App.Settings.Profiles.FirstOrDefault(p => p.Extensions.Count > 0 && exts.All(p.Extensions.Contains));
    }

    private const int ProfilesVersionNow = 4;

    /// <summary>Filee's own profile names, the built-in donuts. A newer version replaces them; donuts the user made stay.</summary>
    private static void SeedProfiles()
    {
        if (App.Settings.ProfilesVersion >= ProfilesVersionNow) return;
        var oldNames = new[] { "images", "pdf", "video", "audio", "office", "spreadsheets", "data", "subtitles", "archives", "ebooks", "fonts", "models", "geo", "cad", "hwp", "vector" }
            .Select(k => Loc.T("profile." + k)).ToHashSet();
        App.Settings.Profiles.RemoveAll(p => p.Builtin || (App.Settings.ProfilesSeeded && oldNames.Contains(p.Name)));
        App.Settings.ProfilesSeeded = true;
        App.Settings.ProfilesVersion = ProfilesVersionNow;

        DonutProfile P(string key, string exts, string ids, string back = "") => new()
        {
            Name = Loc.T("profile." + key), Builtin = true,
            Extensions = exts.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            PresetIds = ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            BackPresetIds = back.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
        };
        App.Settings.Profiles.AddRange(new[]
        {
            P("images", "png jpg jpeg webp bmp gif tif tiff ico heic avif", "png jpg removebg enhance upscale2 faceblur erase annotate",
                "webp img-pdf hide winocr under-200 width-1080 strip turn-90"),
            P("pdf", "pdf", "pdf-png pdf-txt docx xlsx pptx pdf-merge pdf-split pdfedit", "pdf-protect pdf-unlock pdf-range pdf-small nup4 rot90 strip doc-pdf"),
            P("video", "mp4 mov mkv webm avi m4v", "vid-discord vid-wa vid-mail vid-small clip captions vid-mp3 vid-gif",
                "vid-mute flip-h turn-90 crop-1-1 crop-16-9 crop-auto vid-frame merge-vid"),
            P("audio", "mp3 wav flac m4a ogg opus aac", "mp3 m4a wav flac ogg captions aud-small merge-aud"),
            P("office", "docx doc odt rtf txt md html", "doc-pdf docx txt md html epub odt tr-en"),
            P("spreadsheets", "xlsx xls csv tsv", "csv xlsx tsv json doc-pdf sheet-pdf"),
            P("data", "json yaml yml toml xml jsonl", "json yaml toml xml csv jsonl"),
            P("subtitles", "srt vtt ass ssa lrc", "srt vtt ass lrc txt tr-en"),
            P("archives", "zip rar 7z tar gz", "extract zip 7z targz"),
            P("ebooks", "epub mobi azw3 fb2 cbz", "txt doc-pdf mobi azw3 epub kepub"),
            P("fonts", "ttf otf woff woff2 eot", "ttf woff woff2 eot"),
            P("models", "stl obj ply glb gltf 3mf fbx", "stl obj ply glb gltf 3mf fbx"),
            P("geo", "geojson kml kmz gpx shp", "geojson kml gpx shp csv"),
            P("cad", "dxf dwg", "dxf dwg cad-svg png doc-pdf"),
            P("hwp", "hwp hwpx", "hwp-pdf hwp-md txt hwpx"),
            P("vector", "svg eps ps ai", "png doc-pdf jpg"),
        });
        App.Settings.Save();
    }
}
