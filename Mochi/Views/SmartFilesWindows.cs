using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

/// <summary>Search by meaning: type what you remember ("invoice from March", "goa trip photos") and find the file, offline.</summary>
public sealed class SmartSearchWindow : Window
{
    private static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    private readonly IReadOnlyList<string> _folders;
    private readonly List<IndexStore> _stores;
    private readonly TextBox _query = new() { FontSize = 16, CornerRadius = new CornerRadius(14), Padding = new Thickness(6, 11) };
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Width = 180, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private int _lastReady = -1;
    private string _lastQuery = "";

    public static Task<string?> RunAsync(string folder) => RunAsync(new[] { folder });

    public static Task<string?> RunAsync(IReadOnlyList<string> folders)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new SmartSearchWindow(folders);
        w.Closed += (_, _) => tcs.TrySetResult(null);
        w.Show();
        return tcs.Task;
    }

    public SmartSearchWindow(IReadOnlyList<string> folders)
    {
        _folders = folders.Select(f => f.TrimEnd('\\', '/')).ToList();
        _stores = _folders.Select(IndexStore.For).ToList();
        Title = L("x.ss.title", "Search by meaning") + " — " + (_folders.Count == 1 ? Path.GetFileName(_folders[0]) : string.Join(", ", _folders.Select(Path.GetFileName)));
        Width = 920; Height = 700; MinWidth = 640; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _query.Watermark = L("x.ss.hint", "Describe the file: invoice from March, resume, trip to Goa…");
        _query.InnerLeftContent = new TextBlock { Text = "⌕", FontSize = 20, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } };
        _query.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _debounce.Stop(); Search(); } };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Search(); };
        _tick.Tick += (_, _) => OnTick();

        var change = new Button { Content = L("x.ss.change", "Search another folder") };
        change.Click += async (_, _) =>
        {
            var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            if (d.FirstOrDefault()?.TryGetLocalPath() is { } p) { new SmartSearchWindow(new[] { p }).Show(); Close(); }
        };
        var path = new TextBlock { Text = string.Join("   ·   ", _folders), Classes = { "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel { Margin = new Thickness(16, 14, 16, 4) };
        DockPanel.SetDock(change, Dock.Right);
        head.Children.Add(change); head.Children.Add(path);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(16, 2, 16, 8), Children = { _bar, _status } };
        var top = new StackPanel { Spacing = 6, Children = { head, new Border { Margin = new Thickness(16, 0), Child = _query }, statusRow } };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(16, 0, 16, 16), Child = _results } });
        Content = root;

        Intro();
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } sc)
            {
                Width = Math.Min(Width, sc.WorkingArea.Width / sc.Scaling - 40); Height = Math.Min(Height, sc.WorkingArea.Height / sc.Scaling - 40);
                Position = new PixelPoint(sc.WorkingArea.X + (int)((sc.WorkingArea.Width - Width * sc.Scaling) / 2), sc.WorkingArea.Y + (int)((sc.WorkingArea.Height - Height * sc.Scaling) / 2));
            }
            _query.Focus();
            foreach (var s in _stores) _ = s.RefreshAsync();   // joins the background run if there is one; otherwise only looks for what changed
            _tick.Start(); OnTick();
        };
        Closed += (_, _) => _tick.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Intro()
    {
        _results.Children.Clear();
        _results.Children.Add(new TextBlock
        {
            Text = L("x.ss.intro", "Searches inside PDF, Word, Excel, PowerPoint and text files, and by the names of everything else (pictures, videos…). It works offline: nothing leaves your computer."),
            Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8),
        });
        var chips = new WrapPanel();
        foreach (var s in new[] { "invoice from March", "resume", "meeting notes", "bank statement", "photos of the trip" })
        {
            var b = new Button { Content = s, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 5), FontSize = 12 };
            b.Click += (_, _) => { _query.Text = s; _debounce.Stop(); Search(); };
            chips.Children.Add(b);
        }
        _results.Children.Add(chips);
    }

    /// <summary>Progress in plain words; the results are refreshed while the index grows.</summary>
    private void OnTick()
    {
        var total = _stores.Sum(s => s.Total); var ready = _stores.Sum(s => s.Ready);
        var busy = _stores.Any(s => s.Busy);
        _bar.IsVisible = busy && total > 0; _bar.Value = total == 0 ? 0 : ready / (double)total;
        if (busy)
        {
            var phase = _stores.FirstOrDefault(s => s.Busy)?.Phase;
            _status.Text = phase switch
            {
                "list" => L("x.ss.listing", "Looking through your folders…"),
                "read" => string.Format(L("x.ss.reading", "Reading {0} files…"), total),
                "meaning" => string.Format(L("x.ss.understanding", "Understanding files: {0} of {1}. You can search now, results get better as this fills."), ready, total),
                "deep" => string.Format(L("x.ss.deepening", "{0} files ready · reading long documents more closely in the background"), total),
                _ => "",
            };
        }
        else _status.Text = total == 0 ? L("x.ss.empty", "No files found in these folders.") : string.Format(L("x.ss.ready", "{0} files ready to search"), total);

        if (ready != _lastReady) { _lastReady = ready; if (!string.IsNullOrWhiteSpace(_query.Text) && busy) Search(); }
    }

    private async void Search()
    {
        var q = _query.Text?.Trim() ?? "";
        if (q.Length == 0) { _lastQuery = ""; Intro(); return; }
        _lastQuery = q;
        var hits = await Task.Run(() => IndexStore.SearchAll(_stores, q));
        if ((_query.Text?.Trim() ?? "") != q) return;   // typed on meanwhile
        _results.Children.Clear();
        var good = hits.Where(h => h.Score >= 0.24).ToList();
        if (good.Count == 0)
        {
            _results.Children.Add(new TextBlock { Text = L("x.ss.none", "No close match. Try other words, or describe what the file is about."), Classes = { "secondary" }, Margin = new Thickness(0, 8) });
            return;
        }
        foreach (var h in good.Take(25)) _results.Children.Add(Row(h));
    }

    private Control Row(Hit h)
    {
        var f = h.File;
        var (glyph, tint) = Look.Of(f.Kind);
        var strength = Math.Clamp((h.Score - 0.2) / 0.6, 0.08, 1);
        var dir = Path.GetDirectoryName(f.Path) ?? "";

        Button Small(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Classes = { "plain" }, FontSize = 15, Padding = new Thickness(9, 3) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => act();
            return b;
        }

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(f.Path), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = dir, Classes = { "secondary" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        if (h.Snippet.Length > 0) text.Children.Add(new TextBlock { Text = h.Snippet, Classes = { "secondary" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
        text.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(2), Width = 90 * strength, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.Parse(strength > 0.6 ? "#30D158" : strength > 0.3 ? "#FF9F0A" : "#8E8E93")), Margin = new Thickness(0, 2, 0, 0) });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            Small("↗", L("home.open_file", "Open file"), () => Open(f.Path)),
            Small("▭", L("home.open_folder", "Open folder"), () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{f.Path}\"")),
        } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(12), Background = tint, VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = glyph, FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(actions, 2); grid.Children.Add(actions);
        var card = new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.Parse("#1C1C1E")), Child = grid };
        card.DoubleTapped += (_, _) => Open(f.Path);
        ToolTip.SetTip(card, f.Path);
        return card;
    }

    private static void Open(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* no app for it */ }
    }
}

/// <summary>Tidy a messy folder: shows exactly what would move where, lets you untick a group, and can undo the whole thing.</summary>
public sealed class OrganizeWindow : Window
{
    private static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    private readonly string _folder;
    private readonly ComboBox _mode = new() { Width = 340 };
    private readonly StackPanel _groups = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Width = 120, IsVisible = false };
    private readonly Button _go = new() { Classes = { "pill" }, IsEnabled = false };
    private readonly Button _undo = new() { IsVisible = false };
    private List<PlannedMove> _plan = new();
    private readonly Dictionary<string, CheckBox> _boxes = new();

    public static Task<string?> RunAsync(string folder)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new OrganizeWindow(folder);
        w.Closed += (_, _) => tcs.TrySetResult(null);
        w.Show();
        return tcs.Task;
    }

    public OrganizeWindow(string folder)
    {
        _folder = folder.TrimEnd('\\', '/');
        Title = L("x.org.title", "Tidy a folder") + " — " + Path.GetFileName(_folder);
        Width = 860; Height = 700; MinWidth = 620; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _mode.ItemsSource = new[]
        {
            L("x.org.m1", "By kind: Pictures, Videos, PDFs, Documents…"),
            L("x.org.m2", "By kind, then by month"),
            L("x.org.m3", "Documents by topic (smart), the rest by kind"),
        };
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += async (_, _) => await ScanAsync();

        _go.Click += async (_, _) => await ApplyAsync();
        _undo.Content = L("x.org.undo", "Undo the last tidy-up");
        _undo.Click += (_, _) =>
        {
            var n = Organizer.Undo(_folder);
            _status.Text = string.Format(L("x.org.undone", "Put {0} files back."), n);
            _ = ScanAsync();
        };
        var close = new Button { Content = L("common.close", "Close") };
        close.Click += (_, _) => Close();

        var head = new StackPanel { Margin = new Thickness(16, 14, 16, 6), Spacing = 8, Children =
        {
            new TextBlock { Text = _folder, Classes = { "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = L("x.org.how", "Loose files in this folder go into sub-folders. Nothing is deleted or overwritten, folders already inside stay untouched, and you can undo."), TextWrapping = TextWrapping.Wrap, Classes = { "secondary" }, FontSize = 12 },
            _mode,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _busy, _status } },
        } };
        var foot = new DockPanel { Margin = new Thickness(16, 10) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { close, _go } };
        DockPanel.SetDock(right, Dock.Right);
        foot.Children.Add(right); foot.Children.Add(_undo);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(head); root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(16, 0, 16, 8), Child = _groups } });
        Content = root;

        Opened += async (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } sc)
            {
                Width = Math.Min(Width, sc.WorkingArea.Width / sc.Scaling - 40); Height = Math.Min(Height, sc.WorkingArea.Height / sc.Scaling - 40);
                Position = new PixelPoint(sc.WorkingArea.X + (int)((sc.WorkingArea.Width - Width * sc.Scaling) / 2), sc.WorkingArea.Y + (int)((sc.WorkingArea.Height - Height * sc.Scaling) / 2));
            }
            await ScanAsync();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private async Task ScanAsync()
    {
        _busy.IsVisible = true; _go.IsEnabled = false; _groups.Children.Clear(); _boxes.Clear();
        _status.Text = L("x.org.scanning", "Looking at the files…");
        var mode = (OrganizeMode)Math.Max(0, _mode.SelectedIndex);
        var status = new Progress<string>(s => _status.Text = s);
        string? error = null;
        await Task.Run(() => { try { _plan = Organizer.Plan(_folder, mode, status); } catch (Exception ex) { error = ex.Message; _plan = new(); } });
        _busy.IsVisible = false;
        _undo.IsVisible = Organizer.CanUndo(_folder);
        if (error is not null) { _status.Text = error; return; }
        if (_plan.Count == 0) { _status.Text = L("x.org.nothing", "Nothing to tidy: there are no loose files here."); UpdateGo(); return; }

        var groups = _plan.GroupBy(m => m.Group).OrderByDescending(g => g.Count()).ToList();
        _status.Text = string.Format(L("x.org.summary", "{0} files → {1} folders"), _plan.Count, groups.Count);
        foreach (var g in groups)
        {
            var box = new CheckBox { IsChecked = true };
            box.IsCheckedChanged += (_, _) => UpdateGo();
            _boxes[g.Key] = box;
            var kind = Organizer.KindFolder(g.First().Source);
            var (glyph, tint) = Look.Of(CategoryOf(kind));
            var names = new StackPanel { Spacing = 1, Margin = new Thickness(52, 4, 0, 0) };
            foreach (var m in g.Take(12)) names.Children.Add(new TextBlock { Text = Path.GetFileName(m.Source), FontSize = 12, Classes = { "secondary" }, TextTrimming = TextTrimming.CharacterEllipsis });
            if (g.Count() > 12) names.Children.Add(new TextBlock { Text = "+ " + (g.Count() - 12) + " " + L("x.org.more", "more"), FontSize = 12, Classes = { "secondary" } });
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 10 };
            head.Children.Add(box);
            var tile = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = tint, Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            Grid.SetColumn(tile, 1); head.Children.Add(tile);
            var title = new TextBlock { Text = g.Key.Replace('\\', '›'), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(title, 2); head.Children.Add(title);
            var count = new TextBlock { Text = g.Count() + " " + L("x.org.files", "files"), Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(count, 3); head.Children.Add(count);
            _groups.Children.Add(new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.Parse("#1C1C1E")), Child = new StackPanel { Children = { head, names } } });
        }
        UpdateGo();
    }

    private static string CategoryOf(string kindFolder) => kindFolder switch
    {
        "Pictures" => "Image", "Videos" => "Video", "Music & Audio" => "Audio", "PDFs" => "Pdf", "Documents" => "Document", "Spreadsheets" => "Spreadsheet",
        "Presentations" => "Presentation", "Archives" => "Archive", "Fonts" => "Font", "Design & 3D" => "Vector", "Subtitles" => "Subtitle", "E-books" => "Ebook",
        "Code & Data" => "Data", _ => "Text",
    };

    private IEnumerable<PlannedMove> Chosen => _plan.Where(m => _boxes.TryGetValue(m.Group, out var b) && b.IsChecked == true);

    private void UpdateGo()
    {
        var n = Chosen.Count();
        _go.IsEnabled = n > 0;
        _go.Content = n == 0 ? L("x.org.go0", "Tidy up") : string.Format(L("x.org.go", "Tidy up {0} files"), n);
    }

    private async Task ApplyAsync()
    {
        var picked = Chosen.ToList();
        _busy.IsVisible = true; _go.IsEnabled = false;
        var moved = await Task.Run(() => Organizer.Apply(picked, _folder));
        _busy.IsVisible = false;
        await ScanAsync();
        _status.Text = string.Format(L("x.org.done", "Moved {0} files. You can undo it below."), moved);
        _undo.IsVisible = Organizer.CanUndo(_folder);
    }
}
