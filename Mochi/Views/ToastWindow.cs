using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

/// <summary>
/// A small card in the corner that says how a conversion started from a toolbar went: a green tick (or a red mark), the file's name,
/// and what to do next. It slides in with a soft chime, stays while the pointer is over it, and a click shows the result.
/// </summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;

    public static void Notify(IReadOnlyList<RecentItem> items, string? note = null, long inBytes = 0)
    {
        var failed = items.Where(i => i.Failed).ToList();
        var bad = failed.Count > 0;
        var first = bad ? failed[0] : items[0];
        var title = bad ? Loc.TryGet("x.toast.failed") ?? "Couldn't convert" : Loc.TryGet("x.toast.done") ?? "Done";
        var name = first.Name + (!bad && items.Count > 1 ? $"  +{items.Count - 1}" : "");
        _current?.Close();
        // what the user cares about: how much smaller it got, and what to do next; a full disk says so plainly
        var detail = bad ? first.Detail : "";
        string? gain = null;
        if (bad && detail.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
        {
            title = Loc.TryGet("x.toast.disk_full") ?? "Your disk is full";
            detail = Loc.TryGet("x.toast.disk_full_hint") ?? "Free up some space on that drive, then try again.";
        }
        else if (!bad) gain = Gain(items, inBytes);
        _current = new ToastWindow(title, name, detail, note, bad, first.Failed || first.Path.Length == 0 ? null : first.Path, !bad ? first.Detail : null, gain);
        _current.Show();
        if (bad) Sounds.Fail(); else Sounds.Done();
    }

    private static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} MB" : b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} B";

    /// <summary>"2.4 MB → 186 KB, 92% smaller" when the result is smaller; just the new size otherwise.</summary>
    private static string? Gain(IReadOnlyList<RecentItem> items, long inBytes)
    {
        try
        {
            long outBytes = 0;
            foreach (var o in items.SelectMany(i => i.Outputs)) if (File.Exists(o)) outBytes += new FileInfo(o).Length;
            if (outBytes <= 0) return null;
            if (inBytes > outBytes && items.Count == 1 && items[0].Outputs.Count == 1)
                return $"{Size(inBytes)} → {Size(outBytes)}  ·  {(int)Math.Round(100.0 * (inBytes - outBytes) / inBytes)}% smaller";
            return Size(outBytes);
        }
        catch { return null; }
    }

    private static readonly string[] Happy = { "done", "cheer", "rocket", "happy", "convert", "cool", "hello" };
    private static int _lastHappy = -1;
    private static string HappyPick()
    {
        int i; do i = Random.Shared.Next(Happy.Length); while (i == _lastHappy && Happy.Length > 1);
        _lastHappy = i; return Happy[i];
    }

    private ToastWindow(string title, string name, string detail, string? note, bool failed, string? path, string? tool = null, string? gain = null)
    {
        const double W = 392;
        var p = Pal.Now();
        var accent = failed ? Color.Parse("#FF453A") : Color.Parse("#30D158");
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; CanResize = false;
        Width = W; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Mochi says how it went: a different happy sticker each time, or a puzzled one
        Control badge = new MascotView(failed ? "oops" : HappyPick(), 48, MascotMood.Still) { VerticalAlignment = VerticalAlignment.Center, Width = 56, Margin = new Thickness(0, 6, 0, 0) };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = p.TextBrush });
        text.Children.Add(new TextBlock { Text = name, FontSize = 12.5, Foreground = new SolidColorBrush(p.Text, 0.85), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        if (tool is { Length: > 0 }) text.Children.Add(new TextBlock { Text = tool, FontSize = 11.5, Foreground = p.SubBrush, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        if (gain is { Length: > 0 }) text.Children.Add(new TextBlock { Text = gain, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(accent), Margin = new Thickness(0, 3, 0, 0) });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = p.SubBrush, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        if (note is { Length: > 0 }) text.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#FFB020")), TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(0, 3, 0, 0) });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(badge);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        if (path is not null && !failed)
        {
            // the two things people do next: look at the result, or find it in its folder
            Button Pill(string label, bool primary, Action run)
            {
                var b = new Button { Content = label, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Padding = new Thickness(11, 3), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 8, 6, 0),
                    Background = primary ? new SolidColorBrush(ThemeEngine.CurrentAccent) : p.Chip(), Foreground = primary ? Brushes.White : p.TextBrush };
                b.Click += (_, e) => { run(); e.Handled = true; Close(); };
                return b;
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Pill(Loc.TryGet("x.toast.open") ?? "Open", true, () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }));
            actions.Children.Add(Pill(Loc.TryGet("x.toast.show") ?? "Show in folder", false, () => System.Diagnostics.Process.Start("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"")));
            text.Children.Add(actions);
        }

        Content = new Border
        {
            Margin = new Thickness(12), CornerRadius = new CornerRadius(18), Padding = new Thickness(15, 13),
            Background = p.CardBrush, BorderBrush = p.BorderBrush, BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 28, OffsetY = 8, Color = Color.FromArgb(p.Dark ? (byte)150 : (byte)70, 0, 0, 0) }),
            Child = row,
        };

        // bottom-right corner of the work area, once the real height is known; it rises into place while fading in
        PixelPoint rest = default; var placed = false;
        void Place()
        {
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } s) return;
            var h = Bounds.Height > 1 ? Bounds.Height : 100;
            rest = new PixelPoint(s.WorkingArea.Right - (int)((W + 4) * s.Scaling), s.WorkingArea.Bottom - (int)((h + 4) * s.Scaling));
            if (!placed) Position = rest;
            placed = true;
        }
        Opened += (_, _) =>
        {
            Place();
            Dispatcher.UIThread.Post(() => { Place(); if (!App.Settings.ReduceMotion) Rise(rest); else Position = rest; }, DispatcherPriority.Loaded);
        };

        var hover = false;
        PointerEntered += (_, _) => hover = true;
        PointerExited += (_, _) => hover = false;
        PointerPressed += (_, _) =>
        {
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
                System.Diagnostics.Process.Start("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"");
            Close();
        };

        var left = failed ? 9.0 : 8.0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (hover) return; left -= 0.1; if (left <= 0) { timer.Stop(); Close(); } };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); if (_current == this) _current = null; };
    }

    /// <summary>Fades in while sliding up about 18 px.</summary>
    private void Rise(PixelPoint rest)
    {
        var drop = (int)(18 * RenderScaling);
        Opacity = 0; Position = new PixelPoint(rest.X, rest.Y + drop);
        var frames = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(14) };
        t.Tick += (_, _) =>
        {
            frames++;
            var k = Math.Min(1.0, frames / 12.0);
            var ease = 1 - Math.Pow(1 - k, 3);
            Opacity = ease;
            Position = new PixelPoint(rest.X, rest.Y + (int)(drop * (1 - ease)));
            if (k >= 1) t.Stop();
        };
        t.Start();
        Closed += (_, _) => t.Stop();
    }
}
