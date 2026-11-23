using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

/// <summary>
/// Style 1, the Quick Panel: your files, a search box, the tools that fit them, and every other tool grouped by kind.
/// Click a tool to run it (or to choose files for it), drop files on a tool, or type and press Enter.
/// </summary>
public sealed class QuickPanelWindow : ToolbarBase
{
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBox _search = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(6, 9), FontSize = 14 };
    private readonly HashSet<string> _open = new();
    private readonly string? _initial;
    private int _pick;   // highlighted result while searching

    public QuickPanelWindow(MainViewModel vm, IReadOnlyList<string>? files, bool activate, string? initialSearch = null) : base(vm, files, activate) => _initial = initialSearch;

    protected override void BuildContent()
    {
        Width = 392; SizeToContent = SizeToContent.Height; MaxHeight = 640;
        _search.Watermark = L("x.qp.search", "Search tools: enhance, pdf, compress…");
        _search.Background = P.Field; _search.Foreground = P.TextBrush;
        _search.InnerLeftContent = new TextBlock { Text = "⌕", FontSize = 18, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        _search.TextChanged += (_, _) => { _pick = 0; Refresh(); };
        _search.KeyDown += OnSearchKey;

        var scroller = new ScrollViewer { Content = _body, MaxHeight = 440, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
        Content = Wrap(new StackPanel { Spacing = 12, Children = { Header(), _search, scroller } });
    }

    protected override void OnActivated()
    {
        if (_initial is { Length: > 0 }) { _search.Text = _initial; _search.CaretIndex = _initial.Length; }
        _search.Focus();
    }

    protected override void Refresh()
    {
        _body.Children.Clear();
        var q = (_search.Text ?? "").Trim();
        var usable = Usable();

        if (q.Length > 0)
        {
            var hits = Search(q, 14);
            _pick = Math.Clamp(_pick, 0, Math.Max(0, hits.Count - 1));
            if (hits.Count == 0) _body.Children.Add(new TextBlock { Text = Files.Count > 0 ? L("x.qp.none_fit", "Nothing that fits these files matches. Try other words.") : L("x.qp.none", "No tool matches."), Opacity = 0.65, Margin = new Thickness(4, 8), TextWrapping = TextWrapping.Wrap });
            for (var i = 0; i < hits.Count; i++) _body.Children.Add(Row(hits[i], i == _pick));
            if (AskAiRow(q) is { } ask) _body.Children.Add(ask);
            return;
        }

        var aiTools = AiTools();
        if (aiTools.Count > 0)
        {
            _body.Children.Add(Heading("✦ AI"));
            var aiGrid = new WrapPanel();
            foreach (var t in aiTools) aiGrid.Children.Add(Chip(t));
            _body.Children.Add(aiGrid);
        }

        var suggested = Suggested(8).Where(t => !aiTools.Contains(t)).ToList();
        if (suggested.Count > 0)
        {
            _body.Children.Add(Heading(Files.Count > 0 ? L("x.qp.suggested", "Suggested for these files") : L("x.qp.favourites", "Your favourites")));
            var grid = new WrapPanel();
            foreach (var t in suggested) grid.Children.Add(Chip(t));
            _body.Children.Add(grid);
        }

        _body.Children.Add(Heading(L("x.qp.all", "All tools")));
        foreach (var g in usable.GroupBy(t => t.Category).OrderByDescending(g => g.Key == "Ai" ? int.MaxValue : g.Count()))
        {
            var key = g.Key; var open = _open.Contains(key);
            var (glyph, tint) = Look.Of(key);
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
            line.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = tint, Child = new TextBlock { Text = glyph, FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var title = new TextBlock { Text = Loc.T("format.category." + key), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold }; Grid.SetColumn(title, 1); line.Children.Add(title);
            var count = new TextBlock { Text = g.Count() + (open ? "  ▴" : "  ▾"), Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(count, 2); line.Children.Add(count);
            var head = new Button { Classes = { "plain" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 6), Content = line };
            head.Click += (_, _) => { if (!_open.Remove(key)) _open.Add(key); Refresh(); };
            _body.Children.Add(head);
            if (open) foreach (var t in g) _body.Children.Add(Row(t, false));
        }
    }

    private Control Heading(string text) => new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.Bold, Opacity = 0.5, Margin = new Thickness(4, 2, 0, 0) };

    private Control Chip(Tool t)
    {
        var b = new Button
        {
            Width = 160, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 8), HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(14), Background = P.Chip(),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { Icon(t, 32), new TextBlock { Text = t.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 100, MaxLines = 2 } },
            },
        };
        Wire(b, t);
        return b;
    }

    private Control Row(Tool t, bool highlighted)
    {
        var pinned = Toolbar.Pins().Contains(t.Preset.Id);
        var star = new Button { Classes = { "plain" }, Content = pinned ? "★" : "☆", FontSize = 15, Padding = new Thickness(8, 2), Foreground = new SolidColorBrush(Color.Parse(pinned ? "#FFB800" : "#8E8E93")) };
        ToolTip.SetTip(star, pinned ? L("x.qp.unpin", "Unpin") : L("x.qp.pin", "Pin to the top"));
        star.Click += (_, e) =>
        {
            var pins = Toolbar.Pins();
            if (!pins.Remove(t.Preset.Id)) pins.Add(t.Preset.Id);
            App.Settings.Save(); Refresh(); e.Handled = true;
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(Icon(t, 30));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = t.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, new TextBlock { Text = t.Detail, FontSize = 11, Opacity = 0.55 } } };
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(star, 2); grid.Children.Add(star);
        var row = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 5, 2, 5), CornerRadius = new CornerRadius(12), Background = P.Chip(highlighted ? 0.1 : -0.02), Margin = new Thickness(0, 0, 0, 2) };
        Wire(row, t);
        return row;
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        var q = (_search.Text ?? "").Trim();
        if (q.Length == 0) return;
        if (e.Key is Key.Down or Key.Up) { _pick += e.Key == Key.Down ? 1 : -1; Refresh(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            var hits = Search(q, 14);
            if (hits.Count > 0) _ = ClickedAsync(hits[Math.Clamp(_pick, 0, hits.Count - 1)]);
            else if (CanAskAi(q) && All.First(t => t.Preset.Id == "ai-command") is { } ai) RunAskAi(q, ai);
            e.Handled = true;
        }
    }
}
