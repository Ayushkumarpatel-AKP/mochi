using System.Text;
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
/// "Do it by typing": say what you want in plain words, the AI chains Mochi's own presets into a plan,
/// you see the plan, press Run, and watch each step finish. Only presets that really exist and fit the files are ever used.
/// </summary>
public sealed class AiCommandWindow : AiShell
{
    private sealed record Step(Preset Preset, string Why);

    /// <summary>Text typed in a toolbar's search box, carried over so the window can start planning at once.</summary>
    public static string? PendingPrompt { get; set; }

    private static readonly string[] UiModes = { "annotate", "perspective", "removebg", "erase", "dupes", "smartsearch", "organize", "pdfedit", "sheetstudio" };

    private readonly IReadOnlyList<string> _files;
    private readonly TextBox _ask = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 74, FontSize = 15, Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(16) };
    private readonly WrapPanel _examples = new();
    private readonly Button _plan = new() { Classes = { "pill" }, Padding = new Thickness(24, 10), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ContentControl _out = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TaskCompletionSource<IReadOnlyList<string>> _tcs = new();
    private readonly Dictionary<int, (Border Mark, TextBlock Glyph, AiOrb Spin)> _marks = new();
    private List<Step> _steps = new();
    private bool _busy, _running;

    public static Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> files)
    {
        var w = new AiCommandWindow(files);
        w.Show();
        _ = w.StartAsync();
        return w._tcs.Task;
    }

    private AiCommandWindow(IReadOnlyList<string> files) : base(L("x.ai.t_command", "Do it by typing"), 720, 720)
    {
        _files = files;
        SetSub(FilesLine(files));
        Closed += (_, _) => _tcs.TrySetResult(Array.Empty<string>());

        _ask.Background = new SolidColorBrush(P.Text, 0.06);
        _ask.Watermark = L("x.ai.cmd_ph", "Tell Mochi what to do with these files…");
        _plan.Content = "✦  " + L("x.ai.plan_it", "Plan it");
        _plan.Click += async (_, _) => await PlanAsync();
        _ask.KeyDown += async (_, e) => { if (e.Key == Key.Enter && !_busy) { e.Handled = true; await PlanAsync(); } };
        foreach (var ex in Examples())
        {
            var b = new Button { Content = ex, FontSize = 12, Padding = new Thickness(13, 6), CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 6, 6), Background = new SolidColorBrush(P.Text, 0.08) };
            b.Click += (_, _) => { if (_running) return; _ask.Text = ex; _ask.CaretIndex = ex.Length; _ask.Focus(); };
            _examples.Children.Add(b);
        }

        Body.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 12, Margin = new Thickness(22, 6, 22, 22),
                Children = { new TextBlock { Text = L("x.ai.cmd_intro", "Describe the result you want. Mochi picks the tools, shows you the plan, and only then runs it."), Foreground = P.SubBrush, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 }, _ask, _examples, _plan, _out },
            },
        };
        Loaded += (_, _) => _ask.Focus();
    }

    private IEnumerable<string> Examples()
    {
        var exts = _files.Select(f => Util.Ext(f)).Distinct().ToList();
        if (exts.All(e => AiFiles.IsImage(e))) return new[] { "Make them smaller for email", "Convert to WebP and zip them", "Make one PDF from these pictures", "Shrink to half size" };
        if (exts.All(e => e == ".pdf")) return new[] { "Merge them into one PDF", "Compress and protect with a password", "Turn each page into a PNG", "Get the text out" };
        if (exts.All(e => e is ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm")) return new[] { "Make it small enough for WhatsApp", "Extract the audio as MP3", "Compress it and zip it" };
        return new[] { "Convert to PDF", "Compress everything and zip it", "Make them smaller for email", "Turn into plain text" };
    }

    private async Task StartAsync()
    {
        if (!await GateAsync()) return;
        if (PendingPrompt is { Length: > 0 } p) { _ask.Text = p; PendingPrompt = null; await PlanAsync(); }
    }

    // ---------------------------------------------------------------- planning

    private IEnumerable<Preset> Catalog() => Converter.Presets.Where(p =>
        !p.Mode.StartsWith("ai:") && !UiModes.Any(m => p.Mode.StartsWith(m)) && !App.Settings.DisabledFields.Contains(p.Category) && p.Id != "mid");

    private string Describe()
    {
        var shown = _files.Take(14).Select(f =>
        {
            try { return Directory.Exists(f) ? Path.GetFileName(f.TrimEnd('\\', '/')) + " (folder)" : $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1024.0 / 1024.0:0.##} MB)"; }
            catch { return Path.GetFileName(f); }
        });
        return string.Join("; ", shown) + (_files.Count > 14 ? $"; … and {_files.Count - 14} more" : "");
    }

    private async Task PlanAsync()
    {
        var request = (_ask.Text ?? "").Trim();
        if (request.Length == 0 || _busy || _running) return;
        _busy = true; _plan.IsEnabled = false; Orb.Calm = false;
        Cts = new CancellationTokenSource();
        var ct = Cts.Token;

        var steps = new[] { L("x.ai.cmd_s1", "Understanding your request…"), L("x.ai.cmd_s2", "Choosing tools…"), L("x.ai.cmd_s3", "Checking the plan fits your files…") };
        var label = new TextBlock { Text = steps[0], Foreground = P.SubBrush, FontSize = 13 };
        var i = 0;
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
        tick.Tick += (_, _) => { i = Math.Min(i + 1, steps.Length - 1); label.Text = steps[i]; };
        tick.Start();
        _out.Content = new StackPanel { Children = { label, new AiShimmer(3) { Ink = P.Text, Margin = new Thickness(0, 8, 0, 0) } } };

        try
        {
            var catalog = string.Join("\n", Catalog().Select(p => $"{p.Id} | {p.Name} | {(p.Extension.Length > 0 ? p.Extension : "-")}{(p.IsMerge ? " | merges all files into one" : "")}"));
            var lang = App.Settings.AiLanguage;
            var system =
                "You plan file jobs for Mochi, a file converter. The user says what they want done to their files. " +
                "Choose a short chain (1 to 4 steps) of Mochi presets. Steps run in order; the first takes the user's files and each next step takes the files the previous step produced. " +
                "Use ONLY preset ids from the catalog below, and prefer the fewest steps. Match numbers in the request to preset names (e.g. 1080, 720, 200 KB, 2 MB). A preset marked 'merges all files into one' needs two or more files. " +
                "Reply with ONLY JSON: {\"say\":\"one plain sentence about what will happen\",\"steps\":[{\"preset\":\"<id>\",\"why\":\"a few words\"}],\"cannot\":\"\"}. " +
                "If the request cannot be done with the catalog, return \"steps\":[] and put a short reason plus the closest thing that is possible in \"cannot\". " +
                (lang.Length > 0 ? $"Write \"say\", \"why\" and \"cannot\" in {lang}. " : System.Text.RegularExpressions.Regex.IsMatch(request, @"^[\x00-\x7F]+$") ? "Write \"say\", \"why\" and \"cannot\" in English. " : "Write \"say\", \"why\" and \"cannot\" in the language of the request. ") +
                "\n\nCatalog (id | name | output extension):\n" + catalog;
            var msgs = new List<AiMsg> { new("system", system), new("user", $"Files: {Describe()}\n\nRequest: {request}") };

            string say = ""; List<Step>? plan = null; string? cannot = null; string? problem = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var answer = await Ai.CompleteAsync(msgs, ct);
                using var doc = Ai.ParseJson(answer);
                if (doc is null) { problem = "The reply was not valid JSON. Reply with only the JSON object."; msgs.Add(new("assistant", answer)); msgs.Add(new("user", problem)); continue; }
                var root = doc.RootElement;
                say = root.TryGetProperty("say", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() ?? "" : "";
                cannot = root.TryGetProperty("cannot", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() : null;
                var list = new List<Step>(); string? bad = null;
                if (root.TryGetProperty("steps", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                    {
                        var id = e.TryGetProperty("preset", out var pid) && pid.ValueKind == System.Text.Json.JsonValueKind.String ? pid.GetString() ?? "" : "";
                        var why = e.TryGetProperty("why", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.String ? w.GetString() ?? "" : "";
                        if (Converter.Find(id) is { } pr && Catalog().Any(x => x.Id == pr.Id)) list.Add(new Step(pr, why));
                        else { bad = $"“{id}” is not in the catalog. Use only ids from the catalog."; break; }
                    }
                if (bad is not null) { problem = bad; msgs.Add(new("assistant", answer)); msgs.Add(new("user", bad)); continue; }
                if (list.Count == 0) { plan = list; problem = null; break; }
                problem = Validate(list);
                if (problem is null) { plan = list; break; }
                msgs.Add(new("assistant", answer));
                msgs.Add(new("user", "That plan does not work: " + problem + " Fix it with presets that accept those files, or explain in \"cannot\" if it is impossible."));
            }

            tick.Stop();
            if (plan is null) throw new AiException(L("x.ai.cmd_fail", "Mochi could not build a plan that fits these files. Try describing the result differently, or pick a tool from the toolbar.") + (problem is null ? "" : "\n" + problem));
            if (plan.Count == 0) { _out.Content = Info(cannot is { Length: > 0 } ? cannot : L("x.ai.cmd_none", "Mochi has no tool for that yet.")); }
            else { _steps = plan; _out.Content = PlanCard(say); }
        }
        catch (OperationCanceledException) { tick.Stop(); }
        catch (Exception ex)
        {
            tick.Stop();
            var ai = ex as AiException;
            _out.Content = ErrorCard(ai?.Message ?? ex.Message, ai?.NeedsSetup ?? false, () => _ = PlanAsync());
        }
        finally { _busy = false; _plan.IsEnabled = !_running; Orb.Calm = true; }
    }

    /// <summary>Walks the chain with file types only (no files are touched): every step must accept what the step before makes.</summary>
    private string? Validate(List<Step> steps)
    {
        var exts = _files.Select(f => Directory.Exists(f) ? "" : Util.Ext(f)).Distinct().ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            var p = steps[i].Preset;
            foreach (var e in exts)
                if (!Converter.IsSupported("x" + e, p))
                    return $"Step {i + 1} “{p.Name}” cannot take {(e.Length == 0 ? "folders" : e + " files")}.";
            if (p.IsMerge && i == 0 && _files.Count < 2) return $"Step 1 “{p.Name}” needs at least two files, but there is only one.";
            if (p.Extension.Length > 0) exts = new() { p.Extension };
        }
        return null;
    }

    // ---------------------------------------------------------------- showing the plan

    private Control Info(string text) => new Border
    {
        CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Background = new SolidColorBrush(Color.Parse("#FFB020"), 0.13), BorderBrush = new SolidColorBrush(Color.Parse("#FFB020"), 0.4), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
    };

    private Control PlanCard(string say)
    {
        _marks.Clear();
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(new TextBlock { Text = say.Length > 0 ? say : L("x.ai.cmd_plan", "Here is the plan"), FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10) });
        for (var i = 0; i < _steps.Count; i++)
        {
            var s = _steps[i];
            var (glyph, tint) = Look.Of(s.Preset.Category);
            var spin = new AiOrb { Width = 26, Height = 26, IsVisible = false };
            var mark = new TextBlock { Text = (i + 1).ToString(), FontSize = 12, FontWeight = FontWeight.Bold, Foreground = P.SubBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(P.Text, 0.09), VerticalAlignment = VerticalAlignment.Center, Child = new Grid { Children = { mark, spin } } };
            _marks[i] = (badge, mark, spin);
            var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Background = tint, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = s.Preset.DisplayName + (s.Preset.Extension.Length > 0 ? "   →  " + s.Preset.Extension : ""), FontWeight = FontWeight.SemiBold, FontSize = 14 },
                    new TextBlock { Text = s.Why, FontSize = 12, Foreground = P.SubBrush, IsVisible = s.Why.Length > 0, TextWrapping = TextWrapping.Wrap },
                },
            };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 12 };
            g.Children.Add(badge); Grid.SetColumn(icon, 1); g.Children.Add(icon); Grid.SetColumn(text, 2); g.Children.Add(text);
            var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), Background = new SolidColorBrush(P.Text, 0.06), Child = g };
            stack.Children.Add(card);
            if (i < _steps.Count - 1) stack.Children.Add(new TextBlock { Text = "↓", Foreground = P.SubBrush, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(26, 2, 0, 2), FontSize = 14 });
        }
        var run = new Button { Content = "▶  " + L("x.ai.run", "Run this plan"), Classes = { "pill" }, Padding = new Thickness(26, 11) };
        run.Click += async (_, _) => { run.IsEnabled = false; await RunAsync2(); };
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 16, 0, 0), Children = { run } });
        return stack;
    }

    // ---------------------------------------------------------------- running it

    private static string Dest(string originDir)
    {
        var f = App.Settings.OutputFolder;
        if (!string.IsNullOrWhiteSpace(f)) { try { Directory.CreateDirectory(f); return f; } catch { /* fall back to next to the original */ } }
        return originDir;
    }

    private async Task RunAsync2()
    {
        _running = true; _plan.IsEnabled = false; _ask.IsEnabled = false; Orb.Calm = false;
        var tmp = Util.TempDir();
        var items = _files.Select(f => (Path: f, Origin: Path.GetDirectoryName(f.TrimEnd('\\', '/')) ?? ".")).ToList();
        var failedAt = -1; string? error = null;
        try
        {
            for (var i = 0; i < _steps.Count; i++)
            {
                if (Cts.IsCancellationRequested) throw new OperationCanceledException();
                var step = i; var last = i == _steps.Count - 1; var p = _steps[i].Preset;
                Mark(step, "run");
                var input = items;
                items = await Task.Run(() =>
                {
                    var outs = new List<(string, string)>();
                    if (p.IsMerge)
                    {
                        var origin = input[0].Origin;
                        foreach (var o in Converter.RunMany(input.Select(x => x.Path).ToList(), p, last ? Dest(origin) : tmp)) outs.Add((o, origin));
                    }
                    else
                        foreach (var x in input)
                            foreach (var o in Converter.Run(x.Path, p, last ? Dest(x.Origin) : tmp)) outs.Add((o, x.Origin));
                    return outs;
                });
                if (items.Count == 0) throw new InvalidOperationException(string.Format(L("x.ai.cmd_empty", "“{0}” produced nothing."), p.DisplayName));
                Mark(step, "ok");
                await Task.Delay(220);   // long enough to see the tick land before the next step starts
            }
            var outputs = items.Select(x => x.Path).ToList();
            Orb.Calm = true;
            // the plan stays on screen with every step ticked; the result sits under it
            if (_out.Content is StackPanel plan) plan.Children.Add(new Border { Margin = new Thickness(0, 16, 0, 0), Child = DoneCard(outputs) });
            else _out.Content = DoneCard(outputs);
            _tcs.TrySetResult(outputs);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            error = ex.Message;
            failedAt = _marks.FirstOrDefault(m => m.Value.Spin.IsVisible).Key;
            Mark(failedAt, "fail");
            Orb.Calm = true;
            if (_out.Content is StackPanel sp) sp.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Background = new SolidColorBrush(Color.Parse("#FF453A"), 0.13), Child = new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap } });
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* temp cleanup is best-effort */ }
            _running = false; _ask.IsEnabled = true; _plan.IsEnabled = true;
        }
    }

    private void Mark(int i, string state)
    {
        if (!_marks.TryGetValue(i, out var m)) return;
        m.Spin.IsVisible = state == "run"; m.Glyph.IsVisible = state != "run";
        if (state == "ok") { m.Glyph.Text = "✓"; m.Glyph.Foreground = Brushes.White; m.Mark.Background = new SolidColorBrush(Color.Parse("#34C759")); }
        else if (state == "fail") { m.Glyph.Text = "✕"; m.Glyph.Foreground = Brushes.White; m.Mark.Background = new SolidColorBrush(Color.Parse("#FF453A")); }
    }

    private Control DoneCard(List<string> outputs)
    {
        var show = new Button { Content = "↗  " + L("x.ai.show", "Show in folder"), Padding = new Thickness(16, 8) };
        show.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputs[0]}\"");
        var again = new Button { Content = L("x.ai.cmd_again", "Do something else"), Classes = { "plain" }, Padding = new Thickness(12, 8) };
        again.Click += (_, _) => { _out.Content = null; _ask.Text = ""; _ask.Focus(); };
        var names = string.Join("\n", outputs.Take(5).Select(o => "•  " + Path.GetFileName(o))) + (outputs.Count > 5 ? $"\n…  +{outputs.Count - 5}" : "");
        return new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Background = new SolidColorBrush(Color.Parse("#34C759"), 0.12), BorderBrush = new SolidColorBrush(Color.Parse("#34C759"), 0.4), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "✓  " + (outputs.Count == 1 ? L("x.ai.cmd_done_one", "Done: 1 file made") : string.Format(L("x.ai.cmd_done_n", "Done: {0} files made"), outputs.Count)), FontSize = 16, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = names, Foreground = P.SubBrush, FontSize = 12.5 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { show, again } },
                },
            },
        };
    }
}
