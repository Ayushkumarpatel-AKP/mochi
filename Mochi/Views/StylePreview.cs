using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

/// <summary>
/// A small picture of the chosen toolbar style, filled with the presets of the donut being edited, for the Presets page.
/// It only draws; the real toolbars (ToolbarStyles.cs, QuickPanelWindow.cs) are what open on the shortcut.
/// </summary>
public static class StylePreview
{
    private sealed record Item(string Name, string Detail, string Glyph, IBrush Tint);

    public static Control Build(string style, IEnumerable<Preset> presets)
    {
        var p = Pal.Now();
        var items = presets.Select(x =>
        {
            var (glyph, tint) = Look.Of(x.Category);
            return new Item(x.DisplayName, x.Extension.Length == 0 ? Loc.T("format.category." + x.Category) : "→ " + x.Extension, glyph, tint);
        }).ToList();
        if (items.Count == 0)
            return new TextBlock { Text = Loc.TryGet("x.preview_empty") ?? "No presets in this donut yet. Switch some on below to see them here.", Foreground = p.SubBrush, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };

        Control body = style switch
        {
            "dock" => Dock(items, p),
            "command" => Command(items, p),
            "pad" => Pad(items, p),
            _ => Panel(items, p),
        };
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new ContentControl { Foreground = p.TextBrush, Content = body },
        };
    }

    private static Border Card(Control inner, double radius, Thickness pad, double width = double.NaN) => new()
    {
        Width = width, CornerRadius = new CornerRadius(radius), Padding = pad,
        Background = Pal.Now().CardBrush, BorderBrush = Pal.Now().BorderBrush, BorderThickness = new Thickness(1),
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 28, OffsetY = 8, Color = Color.FromArgb(70, 0, 0, 0) }),
        Child = inner,
    };

    private static Control Icon(Item t, double size) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Background = t.Tint,
        Child = new TextBlock { Text = t.Glyph, FontSize = size * 0.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private static Control Field(string hint, Pal p) => new Border
    {
        CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9), Background = p.Field, BorderBrush = p.BorderBrush, BorderThickness = new Thickness(1),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = "⌕", Foreground = p.SubBrush }, new TextBlock { Text = hint, Foreground = p.SubBrush, FontSize = 13 } },
        },
    };

    private static Control Row(Item t, Pal p, double icon = 34) => new Border
    {
        CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 6), Background = p.Chip(),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                Icon(t, icon),
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = t.Name, FontSize = 13, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = t.Detail, FontSize = 11, Foreground = p.SubBrush } } },
            },
        },
    };

    private static Control More(int count, Pal p) => new TextBlock { Text = $"+{count}", FontSize = 11, Foreground = p.SubBrush, HorizontalAlignment = HorizontalAlignment.Center };

    private static Control Panel(List<Item> items, Pal p)
    {
        var s = new StackPanel { Spacing = 8 };
        s.Children.Add(Field(Loc.TryGet("x.qp.search") ?? "Search tools", p));
        s.Children.Add(new TextBlock { Text = Loc.TryGet("x.qp.suggested") ?? "Suggested", FontSize = 11, Foreground = p.SubBrush, Margin = new Thickness(4, 4, 0, 0) });
        foreach (var t in items.Take(5)) s.Children.Add(Row(t, p));
        if (items.Count > 5) s.Children.Add(More(items.Count - 5, p));
        return Card(s, 22, new Thickness(14), 380);
    }

    private static Control Command(List<Item> items, Pal p)
    {
        var s = new StackPanel { Spacing = 8 };
        s.Children.Add(Field(Loc.TryGet("x.cb.hint") ?? "Type what you want, press Enter", p));
        foreach (var t in items.Take(5)) s.Children.Add(Row(t, p, 28));
        if (items.Count > 5) s.Children.Add(More(items.Count - 5, p));
        return Card(s, 18, new Thickness(12), 440);
    }

    private static Control Dock(List<Item> items, Pal p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var t in items.Take(7))
            row.Children.Add(new StackPanel
            {
                Spacing = 4, Width = 70, Margin = new Thickness(0, 4),
                Children =
                {
                    new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Icon(t, 52) },
                    new TextBlock { Text = t.Name, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 30 },
                },
            });
        return Card(row, 32, new Thickness(12, 8));
    }

    private static Control Pad(List<Item> items, Pal p)
    {
        var wrap = new WrapPanel { Width = 372 };
        foreach (var t in items.Take(9))
            wrap.Children.Add(new Border
            {
                Width = 116, Height = 84, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(16), Background = t.Tint, Padding = new Thickness(10),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Bottom, Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = t.Glyph, FontSize = 20, Foreground = Brushes.White },
                        new TextBlock { Text = t.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            });
        return Card(wrap, 22, new Thickness(14, 14, 8, 8));
    }
}
