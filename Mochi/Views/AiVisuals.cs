using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Mochi.Views;

/// <summary>A control that redraws itself about 60 times a second while it is on screen.</summary>
public abstract class AnimatedControl : Control
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    protected AnimatedControl() => _timer.Tick += (_, _) => InvalidateVisual();

    protected double Time => _clock.Elapsed.TotalSeconds;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _timer.Stop(); base.OnDetachedFromVisualTree(e); }
}

/// <summary>
/// The "AI is working" light: three soft colour blobs orbit a bright core, with a few sparkles around it.
/// Thinking = lively, Calm = a slow breath (nothing running).
/// </summary>
public sealed class AiOrb : AnimatedControl
{
    private static RadialGradientBrush Blob(byte r, byte g, byte b) => new()
    {
        GradientStops = { new GradientStop(Color.FromArgb(235, r, g, b), 0), new GradientStop(Color.FromArgb(110, r, g, b), 0.5), new GradientStop(Color.FromArgb(0, r, g, b), 1) },
    };

    private static readonly IBrush[] Blobs = { Blob(255, 77, 141), Blob(122, 92, 255), Blob(46, 196, 255) };
    private static readonly IBrush Core = new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.FromArgb(255, 255, 255, 255), 0), new GradientStop(Color.FromArgb(200, 255, 255, 255), 0.45), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
    };

    private double _energy;   // eases toward 1 while thinking, toward 0 when calm

    public bool Calm { get; set; }

    public override void Render(DrawingContext ctx)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 4) return;
        _energy += ((Calm ? 0.0 : 1.0) - _energy) * 0.05;
        var t = Time * (0.55 + _energy * 1.1);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = size / 2;

        for (var i = 0; i < Blobs.Length; i++)
        {
            var a = t * (0.9 + 0.33 * i) + i * 2.1;
            var reach = r * (0.14 + 0.16 * _energy) * (1 + 0.25 * Math.Sin(t * 1.3 + i));
            var p = new Point(c.X + Math.Cos(a) * reach, c.Y + Math.Sin(a * 1.15) * reach);
            var rad = r * (0.66 + 0.07 * Math.Sin(t * 2 + i * 1.7));
            ctx.DrawEllipse(Blobs[i], null, p, rad, rad);
        }

        var pulse = 0.34 + 0.05 * Math.Sin(Time * (1.6 + _energy * 2.4));
        ctx.DrawEllipse(Core, null, c, r * pulse * 1.6, r * pulse * 1.6);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White, 0.35 + 0.2 * _energy), Math.Max(1, r * 0.03)), c, r * (0.52 + 0.04 * Math.Sin(Time * 1.1)), r * (0.52 + 0.04 * Math.Sin(Time * 1.1)));

        // sparkles
        for (var i = 0; i < 5; i++)
        {
            var a = Time * (0.5 + 0.13 * i) * (i % 2 == 0 ? 1 : -1) + i * 1.26;
            var d = r * (0.62 + 0.18 * Math.Sin(Time * 0.9 + i));
            var tw = 0.5 + 0.5 * Math.Sin(Time * 3.1 + i * 2.3);
            ctx.DrawEllipse(new SolidColorBrush(Colors.White, 0.25 + 0.7 * tw * (0.35 + _energy * 0.65)), null,
                new Point(c.X + Math.Cos(a) * d, c.Y + Math.Sin(a) * d), Math.Max(0.8, r * 0.03 * (0.6 + tw)), Math.Max(0.8, r * 0.03 * (0.6 + tw)));
        }
    }
}

/// <summary>Placeholder lines with a light sweeping across them, shown while the first words of an answer are on their way.</summary>
public sealed class AiShimmer : AnimatedControl
{
    private static readonly double[] Widths = { 0.96, 0.82, 0.9, 0.58 };

    public Color Ink { get; set; } = Colors.White;

    private readonly int _rows;

    public AiShimmer(int rows = 4) { _rows = Math.Clamp(rows, 1, Widths.Length); Height = _rows * 24; }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        if (w < 10) return;
        var x = ((Time * 0.55) % 1.6 - 0.3) * w;   // the sweep crosses the lines, then rests off to the right
        var sweep = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(x - w * 0.25, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(x + w * 0.25, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromArgb(0, Ink.R, Ink.G, Ink.B), 0), new GradientStop(Color.FromArgb(70, Ink.R, Ink.G, Ink.B), 0.5), new GradientStop(Color.FromArgb(0, Ink.R, Ink.G, Ink.B), 1) },
        };
        var baseBrush = new SolidColorBrush(Ink, 0.09);
        for (var i = 0; i < _rows; i++)
        {
            var rect = new Rect(0, i * 24 + 4, w * Widths[i], 12);
            ctx.DrawRectangle(baseBrush, null, rect, 6, 6);
            ctx.DrawRectangle(sweep, null, rect, 6, 6);
        }
    }
}

/// <summary>A small, dependency-free Markdown renderer for AI answers: headings, bullets, numbers, quotes, code, tables, **bold**, *italic*, `code`.</summary>
public static class Md
{
    private static readonly Regex Inline = new(@"(\*\*[^*\n]+\*\*|__[^_\n]+__|`[^`\n]+`|(?<![\*\w])\*[^*\n]+\*(?![\*\w]))", RegexOptions.Compiled);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    public static Control Render(string text, Pal p)
    {
        var panel = new StackPanel { Spacing = 6 };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0) continue;

            if (t.StartsWith("```"))   // a code block runs until the closing fence (or the end of what has arrived so far)
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) code.Add(lines[i]);
                panel.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Background = new SolidColorBrush(p.Text, 0.07),
                    Child = new SelectableTextBlock { Text = string.Join("\n", code), FontFamily = Mono, FontSize = 12.5, Foreground = p.TextBrush, TextWrapping = TextWrapping.Wrap },
                });
                continue;
            }
            if (t.StartsWith('|'))   // a table: gather its rows
            {
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))) continue;
                    rows.Add(cells);
                }
                i--;
                if (rows.Count > 0) panel.Children.Add(Table(rows, p));
                continue;
            }

            var h = Regex.Match(t, @"^(#{1,4})\s+(.*)$");
            if (h.Success)
            {
                var level = h.Groups[1].Length;
                panel.Children.Add(Text(h.Groups[2].Value, p, level == 1 ? 19 : level == 2 ? 16.5 : 15, FontWeight.Bold, new Thickness(0, level == 1 ? 6 : 4, 0, 0)));
                continue;
            }
            var b = Regex.Match(t, @"^[-*•]\s+(.*)$");
            if (b.Success) { panel.Children.Add(Item("•", b.Groups[1].Value, p, line.Length - line.TrimStart().Length)); continue; }
            var n = Regex.Match(t, @"^(\d{1,2})[.)]\s+(.*)$");
            if (n.Success) { panel.Children.Add(Item(n.Groups[1].Value + ".", n.Groups[2].Value, p, line.Length - line.TrimStart().Length)); continue; }
            if (t.StartsWith('>'))
            {
                panel.Children.Add(new Border
                {
                    BorderBrush = new SolidColorBrush(p.Accent), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2),
                    Child = Text(t.TrimStart('>', ' '), p, 14, FontWeight.Normal, default, 0.8),
                });
                continue;
            }
            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,})$")) { panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(p.Text, 0.12), Margin = new Thickness(0, 6) }); continue; }
            panel.Children.Add(Text(t, p, 14.5, FontWeight.Normal, default));
        }
        return panel;
    }

    private static Control Item(string mark, string text, Pal p, int indent) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(Math.Min(indent, 8) * 4 + 2, 0, 0, 0),
        Children =
        {
            new TextBlock { Text = mark, FontSize = 14.5, Foreground = new SolidColorBrush(p.Accent), FontWeight = FontWeight.Bold, MinWidth = 14, VerticalAlignment = VerticalAlignment.Top },
            Place(Text(text, p, 14.5, FontWeight.Normal, default), 1),
        },
    };

    private static Control Place(Control c, int col) { Grid.SetColumn(c, col); return c; }

    private static Control Table(List<string[]> rows, Pal p)
    {
        var cols = rows.Max(r => r.Length);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", cols - 1).Append("*"))), Margin = new Thickness(0, 2) };
        for (var r = 0; r < rows.Count; r++)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < cols; c++)
            {
                var cell = new Border
                {
                    Padding = new Thickness(10, 6), Background = new SolidColorBrush(p.Text, r == 0 ? 0.10 : r % 2 == 0 ? 0.04 : 0.0),
                    Child = Text(c < rows[r].Length ? rows[r][c] : "", p, 13, r == 0 ? FontWeight.Bold : FontWeight.Normal, default),
                };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c); g.Children.Add(cell);
            }
        }
        return new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, BorderBrush = new SolidColorBrush(p.Text, 0.12), BorderThickness = new Thickness(1), Child = g };
    }

    private static SelectableTextBlock Text(string s, Pal p, double size, FontWeight weight, Thickness margin, double alpha = 1)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = size, FontWeight = weight, Foreground = new SolidColorBrush(p.Text, alpha), TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = size * 1.5,
        };
        var pos = 0;
        foreach (Match m in Inline.Matches(s))
        {
            if (m.Index > pos) tb.Inlines!.Add(new Run(s[pos..m.Index]));
            var v = m.Value;
            if (v.StartsWith('`')) tb.Inlines!.Add(new Run(v.Trim('`')) { FontFamily = Mono, Background = new SolidColorBrush(p.Text, 0.1) });
            else if (v.StartsWith("**") || v.StartsWith("__")) tb.Inlines!.Add(new Run(v[2..^2]) { FontWeight = FontWeight.Bold });
            else tb.Inlines!.Add(new Run(v.Trim('*')) { FontStyle = FontStyle.Italic });
            pos = m.Index + m.Length;
        }
        if (pos < s.Length) tb.Inlines!.Add(new Run(s[pos..]));
        return tb;
    }
}
