using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

/// <summary>
/// Style 2, the Dock: one slim row of big icons with names under them, like a dock. Click or drop on an icon;
/// number keys 1–7 run the tools; typing a letter opens the Quick Panel already searching.
/// </summary>
public sealed class DockBarWindow : ToolbarBase
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 4 };

    public DockBarWindow(MainViewModel vm, IReadOnlyList<string>? files, bool activate) : base(vm, files, activate) { }

    protected override double Width0 => 560;

    protected override void BuildContent()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = Wrap(_row, 32, new Thickness(10, 8));
        KeyDown += (_, e) =>
        {
            if (!Activated) return;
            var tools = Suggested(7);
            if (e.Key is >= Key.D1 and <= Key.D7 && e.Key - Key.D1 < tools.Count) { _ = ClickedAsync(tools[e.Key - Key.D1]); e.Handled = true; }
            else if (e.Key is >= Key.A and <= Key.Z) { OpenPanel(e.Key.ToString().ToLowerInvariant()); e.Handled = true; }
        };
    }

    private void OpenPanel(string? search)
    {
        var vm = Vm; var files = HasKnownFiles ? KnownFiles : null;
        Close();
        new QuickPanelWindow(vm, files, true, search).Show();
    }

    protected override void Refresh()
    {
        _row.Children.Clear();
        _row.Children.Add(new Border { Padding = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top, Child = Files.Count > 0 ? Thumb(Files[0], 52) : Tile("＋", "#8E8E93", "#5C5C60", 52) });
        var n = 0;
        foreach (var t in Suggested(7))
        {
            var label = new StackPanel
            {
                Spacing = 4, Width = 70,
                Children =
                {
                    new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Icon(t, 52) },
                    new TextBlock { Text = t.Name, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 30 },
                },
            };
            if (Activated) label.Children.Add(new TextBlock { Text = (++n).ToString(), FontSize = 9, Opacity = 0.45, TextAlignment = TextAlignment.Center });
            var b = new Button { Content = label, Padding = new Thickness(4, 6), CornerRadius = new CornerRadius(16), Background = Brushes.Transparent };
            ToolTip.SetTip(b, t.Name + " " + t.Detail);
            Wire(b, t);
            _row.Children.Add(b);
        }
        var more = new Button
        {
            Padding = new Thickness(4, 6), CornerRadius = new CornerRadius(16), Background = Brushes.Transparent,
            Content = new StackPanel { Spacing = 4, Width = 70, Children = { new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Tile("⋯", "#8E8E93", "#5C5C60", 52) }, new TextBlock { Text = L("x.dock.more", "All tools"), FontSize = 11, TextAlignment = TextAlignment.Center } } },
        };
        more.Click += (_, _) => OpenPanel(null);
        _row.Children.Add(more);
    }
}

/// <summary>
/// Style 3, the Command Bar: a wide search bar at the top of the screen, like a launcher. Type, use the arrow keys, press Enter.
/// With nothing typed it lists your favourites and the tools that fit your files.
/// </summary>
public sealed class CommandBarWindow : ToolbarBase
{
    private readonly TextBox _search = new() { FontSize = 18, CornerRadius = new CornerRadius(16), Padding = new Thickness(6, 12), BorderThickness = new Thickness(0) };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private int _pick;

    public CommandBarWindow(MainViewModel vm, IReadOnlyList<string>? files, bool activate) : base(vm, files, activate) { }

    protected override double Width0 => 620;
    protected override bool CenterTop => true;

    protected override void BuildContent()
    {
        Width = 620; SizeToContent = SizeToContent.Height;
        _search.Watermark = L("x.cb.search", "What do you want to do? enhance, compress, to pdf…");
        _search.Background = P.Field; _search.Foreground = P.TextBrush;
        _search.InnerLeftContent = new TextBlock { Text = "⌕", FontSize = 22, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        _search.TextChanged += (_, _) => { _pick = 0; Refresh(); };
        _search.KeyDown += (_, e) =>
        {
            var hits = Current_Hits();
            if (e.Key is Key.Down or Key.Up) { _pick = Math.Clamp(_pick + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, hits.Count - 1)); Refresh(); e.Handled = true; }
            else if (e.Key == Key.Enter && hits.Count > 0) { _ = ClickedAsync(hits[Math.Clamp(_pick, 0, hits.Count - 1)]); e.Handled = true; }
            else if (e.Key == Key.Enter && CanAskAi(_search.Text ?? "") && All.First(t => t.Preset.Id == "ai-command") is { } ai) { RunAskAi((_search.Text ?? "").Trim(), ai); e.Handled = true; }
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        top.Children.Add(Thumbs);
        Grid.SetColumn(_search, 1); top.Children.Add(_search);
        Content = Wrap(new StackPanel { Spacing = 10, Children = { top, Headline, _list } }, 26, new Thickness(16));
        Headline.Margin = new Thickness(4, 0, 0, 0); Headline.FontSize = 11; Headline.FontWeight = FontWeight.Normal; Headline.Opacity = 0.6;
    }

    protected override void OnActivated() => _search.Focus();

    private List<Tool> Current_Hits()
    {
        var q = (_search.Text ?? "").Trim();
        return q.Length == 0 ? Suggested(8) : Search(q, 8);
    }

    protected override void Refresh()
    {
        var q = (_search.Text ?? "").Trim();
        Headline.Text = Files.Count == 0 ? L("x.cb.nofiles", "No files yet: drop files here, or pick a tool to choose files.") : (Files.Count == 1 ? Path.GetFileName(Files[0].TrimEnd('\\', '/')) : string.Format(L("x.qp.n_files", "{0} files"), Files.Count)) + "  ·  " + (q.Length == 0 ? L("x.qp.suggested", "Suggested for these files") : L("x.cb.results", "Results"));
        Sub.Text = "";
        _list.Children.Clear();
        var hits = Current_Hits();
        if (hits.Count == 0) { _list.Children.Add(new TextBlock { Text = L("x.qp.none", "No tool matches."), Opacity = 0.6, Margin = new Thickness(6, 8) }); if (AskAiRow(q) is { } none) _list.Children.Add(none); return; }
        for (var i = 0; i < hits.Count; i++)
        {
            var t = hits[i];
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
            grid.Children.Add(Icon(t, 36));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = t.Name, FontSize = 14, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = t.Detail, FontSize = 11, Opacity = 0.55 } } };
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            var hint = new TextBlock { Text = i == _pick ? "↵" : "", FontSize = 16, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(hint, 2); grid.Children.Add(hint);
            var star = PinStar(t); Grid.SetColumn(star, 3); grid.Children.Add(star);
            var row = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(14), Background = i == _pick ? P.Chip(0.1) : Brushes.Transparent };
            Wire(row, t);
            _list.Children.Add(row);
        }
        if (q.Length > 0 && AskAiRow(q) is { } ask) _list.Children.Add(ask);
    }
}

/// <summary>
/// Style 4, the Tile Pad: big colourful tiles with tabs for each kind of file. Nothing to read or type: see the picture, tap it.
/// </summary>
public sealed class TilePadWindow : ToolbarBase
{
    private readonly WrapPanel _grid = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly WrapPanel _tabs = new();
    private readonly TextBox _search = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(6, 8), FontSize = 13 };
    private string _tab = "*";   // "*" = suggested

    public TilePadWindow(MainViewModel vm, IReadOnlyList<string>? files, bool activate) : base(vm, files, activate) { }

    protected override double Width0 => 500;

    protected override void BuildContent()
    {
        Width = 500; SizeToContent = SizeToContent.Height; MaxHeight = 680;
        _search.Watermark = L("x.qp.search", "Search tools: enhance, pdf, compress…");
        _search.Background = P.Field; _search.Foreground = P.TextBrush;
        _search.InnerLeftContent = new TextBlock { Text = "⌕", FontSize = 16, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var q = (_search.Text ?? "").Trim();
            if (q.Length > 0 && Search(q, 1).FirstOrDefault() is { } t) { _ = ClickedAsync(t); e.Handled = true; }
        };
        var scroller = new ScrollViewer { Content = _grid, MaxHeight = 430, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Content = Wrap(new StackPanel { Spacing = 12, Children = { Header(), _search, _tabs, scroller } }, 26, new Thickness(16));
    }

    protected override void OnActivated() => _search.Focus();

    protected override void Refresh()
    {
        var q = (_search.Text ?? "").Trim();
        var usable = Usable();

        _tabs.Children.Clear();
        void Tab(string key, string label)
        {
            var on = q.Length == 0 && _tab == key;
            var b = new Button { Content = label, FontSize = 12, Padding = new Thickness(12, 4), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 6, 6), Background = on ? new SolidColorBrush(P.Accent) : P.Chip(), Foreground = on ? Brushes.White : P.TextBrush };
            b.Click += (_, _) => { _tab = key; _search.Text = ""; Refresh(); };
            _tabs.Children.Add(b);
        }
        Tab("*", Files.Count > 0 ? L("x.pad.for_you", "For your files") : L("x.qp.favourites", "Your favourites"));
        foreach (var g in usable.GroupBy(t => t.Category).OrderByDescending(g => g.Key == "Ai" ? int.MaxValue : g.Count()).Take(7)) Tab(g.Key, Loc.T("format.category." + g.Key));

        _grid.Children.Clear();
        var shown = q.Length > 0 ? Search(q, 24) : _tab == "*" ? Suggested(12) : usable.Where(t => t.Category == _tab).ToList();
        if (shown.Count == 0) _grid.Children.Add(new TextBlock { Text = L("x.qp.none", "No tool matches."), Opacity = 0.6, Margin = new Thickness(6, 12) });
        foreach (var t in shown)
        {
            var tile = new StackPanel
            {
                Spacing = 6, Width = 98,
                Children =
                {
                    new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Icon(t, 60) },
                    new TextBlock { Text = t.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, MinHeight = 30 },
                },
            };
            var b = new Button { Content = tile, Padding = new Thickness(4, 10), CornerRadius = new CornerRadius(18), Margin = new Thickness(2), Background = Brushes.Transparent };
            ToolTip.SetTip(b, t.Name + " " + t.Detail);
            Wire(b, t);
            _grid.Children.Add(b);
        }
    }
}
