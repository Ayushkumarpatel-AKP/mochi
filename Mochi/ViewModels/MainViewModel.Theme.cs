using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mochi.Services;

namespace Mochi.ViewModels;

/// <summary>One look in the gallery: a little swatch of its background, card, text and accent.</summary>
public sealed partial class ThemeTile : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public IBrush Bg { get; }
    public IBrush Card { get; }
    public IBrush Text { get; }
    public IBrush Accent { get; }
    public IRelayCommand Select { get; }
    [ObservableProperty] private IBrush _ring = Brushes.Transparent;

    public ThemeTile(ThemeDef d, Action<string> pick)
    {
        Id = d.Id; Name = d.Name;
        // the default look has no colours of its own: show the iOS-style dark swatch with the user's accent
        Bg = new SolidColorBrush(Color.Parse(d.Dark is null ? "#000000" : d.Bg));
        Card = new SolidColorBrush(Color.Parse(d.Dark is null ? "#1C1C1E" : d.Card));
        Text = new SolidColorBrush(Color.Parse(d.Dark is null ? "#FFFFFF" : d.Text));
        Accent = new SolidColorBrush(Color.Parse(d.Dark is null ? "#F0456B" : d.Accent));
        Select = new RelayCommand(() => pick(d.Id));
    }
}

/// <summary>The Theme page: looks, appearance, accent, surfaces, glass, density, text, delight, and sharing a look as a code.</summary>
public partial class MainViewModel
{
    public ObservableCollection<ThemeTile> ThemeTiles { get; } = new();
    public ObservableCollection<FilterChip> ModeChips { get; } = new();
    public ObservableCollection<FilterChip> MaterialChips { get; } = new();
    public ObservableCollection<FilterChip> DensityChips { get; } = new();
    public List<string> FontChoices { get; } = new();

    [ObservableProperty] private string _themeStatus = "";

    private void InitThemeUi()
    {
        ThemeTiles.Clear();
        foreach (var d in ThemeEngine.Themes) ThemeTiles.Add(new ThemeTile(d, PickTheme));
        FillChips(ModeChips, new[] { ("System", "x.th.system", "System"), ("Light", "x.th.light", "Light"), ("Dark", "x.th.dark", "Dark"), ("Auto", "x.th.auto", "Auto") }, k => { App.Settings.Mode = k; ThemeChanged(); });
        FillChips(MaterialChips, new[] { ("None", "x.th.solid", "Solid"), ("Mica", "x.th.mica", "Mica"), ("Acrylic", "x.th.acrylic", "Acrylic") }, k => { App.Settings.Material = k; ThemeChanged(); });
        FillChips(DensityChips, new[] { ("Compact", "x.th.compact", "Compact"), ("Comfortable", "x.th.comfortable", "Comfortable"), ("Spacious", "x.th.spacious", "Spacious") }, k => { App.Settings.Density = k; ThemeChanged(); RefreshNav(); });
        FontChoices.Clear();
        FontChoices.AddRange(ThemeEngine.Fonts().Select(f => f.Length == 0 ? Loc.TryGet("x.th.font_default") ?? "Mochi (default)" : f));
        RefreshThemeUi();
    }

    private static void FillChips(ObservableCollection<FilterChip> to, (string Key, string Loc, string Fallback)[] items, Action<string> apply)
    {
        to.Clear();
        foreach (var (key, loc, fallback) in items) to.Add(new FilterChip(key, Services.Loc.TryGet(loc) ?? fallback, apply));
    }

    /// <summary>Applies the settings to the whole app, saves them, and refreshes the page.</summary>
    private void ThemeChanged()
    {
        App.ApplyTheme();
        App.Settings.Save();
        RefreshThemeUi();
    }

    private void RefreshThemeUi()
    {
        var s = App.Settings;
        foreach (var t in ThemeTiles) t.Ring = t.Id == s.ThemePreset ? new SolidColorBrush(ThemeEngine.CurrentAccent) : new SolidColorBrush(Colors.White, 0.12);
        foreach (var c in ModeChips) c.IsOn = c.Key == s.Mode;
        foreach (var c in MaterialChips) c.IsOn = c.Key == s.Material;
        foreach (var c in DensityChips) c.IsOn = c.Key == s.Density;
        foreach (var n in new[] { nameof(ModeEnabled), nameof(ShowAutoTimes), nameof(AutoFrom), nameof(AutoTo), nameof(AccentHex), nameof(AccentHue), nameof(AccentCycle), nameof(BgTint), nameof(Oled), nameof(Softness),
                                  nameof(FontIndex), nameof(TextScale), nameof(SoundEffects), nameof(SoundVolume), nameof(OledEnabled), nameof(ThemeName) })
            OnPropertyChanged(n);
    }

    private void PickTheme(string id)
    {
        var d = ThemeEngine.Find(id);
        App.Settings.ThemePreset = d.Id;
        App.Settings.Accent = d.Dark is not null ? d.Accent : "#F0456B";   // each look brings its own accent; change it afterwards if you like
        ThemeChanged();
        ThemeStatus = "";
    }

    public string ThemeName => ThemeEngine.Find(App.Settings.ThemePreset).Name;
    /// <summary>Appearance (System / Light / Dark / Auto) only matters for the default look: the others are one fixed look.</summary>
    public bool ModeEnabled => ThemeEngine.Find(App.Settings.ThemePreset).Dark is null;
    public bool ShowAutoTimes => ModeEnabled && App.Settings.Mode == "Auto";
    public bool OledEnabled => ThemeEngine.Current.Dark;

    public string AutoFrom
    {
        get => App.Settings.AutoDarkFrom;
        set { if (ThemeEngine.TryTime(value, out _)) { App.Settings.AutoDarkFrom = value.Trim(); ThemeChanged(); } }
    }
    public string AutoTo
    {
        get => App.Settings.AutoDarkTo;
        set { if (ThemeEngine.TryTime(value, out _)) { App.Settings.AutoDarkTo = value.Trim(); ThemeChanged(); } }
    }

    public string AccentHex
    {
        get => App.Settings.Accent;
        set { if (Color.TryParse(value.StartsWith('#') ? value : "#" + value, out var c)) { App.Settings.Accent = ThemeEngine.Hex(c); App.Settings.AccentCycle = false; ThemeChanged(); } }
    }
    public double AccentHue
    {
        get => Color.TryParse(App.Settings.Accent, out var c) ? ThemeEngine.HueOf(c) : 210;
        set { App.Settings.Accent = ThemeEngine.Hex(ThemeEngine.FromHsv(value, 0.72, 1.0)); App.Settings.AccentCycle = false; App.ApplyTheme(); App.Settings.Save(); OnPropertyChanged(nameof(AccentHex)); OnPropertyChanged(nameof(AccentCycle)); }
    }
    public bool AccentCycle { get => App.Settings.AccentCycle; set { App.Settings.AccentCycle = value; ThemeChanged(); } }
    public double BgTint { get => App.Settings.BgTint; set { App.Settings.BgTint = value; App.ApplyTheme(); App.Settings.Save(); } }
    public bool Oled { get => App.Settings.Oled; set { App.Settings.Oled = value; ThemeChanged(); } }
    public double TextScale { get => App.Settings.TextScale; set { App.Settings.TextScale = Math.Round(value, 2); App.ApplyTheme(); App.Settings.Save(); } }
    public bool SoundEffects { get => App.Settings.SoundEffects; set { App.Settings.SoundEffects = value; App.Settings.Save(); } }
    public double SoundVolume { get => App.Settings.SoundVolume; set { App.Settings.SoundVolume = Math.Round(value, 2); App.Settings.Save(); } }
    [RelayCommand] private void PreviewSound() => Sounds.Done(force: true);

    public int FontIndex
    {
        get => Math.Max(0, ThemeEngine.Fonts().IndexOf(App.Settings.UiFont));
        set { var f = ThemeEngine.Fonts(); App.Settings.UiFont = f[Math.Clamp(value, 0, f.Count - 1)]; ThemeChanged(); }
    }

    /// <summary>Puts the sidebar back together so a new density takes effect.</summary>
    private void RefreshNav()
    {
        var key = Selected?.Key ?? "home";
        Nav.Clear();
        foreach (var k in Keys) Nav.Add(new NavItem(k, Loc.T("nav." + k)));
        Selected = Nav.First(n => n.Key == key);
    }

    // ---------------------------------------------------------------- share, surprise, reset

    [RelayCommand]
    private async Task CopyThemeCode()
    {
        if (App.MainWindowRef is { Clipboard: { } cb }) { await cb.SetTextAsync(ThemeEngine.Export()); ThemeStatus = Loc.TryGet("x.th.copied") ?? "Theme code copied. Paste it anywhere to share your look."; }
    }

    [RelayCommand]
    private async Task PasteThemeCode()
    {
        var text = App.MainWindowRef is { Clipboard: { } cb } ? await cb.GetTextAsync() : null;
        if (text is not null && ThemeEngine.Import(text)) { ThemeChanged(); RefreshNav(); ThemeStatus = Loc.TryGet("x.th.pasted") ?? "Theme applied."; }
        else ThemeStatus = Loc.TryGet("x.th.bad_code") ?? "The clipboard has no Mochi theme code (it starts with MOCHI1:).";
    }

    [RelayCommand] private void SurpriseMe() { ThemeEngine.Surprise(); ThemeChanged(); ThemeStatus = Loc.TryGet("x.th.surprise") ?? "Fresh look. Press again for another."; }
    [RelayCommand] private void ResetTheme() { ThemeEngine.Reset(); ThemeChanged(); RefreshNav(); ThemeStatus = Loc.TryGet("x.th.reset") ?? "Back to the default look."; }
}
