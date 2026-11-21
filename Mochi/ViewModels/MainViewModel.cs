using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mochi.Services;
using Microsoft.VisualBasic.FileIO;

namespace Mochi.ViewModels;

/// <summary>String lookup for bindings like T[home.title]; Refresh() re-reads every one after a language change.</summary>
public sealed class Tr : System.ComponentModel.INotifyPropertyChanged
{
    public string this[string key] => Loc.T(key);
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
}

public sealed record NavItem(string Key, string Label)
{
    private static readonly Dictionary<string, string> IconData = new()
    {
        ["home"] = "M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M9 22V12h6v10",
        ["presets"] = "M12 2 2 7l10 5 10-5-10-5z M2 17l10 5 10-5 M2 12l10 5 10-5",
        ["ai"] = "M12 8V4H8 M4 8h16a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2z M2 14h2 M20 14h2 M15 13v2 M9 13v2",
        ["smart"] = "M10 4l1.8 5.2L17 11l-5.2 1.8L10 18l-1.8-5.2L3 11l5.2-1.8z M19 3v4 M17 5h4",
        ["engines"] = "M6 4h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M9 9h6v6H9z M9 2v2 M15 2v2 M9 20v2 M15 20v2 M2 9h2 M2 15h2 M20 9h2 M20 15h2",
        ["watch"] = "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z",
        ["triggers"] = "M15 6v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3V6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3",
        ["toolbar"] = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M8 12a4 4 0 1 0 8 0a4 4 0 1 0-8 0",
        ["fields"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M3 9h18 M3 15h18 M9 9v12",
        ["general"] = "M12 2l2 2.5 3.2-.6.9 3.1 3 1.2-.8 3.2L22 12l-2.2 2.5.8 3.2-3 1.2-.9 3.1-3.2-.6L12 22l-2-2.5-3.2.6-.9-3.1-3-1.2.8-3.2L2 12l2.2-2.5-.8-3.2 3-1.2.9-3.1 3.2.6z M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0",
        ["theme"] = "M18.37 2.63 14 7l-1.59-1.59a2 2 0 0 0-2.82 0L8 7l9 9 1.59-1.59a2 2 0 0 0 0-2.82L17 10l4.37-4.37a2.12 2.12 0 1 0-3-3z M9 8c-2 3-4 3.5-7 4l8 10c2-1 6-5 6-7 M14.5 17.5 4.5 15",
        ["about"] = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M12 16v-4 M12 8h.01",
    };

    public Geometry? Icon => IconData.TryGetValue(Key, out var d) ? Geometry.Parse(d) : null;
    public bool DividerBefore => Key == "general";
    public Thickness Gap
    {
        get
        {
            var k = App.Settings.Density switch { "Compact" => 0.35, "Spacious" => 1.6, _ => 1.0 };
            return DividerBefore ? new Thickness(0, 26 * k, 0, 3 * k) : new Thickness(0, 3 * k);
        }
    }
}

public sealed record RecentItem(string Name, string Path, string Detail, bool Failed, IReadOnlyList<string> Outputs);

/// <summary>One slice in the donut being edited (a preset id or a group id) with its label.</summary>
public sealed record SliceRow(string Id, string Label);

/// <summary>A row on the Presets page: a preset, or a group of presets, with its "in donut" switch.</summary>
public sealed partial class PresetRow : ObservableObject
{
    private readonly MainViewModel _vm;
    private readonly string _id;
    public PresetRow(Preset p, bool inDonut, MainViewModel vm)
        : this(p.Id, p.DisplayName, p.Extension.Length == 0 ? Loc.T("format.category." + p.Category) : $"{Loc.T("format.category." + p.Category)} → {p.Extension}", inDonut, vm, p.Category, p.Extension) { }
    public PresetRow(string id, string name, string detail, bool inDonut, MainViewModel vm, string category = "group", string ext = "")
    {
        _id = id; Name = name; Detail = detail; _inDonut = inDonut; _vm = vm;
        var (g, tint) = Look.Of(category);
        Glyph = g; Tint = tint; Badge = ext.Length > 1 ? ext[1..].ToUpperInvariant() : "";
    }
    public string Name { get; }
    public string Detail { get; }
    public string Glyph { get; }
    public Avalonia.Media.IBrush Tint { get; }
    public string Badge { get; }
    public bool HasBadge => Badge.Length > 0;

    [ObservableProperty] private bool _inDonut;
    partial void OnInDonutChanged(bool value) => _vm.SetInDonut(_id, value);
}

public sealed partial class WatchRow : ObservableObject
{
    public WatchEntry Entry { get; }
    private readonly MainViewModel _vm;
    public WatchRow(WatchEntry e, MainViewModel vm)
    {
        Entry = e; _vm = vm;
        _preset = Converter.Find(e.PresetId) ?? Converter.Presets[0];
        _enabled = e.Enabled;
    }
    public string Path => Entry.Path;

    [ObservableProperty] private Preset _preset;
    [ObservableProperty] private bool _enabled;
    partial void OnPresetChanged(Preset value) { Entry.PresetId = value.Id; _vm.WatchChanged(); }
    partial void OnEnabledChanged(bool value) { Entry.Enabled = value; _vm.WatchChanged(); }
}

public sealed partial class FieldRow : ObservableObject
{
    private readonly string _cat;
    public FieldRow(string cat, bool on) { _cat = cat; _enabled = on; }
    public string Name => Loc.T("format.category." + _cat);

    [ObservableProperty] private bool _enabled;
    partial void OnEnabledChanged(bool value)
    {
        var d = App.Settings.DisabledFields;
        d.Remove(_cat);
        if (!value) d.Add(_cat);
        App.Settings.Save();
        App.Vm?.RefreshPresets();
    }
}

public sealed partial class EngineRow : ObservableObject
{
    private readonly MainViewModel _vm;
    public IEngine Engine { get; }
    public EngineRow(IEngine e, MainViewModel vm) { Engine = e; _vm = vm; Refresh(); }

    // Filee's own translated engine texts where it has them
    private static readonly Dictionary<string, string> FileeKey = new()
    {
        ["data"] = "data", ["spreadsheet"] = "spreadsheet", ["subtitle"] = "subtitle", ["ebook"] = "ebook", ["email"] = "email", ["font"] = "font",
        ["geo"] = "geo", ["cad"] = "cad", ["model3d"] = "model3d", ["vector"] = "vector", ["archive"] = "archive", ["ffmpeg"] = "ffmpeg",
        ["magick"] = "magick", ["ghostscript"] = "ghostscript", ["pandoc"] = "pandoc", ["libreoffice"] = "libreoffice", ["tesseract"] = "tesseract",
        ["whisper"] = "whisper", ["calibre"] = "calibre", ["rhwp"] = "rhwp", ["pdf"] = "pdfsharp", ["icns"] = "icns", ["document"] = "markdown", ["translate"] = "translator",
    };
    public string Name => FileeKey.TryGetValue(Engine.Id, out var k) && Loc.TryGet("engine." + k + ".name") is { } n ? n : Engine.Name;
    public string Description => FileeKey.TryGetValue(Engine.Id, out var k) && Loc.TryGet("engine." + k + ".description") is { } d ? d : Engine.Description;
    public string Formats => Engine.Formats;
    public bool IsExternal => Engine.Tool is not null;
    public bool CanInstall => Installer.CanInstall(Engine) && !Engine.Ready;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _ready;
    [ObservableProperty] private bool _busy;

    public void Refresh()
    {
        Ready = Engine.Ready;
        Status = Engine.Tool is null ? Loc.T("x.built_in") : Ready ? Loc.T("x.ready_at", Tools.Find(Engine.Tool) ?? "") : Loc.T("x.not_installed");
        OnPropertyChanged(nameof(CanInstall));
    }

    [RelayCommand]
    private async Task Install()
    {
        Busy = true;
        try { await Installer.InstallAsync(Engine, s => Dispatcher.UIThread.Post(() => Status = s)); }
        catch (Exception ex) { Status = Loc.T("x.install_failed", ex.Message); Busy = false; return; }
        Busy = false;
        _vm.RefreshEngines();
    }

    [RelayCommand] private void Up() => _vm.MoveEngine(this, -1);
    [RelayCommand] private void Down() => _vm.MoveEngine(this, 1);

    [RelayCommand]
    private async Task UseMyCopy()
    {
        if (_vm.PickExe is null || Engine.Tool is null) return;
        var f = await _vm.PickExe();
        if (f is null) return;
        App.Settings.ToolPaths[Engine.Tool] = f;
        App.Settings.Save();
        _vm.RefreshEngines();
    }
}

public partial class MainViewModel : ObservableObject
{
    private static readonly string[] Keys =
        { "home", "presets", "smart", "ai", "engines", "watch", "triggers", "toolbar", "fields", "general", "theme", "about" };
    private static readonly string[] Categories =
        { "Archive", "Audio", "Cad", "Files", "Data", "Document", "Ebook", "Font", "Geo", "Hwp", "Image", "Model3D", "Pdf", "Presentation", "Spreadsheet", "Subtitle", "Text", "Vector", "Video" };

    public Tr T { get; } = new();
    public ObservableCollection<NavItem> Nav { get; } = new(Keys.Select(k => new NavItem(k, Loc.T("nav." + k))));
    public ObservableCollection<RecentItem> Recent { get; } = new();
    public ObservableCollection<PresetRow> PresetRows { get; } = new();
    public ObservableCollection<SliceRow> DonutList { get; } = new();
    public ObservableCollection<WatchRow> WatchRows { get; } = new();
    public ObservableCollection<FieldRow> FieldRows { get; } = new();
    public ObservableCollection<EngineRow> EngineRows { get; } = new();
    public ObservableCollection<Preset> HomePresets { get; } = new();

    public string[] Modes { get; } = { "System", "Light", "Dark" };
    public string[] Modifiers { get; } = { "Alt or Shift", "Alt", "Shift", "Ctrl" };
    public string[] Languages { get; } = { "en", "de", "es", "fr", "hi", "id", "it", "ja", "ko", "pl", "pt-BR", "pt-PT", "ru", "tr", "vi", "zh-CN", "zh-TW" };
    public string[] Swatches { get; } = { "#F0456B", "#0A84FF", "#30D158", "#FF9F0A", "#FF375F", "#BF5AF2", "#64D2FF", "#AC8E68", "#8E8E93" };

    /// <summary>Set by the window: opens a file picker for "Use my copy…".</summary>
    public Func<Task<string?>>? PickExe { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle), nameof(IsHome), nameof(IsPresets), nameof(IsSmart), nameof(IsAi), nameof(IsEngines), nameof(IsWatch),
        nameof(IsTriggers), nameof(IsToolbar), nameof(IsFields), nameof(IsGeneral), nameof(IsTheme), nameof(IsAbout))]
    private NavItem? _selected;

    [ObservableProperty] private Preset? _preset;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _presetFilter = "";
    partial void OnPresetFilterChanged(string value) => RefreshPresets();
    public string PresetCategory { get; private set; } = "all";

    public MainViewModel()
    {
        foreach (var c in Categories) FieldRows.Add(new FieldRow(c, !App.Settings.DisabledFields.Contains(c)));
        foreach (var w in App.Settings.Watch) WatchRows.Add(new WatchRow(w, this));
        LoadProfiles();
        LoadGroups();
        InitThemeUi();
        RefreshPresets();
        RefreshEngines();
        foreach (var h in App.Settings.History) Recent.Add(new RecentItem(h.Name, h.Path, h.Detail, h.Failed, h.Outputs));
        InitTiles();
        InitSearchFolders();
        Selected = Nav[0];
    }

    private static bool Allowed(Preset p) => !App.Settings.DisabledFields.Contains(p.Category) && !Ai.Hidden(p);

    /// <summary>Rebuilds every list that depends on which presets exist and which are in the donut.</summary>
    public void RefreshPresets()
    {
        var cur = Preset?.Id;
        PresetRows.Clear(); HomePresets.Clear();
        var total = 0;
        var f = PresetFilter.Trim();
        var key = PresetCategory + "|" + f;
        if (key != _presetKey) { _presetKey = key; _presetPage = 0; }   // a new filter starts on page 1; toggling a tool stays on the page
        var cat = PresetCategory;
        if (f.Length == 0 && cat is "all" or "donut" && _presetPage == 0)
            foreach (var g in App.Settings.Groups.Where(g => g.Id != "ai" || Ai.IsConfigured))
                if (cat != "donut" || CurrentIds.Contains("g:" + g.Id))
                    PresetRows.Add(new PresetRow("g:" + g.Id, "▸ " + g.Name, Loc.T("x.group"), CurrentIds.Contains("g:" + g.Id), this));
        foreach (var p in Converter.Presets.Where(Allowed))
        {
            if (p.Category != "Ai") HomePresets.Add(p);   // the Home picker keeps its everyday presets; AI tools live in the toolbars
            var typed = f.Length == 0 || p.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Category.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Extension.Contains(f, StringComparison.OrdinalIgnoreCase);
            var chip = cat == "all" || (cat == "donut" ? CurrentIds.Contains(p.Id) : cat == p.Category);
            if (!typed || !chip) continue;
            total++;
            if (total > _presetPage * PresetPageSize && total <= (_presetPage + 1) * PresetPageSize) PresetRows.Add(new PresetRow(p, CurrentIds.Contains(p.Id), this));
        }
        PresetTotal = total;
        RefreshPager(total);
        RefreshPresetChips();
        RebuildDonutList();
        NotifyDonutPreview();
        Preset = HomePresets.FirstOrDefault(p => p.Id == cur) ?? HomePresets.FirstOrDefault();
    }

    public void RefreshEngines()
    {
        Tools.Reset();
        EngineRows.Clear();
        foreach (var e in Converter.Engines) EngineRows.Add(new EngineRow(e, this));
        BuildEngineGroups();
    }

    public void MoveEngine(EngineRow row, int d)
    {
        var i = EngineRows.IndexOf(row); var j = i + d;
        if (i < 0 || j < 0 || j >= EngineRows.Count) return;
        EngineRows.Move(i, j);
        Converter.SaveOrder(EngineRows.Select(r => r.Engine));
    }

    [RelayCommand] private void CheckEnginesAgain() => RefreshEngines();

    // ---- page flags
    public string PageTitle => Selected?.Label ?? "";
    private bool Is(string k) => Selected?.Key == k;
    public bool IsHome => Is("home");
    public bool IsPresets => Is("presets");
    public bool IsEngines => Is("engines");
    public bool IsSmart => Is("smart");
    public bool IsAi => Is("ai");
    public Avalonia.Media.IBrush SearchTint => Look.Of("Files").Tint;
    public Avalonia.Media.IBrush TidyTint => Look.Of("Archive").Tint;
    public bool IsWatch => Is("watch");
    public bool IsTriggers => Is("triggers");
    public bool IsToolbar => Is("toolbar");
    public bool IsFields => Is("fields");
    public bool IsGeneral => Is("general");
    public bool IsTheme => Is("theme");
    public bool IsAbout => Is("about");
    public string Version => Loc.T("about.version", typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.1.2");

    // ---- theme
    public int Mode
    {
        get => Math.Max(0, Array.IndexOf(Modes, App.Settings.Mode));
        set { App.Settings.Mode = Modes[Math.Max(0, value)]; ApplyTheme(); }
    }
    public double Softness { get => App.Settings.Softness; set { App.Settings.Softness = value; ApplyTheme(); } }
    public bool ReduceMotion { get => App.Settings.ReduceMotion; set { App.Settings.ReduceMotion = value; App.Settings.Save(); } }
    private static void ApplyTheme() { App.ApplyTheme(); App.Settings.Save(); }
    [RelayCommand] private void SetAccent(string hex) { App.Settings.Accent = hex; ApplyTheme(); }

    // ---- general / triggers / toolbar
    public int Language
    {
        get => Math.Max(0, Array.IndexOf(Languages, App.Settings.Language));
        set { App.Settings.Language = Languages[Math.Max(0, value)]; App.Settings.Save(); ApplyLanguage(); OnPropertyChanged(); }
    }
    public bool CloseToTray { get => App.Settings.CloseToTray; set { App.Settings.CloseToTray = value; App.Settings.Save(); } }
    public bool TriggersEnabled { get => App.Settings.TriggersEnabled; set { App.Settings.TriggersEnabled = value; App.Settings.Save(); } }
    public int Modifier
    {
        get => Math.Max(0, Array.IndexOf(Modifiers, App.Settings.TriggerModifier));
        set { App.Settings.TriggerModifier = Modifiers[Math.Max(0, value)]; App.Settings.Save(); OnPropertyChanged(nameof(TriggerHint)); }
    }
    public string TriggerHint => Loc.T("x.trigger_hint", App.Settings.TriggerModifier);
    public string WhatsNewLabel => Loc.T("whats_new.about_link", "2.3.0");
    public double DonutOpacity { get => App.Settings.DonutOpacity; set { App.Settings.DonutOpacity = value; App.Settings.Save(); } }

    public void SetInDonut(string id, bool on)
    {
        var ids = CurrentIds;
        ids.Remove(id);
        var added = on && ids.Count < DonutSlots;
        if (added) ids.Add(id);
        App.Settings.Save();
        RebuildDonutList();
        NotifyDonutPreview();
        if (added) ExplainProfiles(id); else ClearDonutNote();
        if (on && !added) Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPresets);   // donut full: the switch goes back off
    }

    private string LabelOf(string id) => id.StartsWith("g:")
        ? "▸ " + (App.Settings.Groups.FirstOrDefault(g => "g:" + g.Id == id)?.Name ?? id)
        : Converter.Find(id)?.DisplayName ?? id;

    private void RebuildDonutList()
    {
        DonutList.Clear();
        foreach (var id in CurrentIds)
            if (id.StartsWith("g:") ? App.Settings.Groups.Any(g => "g:" + g.Id == id) && (id != "g:ai" || Ai.IsConfigured) : Converter.Find(id) is { } q && Allowed(q))
                DonutList.Add(new SliceRow(id, LabelOf(id)));
    }

    [RelayCommand] private void MoveUp(SliceRow r) => Move(r, -1);
    [RelayCommand] private void MoveDown(SliceRow r) => Move(r, 1);
    private void Move(SliceRow r, int d)
    {
        var ids = CurrentIds;
        var i = ids.IndexOf(r.Id); var j = i + d;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        App.Settings.Save();
        RebuildDonutList();
    }

    // ---- watch folders
    public Action? OnWatchChanged { get; set; }
    public void WatchChanged() { App.Settings.Save(); OnWatchChanged?.Invoke(); }

    public void AddWatch(string folder)
    {
        if (App.Settings.Watch.Any(w => w.Path == folder)) return;
        var e = new WatchEntry { Path = folder, PresetId = Preset?.Id ?? "png" };
        App.Settings.Watch.Add(e);
        WatchRows.Add(new WatchRow(e, this));
        WatchChanged();
    }

    [RelayCommand]
    private void RemoveWatch(WatchRow r)
    {
        App.Settings.Watch.Remove(r.Entry);
        WatchRows.Remove(r);
        WatchChanged();
    }

    // ---- conversion
    [RelayCommand] private void ClearHistory() { Recent.Clear(); SaveHistory(); }

    /// <summary>The last 100 conversions are kept between runs.</summary>
    private void SaveHistory()
    {
        App.Settings.History = Recent.Take(100).Select(r => new HistoryEntry { Name = r.Name, Path = r.Path, Detail = r.Detail, Failed = r.Failed, Outputs = r.Outputs.ToList() }).ToList();
        App.Settings.Save();
    }

    public async Task ConvertAsync(IReadOnlyCollection<string> files, Preset? preset = null, string? outDir = null)
    {
        var p = preset ?? Preset;
        if (p is null || files.Count == 0) return;
        Dispatcher.UIThread.Post(() => { Busy = true; Status = Loc.T("x.converting", files.Count, p.DisplayName); });

        var jobs = p.IsMerge ? new List<IReadOnlyList<string>> { files.ToList() } : files.Select(f => (IReadOnlyList<string>)new[] { f }).ToList();
        var ok = 0;
        foreach (var job in jobs)
        {
            RecentItem item;
            try
            {
                var outs = await Task.Run(() => p.IsMerge ? Converter.RunMany(job, p, outDir) : Converter.Run(job[0], p, outDir));
                if (outs.Count == 0) { ok++; continue; }   // a tool window closed without saving: nothing to report
                item = new RecentItem(Path.GetFileName(outs[0].TrimEnd('\\', '/')) + (outs.Count > 1 ? $"  (+{outs.Count - 1})" : ""), outs[0], p.DisplayName, false, outs);
                ok++;
            }
            catch (Exception ex)
            {
                item = new RecentItem(Path.GetFileName(job[0].TrimEnd('\\', '/')), job[0], ex.Message, true, Array.Empty<string>());
            }
            await Dispatcher.UIThread.InvokeAsync(() => { Recent.Insert(0, item); SaveHistory(); });
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Busy = false;
            if (App.Settings.OpenFolderWhenDone && Recent.Count > 0 && !Recent[0].Failed) OpenFolder(Recent[0]);
            Status = Loc.T("x.done_of", ok, jobs.Count) + (ok < jobs.Count ? Loc.T("x.see_red") : "");
        });
    }

    [RelayCommand]
    private void OpenFolder(RecentItem r) =>
        System.Diagnostics.Process.Start("explorer.exe", Directory.Exists(r.Path) ? $"\"{r.Path}\"" : $"/select,\"{r.Path}\"");

    [RelayCommand]
    private void OpenFile(RecentItem r)
    {
        if (!r.Failed) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Path) { UseShellExecute = true });
    }

    /// <summary>Moves the results of one conversion to the Recycle Bin.</summary>
    [RelayCommand]
    private void Undo(RecentItem r)
    {
        var failed = new List<string>();
        foreach (var f in r.Outputs)
        {
            try
            {
                if (Directory.Exists(f)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(f, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else if (File.Exists(f)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) { failed.Add(Path.GetFileName(f) + ": " + ex.Message); }
        }
        Recent.Remove(r);
        Status = failed.Count == 0 ? Loc.T("home.undone") : Loc.T("home.undo_failed", failed.Count, failed[0]);
    }
}
