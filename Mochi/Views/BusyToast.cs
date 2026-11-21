using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>
/// A small "working" pill in the corner while something started from a toolbar is running: the AI orb, what is happening, and a seconds counter.
/// It goes away by itself when the job ends (the result toast takes its place).
/// </summary>
public sealed class BusyToast : Window
{
    private static BusyToast? _current;
    private static readonly string[] InteractiveModes = { "annotate", "perspective", "removebg", "erase", "dupes", "smartsearch", "organize", "pdfedit", "sheetstudio" };

    /// <summary>Tools that open their own window for a while: no "working" pill for them.</summary>
    public static bool IsInteractive(Preset p) => InteractiveModes.Any(m => p.Mode.StartsWith(m));

    /// <summary>Shows the pill; call <see cref="End"/> when done. AI tools end it themselves as soon as their window appears.</summary>
    public static void Begin(Preset p, int fileCount, bool onlineFiles = false)
    {
        if (IsInteractive(p)) return;
        End();
        var text = p.Category == "Ai" ? Loc.TryGet("x.busy.ai") ?? "Opening AI…" : Loc.T("x.converting", fileCount, p.DisplayName);
        if (onlineFiles) text += "  (" + (Loc.TryGet("x.busy.cloud") ?? "downloading from OneDrive first") + ")";
        _current = new BusyToast(text);
        _current.Show();
        if (p.Category == "Ai") AiShell.FirstWindowOpened += EndOnce;
    }

    private static void EndOnce() { AiShell.FirstWindowOpened -= EndOnce; End(); }

    public static void End()
    {
        AiShell.FirstWindowOpened -= EndOnce;
        var t = _current; _current = null;
        if (t is not null) Dispatcher.UIThread.Post(t.Close);
    }

    private BusyToast(string text)
    {
        const double W = 340, H = 64;
        var dark = Pal.Now().Dark;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; CanResize = false;
        Width = W; Height = H;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        var secs = new TextBlock { Text = "", FontSize = 11, Foreground = new SolidColorBrush(Colors.White, 0.6) };
        Content = new Border
        {
            Margin = new Thickness(10), CornerRadius = new CornerRadius(18), Padding = new Thickness(12, 0),
            Background = new SolidColorBrush(Color.Parse(dark ? "#2C2C2E" : "#3A3A3C")),
            BorderBrush = new SolidColorBrush(Colors.White, 0.12), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 6, Color = Color.FromArgb(120, 0, 0, 0) }),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12,
                Children =
                {
                    new MascotView("laptop", 36, MascotMood.Float) { Width = 44, VerticalAlignment = VerticalAlignment.Center },
                    Col(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { label, secs } }, 1),
                },
            },
        };
        Opened += (_, _) =>
        {
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } s)
                Position = new PixelPoint(s.WorkingArea.Right - (int)((W + 8) * s.Scaling), s.WorkingArea.Bottom - (int)((H + 8) * s.Scaling));
        };
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { var n = (int)(DateTime.UtcNow - start).TotalSeconds; if (n >= 3) secs.Text = n + " s" + (n >= 20 ? "  ·  " + (Loc.TryGet("x.busy.long") ?? "big or online-only files take longer") : ""); };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); if (_current == this) _current = null; };
    }

    private static Control Col(Control c, int col) { Grid.SetColumn(c, col); return c; }
}
