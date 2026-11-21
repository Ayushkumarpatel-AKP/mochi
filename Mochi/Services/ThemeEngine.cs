using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Mochi.Services;

/// <summary>A ready-made look. <see cref="Dark"/> null means "follows Appearance (System / Light / Dark / Auto)".</summary>
public sealed record ThemeDef(string Id, string Name, bool? Dark, string Bg, string Sidebar, string Card, string Text, string Secondary, string Accent);

/// <summary>The colours every window draws with right now.</summary>
public sealed record Palette(bool Dark, Color Bg, Color Sidebar, Color Card, Color Text, Color Secondary, Color Accent);

/// <summary>
/// Turns the theme settings (look, appearance, accent, tint, glass, density, font…) into the resources the whole app draws with.
/// One call, <see cref="Apply"/>, after any change; every window follows because they all use these resource keys.
/// </summary>
public static class ThemeEngine
{
    public static readonly ThemeDef[] Themes =
    {
        new("mochi", "Mochi", null, "", "", "", "", "", ""),
        new("midnight", "Midnight", true, "#0B1020", "#E6121A30", "#131A2E", "#E8ECFF", "#8C97B8", "#6C8CFF"),
        new("graphite", "Graphite", true, "#121212", "#E61A1A1A", "#1E1E1E", "#EDEDED", "#9A9A9A", "#8AB4F8"),
        new("dracula", "Dracula", true, "#1E1F29", "#E6282A36", "#282A36", "#F8F8F2", "#9AA0C0", "#BD93F9"),
        new("nord", "Nord", true, "#2B303B", "#E6343B4A", "#3B4252", "#ECEFF4", "#A3ABBE", "#88C0D0"),
        new("forest", "Forest", true, "#0D1512", "#E6121E19", "#15231D", "#E6F2EA", "#8DA89A", "#4ADE80"),
        new("sunset", "Sunset", true, "#1B0F14", "#E6261419", "#2A1820", "#FFEDE8", "#C79A9A", "#FF7A59"),
        new("solar", "Solar", true, "#002B36", "#E6073642", "#073642", "#EEE8D5", "#93A1A1", "#E0A800"),
        new("sakura", "Sakura", false, "#FFF5F7", "#E6FFE8EE", "#FFFFFF", "#3A1F2B", "#A5788A", "#FF5C8A"),
        new("paper", "Paper", false, "#F6F1E7", "#E6EFE8D8", "#FFFDF8", "#2B2620", "#8C8272", "#C2703D"),
        new("ocean", "Ocean", false, "#EEF5FB", "#E6DCEAF6", "#FFFFFF", "#0F2233", "#6B8299", "#1E88E5"),
        new("mono", "Mono", false, "#FFFFFF", "#E6F4F4F4", "#F7F7F7", "#111111", "#777777", "#111111"),
    };

    public static readonly string[] AccentSwatches = { "#F0456B", "#0A84FF", "#30D158", "#FF9F0A", "#FF375F", "#BF5AF2", "#64D2FF", "#AC8E68", "#8E8E93" };

    public static ThemeDef Find(string id) => Themes.FirstOrDefault(t => t.Id == id) ?? Themes[0];

    public static Palette Current { get; private set; } = new(false, Color.Parse("#FFF4F6"), Color.Parse("#E6FFE3EA"), Color.Parse("#FFFDFD"), Color.Parse("#3A1F2B"), Color.Parse("#A5788A"), Color.Parse("#F0456B"));

    /// <summary>The accent in use this instant (it moves while "Living accent" is on).</summary>
    public static Color CurrentAccent { get; private set; } = Color.Parse("#F0456B");

    private static DispatcherTimer? _cycle, _clock;
    private static double _hue;
    private static bool? _nightShown;

    // ---------------------------------------------------------------- applying

    public static void Apply()
    {
        var app = Application.Current;
        if (app is null) return;
        var s = App.Settings;
        var def = Find(s.ThemePreset);

        bool? forced = def.Dark ?? s.Mode switch { "Light" => false, "Dark" => true, "Auto" => NightNow(s), _ => (bool?)null };
        app.RequestedThemeVariant = forced is null ? ThemeVariant.Default : forced.Value ? ThemeVariant.Dark : ThemeVariant.Light;
        var dark = forced ?? app.ActualThemeVariant != ThemeVariant.Light;

        var accent = Color.TryParse(s.Accent, out var a) ? a : Color.Parse("#F0456B");

        Color bg, side, card, text, sec;
        if (def.Dark is null)
        {
            // the cosy Mochi look: strawberry-milk paper and warm cocoa
            if (dark) { bg = Color.Parse("#1D1216"); side = Color.Parse("#E62A1B20"); card = Color.Parse("#2A1C21"); text = Color.Parse("#FFEFF2"); sec = Color.Parse("#C9A0AD"); }
            else { bg = Color.Parse("#FFF4F6"); side = Color.Parse("#E6FFE3EA"); card = Color.Parse("#FFFDFD"); text = Color.Parse("#3A1F2B"); sec = Color.Parse("#A5788A"); }
        }
        else { bg = Color.Parse(def.Bg); side = Color.Parse(def.Sidebar); card = Color.Parse(def.Card); text = Color.Parse(def.Text); sec = Color.Parse(def.Secondary); }

        var tint = Math.Clamp(s.BgTint, 0, 1);
        if (tint > 0) { bg = Mix(bg, accent, tint * 0.16); card = Mix(card, accent, tint * 0.12); side = Mix(side, accent, tint * 0.14); }
        if (s.Oled && dark) { bg = Color.Parse("#000000"); card = Mix(card, Colors.Black, 0.35); side = Color.FromArgb(side.A, 0x06, 0x06, 0x08); }

        var glass = s.Material != "None";
        var sideShown = glass ? Color.FromArgb(dark ? (byte)0x78 : (byte)0x98, side.R, side.G, side.B) : side;

        var r = app.Resources;
        r["IosBgBrush"] = new SolidColorBrush(bg);
        r["IosSidebarBrush"] = new SolidColorBrush(sideShown);
        r["IosCardBrush"] = new SolidColorBrush(card);
        r["IosTextBrush"] = new SolidColorBrush(text);
        r["IosSecondaryBrush"] = new SolidColorBrush(sec);
        r["IosSeparatorBrush"] = new SolidColorBrush(text, dark ? 0.14 : 0.10);
        r["IosFillBrush"] = new SolidColorBrush(text, dark ? 0.12 : 0.07);
        SetAccent(accent);
        r["IosRadius"] = new CornerRadius(s.Softness);

        // text and spacing
        r["IosFont"] = new FontFamily(s.UiFont.Length == 0 ? "avares://Mochi/Assets/Fonts#Noto Sans" : s.UiFont);
        r["IosFontSize"] = 15.0 * Math.Clamp(s.TextScale, 0.8, 1.4);
        var (navV, cardPad) = s.Density switch { "Compact" => (7.0, 11.0), "Spacious" => (17.0, 24.0), _ => (12.0, 16.0) };
        r["IosNavPad"] = new Thickness(16, navV);
        r["IosCardPad"] = new Thickness(cardPad);

        Current = new Palette(dark, bg, side, card, text, sec, accent);
        ApplyMaterial(glass);
        RunCycle(s.AccentCycle);
        RunClock(s.Mode == "Auto" && def.Dark is null);
        Changed?.Invoke();
    }

    /// <summary>Raised after every <see cref="Apply"/>, for things that keep their own copy of the colours.</summary>
    public static event Action? Changed;

    private static void SetAccent(Color c)
    {
        CurrentAccent = c;
        var app = Application.Current!;
        app.Resources["IosAccentBrush"] = new SolidColorBrush(c);
        app.Resources["SystemAccentColor"] = c;   // sliders, focus rings and other Fluent controls follow the accent
    }

    /// <summary>Mica or Acrylic behind the main window; falls back to solid when Windows can't do it.</summary>
    private static void ApplyMaterial(bool glass)
    {
        if (App.MainWindowRef is not { } w) return;
        var kind = App.Settings.Material;
        w.TransparencyLevelHint = !glass ? new[] { WindowTransparencyLevel.None }
            : kind == "Acrylic" ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica } : new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur };
        if (!glass) { w.ClearValue(Window.BackgroundProperty); return; }
        w.Background = Brushes.Transparent;
        DispatcherTimer.RunOnce(() =>
        {
            if (App.Settings.Material != "None" && w.ActualTransparencyLevel == WindowTransparencyLevel.None) w.ClearValue(Window.BackgroundProperty);   // no glass here: stay solid
        }, TimeSpan.FromMilliseconds(500));
    }

    // ---------------------------------------------------------------- living accent and Auto appearance

    private static void RunCycle(bool on)
    {
        if (!on) { _cycle?.Stop(); return; }
        _cycle ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _cycle.Tick -= CycleTick; _cycle.Tick += CycleTick;
        if (!_cycle.IsEnabled) _cycle.Start();
    }

    private static void CycleTick(object? sender, EventArgs e)
    {
        if (App.Settings.ReduceMotion) return;
        _hue = (_hue + 0.8) % 360;
        SetAccent(FromHsv(_hue, 0.72, 1.0));
    }

    private static void RunClock(bool on)
    {
        if (!on) { _clock?.Stop(); _nightShown = null; return; }
        _nightShown = NightNow(App.Settings);
        _clock ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick -= ClockTick; _clock.Tick += ClockTick;
        if (!_clock.IsEnabled) _clock.Start();
    }

    private static void ClockTick(object? sender, EventArgs e)
    {
        if (NightNow(App.Settings) != _nightShown) Apply();   // sunset or sunrise reached
    }

    /// <summary>True between the "dark from" and "light from" times (it may span midnight).</summary>
    public static bool NightNow(Settings s)
    {
        TimeSpan From = Parse(s.AutoDarkFrom, 19), To = Parse(s.AutoDarkTo, 7), now = DateTime.Now.TimeOfDay;
        return From <= To ? now >= From && now < To : now >= From || now < To;
    }

    public static bool TryTime(string text, out TimeSpan t) => TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, null, out t) && t < TimeSpan.FromDays(1);

    private static TimeSpan Parse(string text, int fallbackHour) => TryTime(text, out var t) ? t : TimeSpan.FromHours(fallbackHour);

    // ---------------------------------------------------------------- colour helpers

    public static Color Mix(Color x, Color y, double t) =>
        Color.FromArgb(x.A, (byte)(x.R + (y.R - x.R) * t), (byte)(x.G + (y.G - x.G) * t), (byte)(x.B + (y.B - x.B) * t));

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s; var x = c * (1 - Math.Abs(h / 60 % 2 - 1)); var m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public static double HueOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), d = max - Math.Min(r, Math.Min(g, b));
        if (d < 1e-6) return 0;
        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---------------------------------------------------------------- fonts

    private static readonly string[] FontCandidates =
    {
        "Segoe UI", "Segoe UI Variable Text", "Bahnschrift", "Calibri", "Verdana", "Trebuchet MS", "Georgia", "Cambria", "Cascadia Code", "Consolas", "Comic Sans MS",
    };

    /// <summary>The bundled font first (empty name), then the nicer fonts this PC actually has.</summary>
    public static List<string> Fonts()
    {
        var have = new HashSet<string>(FontManager.Current.SystemFonts.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        return new[] { "" }.Concat(FontCandidates.Where(have.Contains)).ToList();
    }

    // ---------------------------------------------------------------- share, surprise, reset

    private sealed record Code(int V, string P, string M, string A, bool C, double T, bool O, string Mat, string D, string F, double S, double R);

    /// <summary>The whole look as one short text, to paste to a friend (or back later).</summary>
    public static string Export()
    {
        var s = App.Settings;
        var json = JsonSerializer.Serialize(new Code(1, s.ThemePreset, s.Mode, s.Accent, s.AccentCycle, s.BgTint, s.Oled, s.Material, s.Density, s.UiFont, s.TextScale, s.Softness));
        return "MOCHI1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool Import(string text)
    {
        try
        {
            text = text.Trim();
            if (!text.StartsWith("MOCHI1:")) return false;
            var b64 = text[7..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
            var c = JsonSerializer.Deserialize<Code>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            if (c is null || c.V != 1 || !Color.TryParse(c.A, out _)) return false;
            var s = App.Settings;
            s.ThemePreset = Find(c.P).Id; s.Mode = c.M is "System" or "Light" or "Dark" or "Auto" ? c.M : "System"; s.Accent = c.A; s.AccentCycle = c.C;
            s.BgTint = Math.Clamp(c.T, 0, 1); s.Oled = c.O; s.Material = c.Mat is "Mica" or "Acrylic" ? c.Mat : "None";
            s.Density = c.D is "Compact" or "Spacious" ? c.D : "Comfortable"; s.UiFont = Fonts().Contains(c.F) ? c.F : ""; s.TextScale = Math.Clamp(c.S, 0.8, 1.4); s.Softness = Math.Clamp(c.R, 4, 32);
            return true;
        }
        catch { return false; }
    }

    public static void Surprise()
    {
        var rnd = Random.Shared; var s = App.Settings;
        var t = Themes[rnd.Next(Themes.Length)];
        s.ThemePreset = t.Id;
        s.Accent = Hex(FromHsv(rnd.Next(360), 0.65 + rnd.NextDouble() * 0.3, 1.0));
        s.AccentCycle = false;
        s.BgTint = rnd.Next(3) == 0 ? 0 : Math.Round(rnd.NextDouble() * 0.6, 2);
        s.Softness = rnd.Next(6, 30);
    }

    public static void Reset()
    {
        var s = App.Settings;
        s.ThemePreset = "mochi"; s.Mode = "System"; s.Accent = "#F0456B"; s.AccentCycle = false; s.BgTint = 0; s.Oled = false; s.Material = "None";
        s.Density = "Comfortable"; s.UiFont = ""; s.TextScale = 1; s.Softness = 18; s.AutoDarkFrom = "19:00"; s.AutoDarkTo = "07:00";
    }
}
