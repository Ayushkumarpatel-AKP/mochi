using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

/// <summary>
/// Smart rename: the AI looks at what each file is (its words, or its picture) and proposes a clear name.
/// Proposals appear row by row, can be edited or unticked, nothing changes until "Rename", and one click undoes it.
/// </summary>
public sealed class AiRenameWindow : AiShell
{
    private sealed class Row
    {
        public required string Path { get; init; }
        public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);
        public string Ext => System.IO.Path.GetExtension(Path);
        public CheckBox Check { get; } = new() { VerticalAlignment = VerticalAlignment.Center, IsEnabled = false };
        public TextBox Box { get; } = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, Padding = new Thickness(8, 5), IsVisible = false };
        public Control Wait { get; } = new AiShimmer(1) { Height = 24 };
        public Border Card { get; } = new() { Opacity = 0 };
        public string Proposed { get; set; } = "";
    }

    private static readonly string[] StyleNames = { "Natural words", "kebab-case", "snake_case", "Title Case" };
    private static readonly string[] StyleRules =
    {
        "plain words with spaces, capital letters only where natural (e.g. Electricity bill Oct 2026)",
        "all lowercase, words joined by hyphens (e.g. electricity-bill-oct-2026)",
        "all lowercase, words joined by underscores (e.g. electricity_bill_oct_2026)",
        "Title Case With Spaces (e.g. Electricity Bill Oct 2026)",
    };

    private readonly List<Row> _rows = new();
    private readonly StackPanel _list = new() { Spacing = 8, Margin = new Thickness(22, 4, 22, 12) };
    private readonly ComboBox _style = new() { ItemsSource = StyleNames, SelectedIndex = 0, Width = 170 };
    private readonly Button _again = new() { Padding = new Thickness(14, 7), CornerRadius = new CornerRadius(14) };
    private readonly CheckBox _all = new() { IsChecked = true, IsEnabled = false };
    private readonly TextBlock _count = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _apply = new() { Classes = { "pill" }, Padding = new Thickness(24, 10), IsEnabled = false };
    private readonly Button _cancel = new() { Padding = new Thickness(18, 10) };
    private readonly TextBlock _result = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _undo = new() { Padding = new Thickness(14, 8), IsVisible = false };
    private readonly List<(string Old, string New)> _done = new();
    private readonly TaskCompletionSource<IReadOnlyList<string>> _tcs = new();
    private readonly string[] _paths;
    private int _skipped;
    private bool _working;

    // renaming changes files in place and is undone in this window, so nothing is reported to the result history
    public static Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> files)
    {
        var w = new AiRenameWindow(files);
        w.Show();
        _ = w.StartAsync();
        return w._tcs.Task;
    }

    private AiRenameWindow(IReadOnlyList<string> files) : base(L("x.ai.t_rename", "Smart rename"), 760, 700)
    {
        _paths = files.Where(File.Exists).ToArray();
        _skipped = files.Count - _paths.Length;
        Closed += (_, _) => _tcs.TrySetResult(Array.Empty<string>());
        SetSub(string.Format(L("x.ai.n_files", "{0} files"), _paths.Length));

        _again.Content = "↻  " + L("x.ai.regen", "Suggest again");
        _again.IsEnabled = false;
        _again.Click += async (_, _) => await ProposeAsync();
        _style.SelectionChanged += (_, _) => { if (_rows.Count > 0 && !_working && _done.Count == 0) _ = ProposeAsync(); };
        _all.Click += (_, _) => { foreach (var r in _rows.Where(r => r.Check.IsEnabled)) r.Check.IsChecked = _all.IsChecked; Recount(); };
        _apply.Click += async (_, _) => await ApplyAsync();
        _cancel.Content = L("common.close", "Close");
        _cancel.Click += (_, _) => Close();
        _undo.Content = "↶  " + L("x.ai.undo", "Undo");
        _undo.Click += (_, _) => UndoAll();

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(22, 0, 22, 8) };
        bar.Children.Add(_all);
        var style = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = L("x.ai.style", "Style"), VerticalAlignment = VerticalAlignment.Center, Foreground = P.SubBrush, FontSize = 12.5 }, _style } };
        Grid.SetColumn(style, 1); bar.Children.Add(style);
        Grid.SetColumn(_count, 2); _count.Foreground = P.SubBrush; bar.Children.Add(_count);
        Grid.SetColumn(_again, 3); bar.Children.Add(_again);

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(22, 8, 22, 18) };
        foot.Children.Add(_result);
        Grid.SetColumn(_undo, 1); foot.Children.Add(_undo);
        Grid.SetColumn(_cancel, 2); foot.Children.Add(_cancel);
        Grid.SetColumn(_apply, 3); foot.Children.Add(_apply);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = _list });
        Body.Content = root;
    }

    private async Task StartAsync()
    {
        if (_paths.Length == 0) { _result.Text = L("x.ai.rename_none", "Smart rename works on files, not folders."); _all.IsVisible = false; _style.IsEnabled = false; return; }
        if (!await GateAsync()) return;
        foreach (var path in _paths) _list.Children.Add(MakeRow(path));
        await ProposeAsync();
    }

    private Control MakeRow(string path)
    {
        var r = new Row { Path = path };
        _rows.Add(r);
        var (glyph, tint) = Look.Of(Directory.Exists(path) ? "Archive" : FileIndex.KindOf(r.Ext.ToLowerInvariant()));
        var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Background = tint, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var ext = new TextBlock { Text = r.Ext, Foreground = P.SubBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 };
        var edit = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        edit.Children.Add(r.Box); Grid.SetColumn(ext, 1); edit.Children.Add(ext);
        var newLine = new Grid { Children = { r.Wait, edit } };
        var text = new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = Path.GetFileName(path), FontSize = 12, Foreground = P.SubBrush, TextTrimming = TextTrimming.CharacterEllipsis }, newLine },
        };
        r.Check.IsCheckedChanged += (_, _) => Recount();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 12 };
        g.Children.Add(r.Check);
        Grid.SetColumn(icon, 1); g.Children.Add(icon);
        Grid.SetColumn(text, 2); g.Children.Add(text);
        r.Card.Child = g;
        r.Card.CornerRadius = new CornerRadius(14); r.Card.Padding = new Thickness(14, 10);
        r.Card.Background = new SolidColorBrush(P.Text, 0.05);
        r.Card.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(320) } };
        DispatcherTimer.RunOnce(() => r.Card.Opacity = 1, TimeSpan.FromMilliseconds(30 * Math.Min(_rows.Count, 12)));
        return r.Card;
    }

    private void Recount()
    {
        var n = _rows.Count(r => r.Check.IsChecked == true && r.Check.IsEnabled);
        _count.Text = _done.Count > 0 ? "" : string.Format(L("x.ai.n_selected", "{0} of {1} selected"), n, _rows.Count) + (_skipped > 0 ? string.Format(L("x.ai.n_skipped", "  ·  {0} folders skipped"), _skipped) : "");
        _apply.Content = n == 1 ? L("x.ai.rename_one", "Rename 1 file") : string.Format(L("x.ai.rename_n", "Rename {0} files"), n);
        _apply.IsEnabled = n > 0 && !_working && _done.Count == 0;
    }

    // ---------------------------------------------------------------- asking the AI, a few files at a time

    private async Task ProposeAsync()
    {
        if (_working) return;
        _working = true; Orb.Calm = false;
        _again.IsEnabled = false; _apply.IsEnabled = false; _style.IsEnabled = false; _all.IsEnabled = false;
        _result.Text = "";
        _result.Foreground = P.SubBrush;
        Cts = new CancellationTokenSource();
        var ct = Cts.Token;
        foreach (var r in _rows) { r.Check.IsEnabled = false; r.Box.IsVisible = false; r.Wait.IsVisible = true; }

        var si = Math.Max(0, _style.SelectedIndex);
        string? error = null; var errSetup = false;
        var finished = 0;
        try
        {
            foreach (var batch in _rows.Chunk(6))
            {
                _result.Text = string.Format(L("x.ai.naming", "Reading {0} of {1}…"), Math.Min(finished + batch.Length, _rows.Count), _rows.Count);
                var names = await NameBatchAsync(batch, si, ct);
                foreach (var (row, i) in batch.Select((r, i) => (r, i)))
                {
                    var name = names.TryGetValue(i, out var n) ? n : row.Stem;
                    name = Clean(name, row.Stem, si);
                    row.Proposed = name;
                    row.Box.Text = name;
                    row.Wait.IsVisible = false; row.Box.IsVisible = true;
                    row.Check.IsEnabled = true;
                    row.Check.IsChecked = !string.Equals(name, row.Stem, StringComparison.Ordinal);
                }
                finished += batch.Length;
                Recount();
            }
        }
        catch (OperationCanceledException) { }
        catch (AiException ex) { error = ex.Message; errSetup = ex.NeedsSetup; }
        catch (Exception ex) { error = ex.Message; }

        _working = false; Orb.Calm = true;
        _again.IsEnabled = true; _style.IsEnabled = true; _all.IsEnabled = _rows.Any(r => r.Check.IsEnabled);
        foreach (var r in _rows.Where(r => !r.Check.IsEnabled)) { r.Wait.IsVisible = false; r.Box.Text = r.Stem; r.Box.IsVisible = true; r.Check.IsEnabled = true; r.Check.IsChecked = false; }
        if (error is not null)
        {
            _result.Foreground = new SolidColorBrush(Color.Parse("#FF6B60"));
            _result.Text = error;
            if (errSetup) { var open = new Button { Content = L("x.ai.open_settings", "Open AI settings"), Padding = new Thickness(14, 7) }; open.Click += (_, _) => { App.ShowPage("ai"); Close(); }; _list.Children.Insert(0, open); }
        }
        else if (!ct.IsCancellationRequested) _result.Text = L("x.ai.rename_hint", "Check the names, edit any you like, then press Rename.");
        Recount();
    }

    private async Task<Dictionary<int, string>> NameBatchAsync(Row[] batch, int styleIndex, CancellationToken ct)
    {
        var sources = await Task.Run(() => batch.Select(r => { try { return AiFiles.Peek(r.Path); } catch { return null; } }).ToList(), ct);
        var text = new StringBuilder();
        var images = new List<string>();
        for (var i = 0; i < batch.Length; i++)
        {
            text.Append($"[{i}] current name: {Path.GetFileName(batch[i].Path)}\n");
            var s = sources[i];
            if (s is null) { text.Append("    content: (could not be read)\n"); continue; }
            if (s.Images.Count > 0 && string.IsNullOrWhiteSpace(s.Text)) { images.Add(s.Images[0]); text.Append($"    content: the picture is image #{images.Count}\n"); }
            else if (!string.IsNullOrWhiteSpace(s.Text)) text.Append("    content: ").Append(s.Text!.Length > 600 ? s.Text[..600] : s.Text).Append('\n');
            else text.Append("    content: (nothing readable: go by the current name)\n");
        }
        var lang = App.Settings.AiLanguage;
        var system =
            "You rename files so they are easy to find later. For each item look at its current name and its content. " +
            "Reply with ONLY a JSON object, no other text: {\"names\":[{\"i\":0,\"name\":\"...\"}]} with one entry per item and the same i. " +
            "Rules: the name has NO file extension; 2 to 7 words that say what the file IS (kind of document, subject, person or company, and a date or number when the content shows one); " +
            "never use \\ / : * ? \" < > |; at most 70 characters; if the content tells you nothing, tidy the current name instead (drop things like IMG_, Copy, final_v2, random digits). " +
            $"Style: {StyleRules[styleIndex]}. " + (lang.Length > 0 ? $"Write the names in {lang}." : "Write each name in the language of the file's content.");
        var answer = await Ai.CompleteAsync(new[] { new AiMsg("system", system), new AiMsg("user", text.ToString(), images.Count > 0 ? images : null) }, ct);

        var map = new Dictionary<int, string>();
        using var doc = Ai.ParseJson(answer);
        if (doc is not null && doc.RootElement.TryGetProperty("names", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.TryGetProperty("i", out var i) && i.TryGetInt32(out var idx) && e.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                    map[idx] = n.GetString() ?? "";
        if (map.Count == 0) throw new AiException(L("x.ai.rename_bad", "Mochi could not understand the AI's reply. Please press “Suggest again”."));
        return map;
    }

    /// <summary>Makes the AI's suggestion safe and in the chosen style: no forbidden characters, sensible length, never empty.</summary>
    private static string Clean(string name, string fallback, int style)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Where(c => !bad.Contains(c) && !char.IsControl(c)).ToArray());
        var ext = Path.GetExtension(s);
        if (ext.Length is >= 2 and <= 6 && !ext.Contains(' ')) s = s[..^ext.Length];   // the model added an extension: ours is kept
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '.', '-', '_');
        s = style switch
        {
            1 => Regex.Replace(s.ToLowerInvariant(), @"[\s_]+", "-"),
            2 => Regex.Replace(s.ToLowerInvariant(), @"[\s\-]+", "_"),
            3 => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()),
            _ => s,
        };
        if (s.Length > 70) s = s[..70].TrimEnd(' ', '-', '_', '.');
        return s.Length == 0 ? fallback : s;
    }

    // ---------------------------------------------------------------- renaming, and undoing it

    private static string Free(string dir, string stem, string ext, string current)
    {
        var path = Path.Combine(dir, stem + ext);
        for (var n = 2; File.Exists(path) && !string.Equals(path, current, StringComparison.OrdinalIgnoreCase); n++) path = Path.Combine(dir, $"{stem} ({n}){ext}");
        return path;
    }

    private async Task ApplyAsync()
    {
        var todo = _rows.Where(r => r.Check.IsChecked == true && r.Check.IsEnabled).Select(r => (r.Path, Stem: Clean(r.Box.Text ?? "", r.Stem, 0))).ToList();
        if (todo.Count == 0) return;
        _apply.IsEnabled = false; _again.IsEnabled = false; _style.IsEnabled = false; _all.IsEnabled = false;
        foreach (var r in _rows) { r.Check.IsEnabled = false; r.Box.IsEnabled = false; }
        Orb.Calm = false;
        var failed = 0;
        await Task.Run(() =>
        {
            foreach (var (path, stem) in todo)
            {
                try
                {
                    var target = Free(Path.GetDirectoryName(path)!, stem, Path.GetExtension(path), path);
                    if (string.Equals(target, path, StringComparison.Ordinal)) continue;
                    File.Move(path, target);
                    lock (_done) _done.Add((path, target));
                }
                catch { failed++; }
            }
        });
        Orb.Calm = true;
        foreach (var (o, n) in _done)
            if (_rows.FirstOrDefault(r => r.Path == o) is { } row) { row.Box.Text = Path.GetFileNameWithoutExtension(n); row.Check.IsChecked = true; }
        _result.Foreground = new SolidColorBrush(Color.Parse(failed == 0 ? "#34C759" : "#FFB020"));
        _result.Text = (_done.Count == 1 ? L("x.ai.renamed_one", "✓  Renamed 1 file") : string.Format(L("x.ai.renamed_n", "✓  Renamed {0} files"), _done.Count)) + (failed > 0 ? string.Format(L("x.ai.n_failed", "  ·  {0} could not be renamed (in use?)"), failed) : "");
        _undo.IsVisible = _done.Count > 0;
        _count.Text = "";
        _cancel.Content = L("common.done", "Done");
    }

    private void UndoAll()
    {
        var back = 0;
        foreach (var (old, now) in Enumerable.Reverse(_done).ToList())
        {
            try { if (File.Exists(now) && !File.Exists(old)) { File.Move(now, old); back++; } } catch { /* leave that one as it is */ }
        }
        _done.Clear();
        _undo.IsVisible = false;
        _result.Foreground = P.SubBrush;
        _result.Text = string.Format(L("x.ai.undone", "↶  Put {0} files back to their old names."), back);
        foreach (var r in _rows) { r.Box.IsEnabled = true; r.Check.IsEnabled = true; r.Box.Text = r.Proposed.Length > 0 ? r.Proposed : r.Stem; }
        _again.IsEnabled = true; _style.IsEnabled = true; _all.IsEnabled = true;
        Recount();
    }
}
