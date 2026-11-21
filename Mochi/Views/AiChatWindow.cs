using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Animation;
using Avalonia.Threading;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>Opens the AI tool windows; this is what the toolbar's AI presets call.</summary>
public static class AiLauncher
{
    public static Task<IReadOnlyList<string>> RunAsync(string mode, IReadOnlyList<string> files) => mode switch
    {
        "ai:rename" => AiRenameWindow.RunAsync(files),
        "ai:command" => AiCommandWindow.RunAsync(files),
        _ => AiChatWindow.RunAsync(mode, files),
    };
}

/// <summary>
/// Summarise, Ask this file, Explain image: one conversation window. The answer streams in word by word,
/// with a living orb and a shimmer while the first words are on their way, then follow-ups and one-tap refinements.
/// </summary>
public sealed class AiChatWindow : AiShell
{
    private readonly string _mode;
    private readonly IReadOnlyList<string> _files;
    private readonly StackPanel _log = new() { Spacing = 20, Margin = new Thickness(22, 6, 22, 18) };
    private readonly ScrollViewer _scroll = new();
    private readonly TextBox _input = new() { AcceptsReturn = false, FontSize = 14, Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 10) };
    private readonly Button _send = new() { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 17, FontWeight = FontWeight.Bold };
    private readonly WrapPanel _chips = new() { Margin = new Thickness(22, 0, 22, 8) };
    private readonly List<AiMsg> _hist = new();
    private readonly TaskCompletionSource<IReadOnlyList<string>> _done = new();
    private List<AiSource> _src = new();
    private string _system = "";
    private bool _busy;
    private string? _saved;
    private string _lastAnswer = "";

    public static Task<IReadOnlyList<string>> RunAsync(string mode, IReadOnlyList<string> files)
    {
        var w = new AiChatWindow(mode, files);
        w.Show();
        _ = w.StartAsync();
        return w._done.Task;
    }

    private AiChatWindow(string mode, IReadOnlyList<string> files)
        : base(mode switch { "ai:ask" => L("x.ai.t_ask", "Ask this file"), "ai:explain" => L("x.ai.t_explain", "Explain image"), _ => L("x.ai.t_summary", "Summarise") }, 700, 720)
    {
        _mode = mode; _files = files;
        SetSub(FilesLine(files));
        Closed += (_, _) => _done.TrySetResult(_saved is null ? Array.Empty<string>() : new[] { _saved });

        _scroll.Content = _log;
        _input.Classes.Add("aiinput");
        _send.Content = "↑";
        _send.Classes.Add("pill");
        _send.Click += (_, _) => { if (_busy) Cts.Cancel(); else SendTyped(); };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter && !_busy) { SendTyped(); e.Handled = true; } };
        _input.Watermark = _mode == "ai:ask" ? L("x.ai.ask_ph", "Ask anything about this file…") : L("x.ai.follow_ph", "Ask a follow-up…");

        var bar = new Border
        {
            Margin = new Thickness(22, 0, 22, 18), CornerRadius = new CornerRadius(24), Padding = new Thickness(12, 4, 6, 4),
            Background = new SolidColorBrush(P.Text, 0.06), BorderBrush = P.BorderBrush, BorderThickness = new Thickness(1),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _input, Place(_send, 1) } },
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        grid.Children.Add(_scroll);
        Grid.SetRow(_chips, 1); grid.Children.Add(_chips);
        Grid.SetRow(bar, 2); grid.Children.Add(bar);
        Body.Content = grid;
        Loaded += (_, _) => _input.Focus();
    }

    private static Control Place(Control c, int col) { Grid.SetColumn(c, col); return c; }

    // ---------------------------------------------------------------- start: connect, read, first answer

    private async Task StartAsync()
    {
        _input.IsEnabled = false; _send.IsEnabled = false;
        if (!await GateAsync()) return;
        var status = Waiting(new[] { L("x.ai.reading", "Reading your file…") }, out var stop);
        _log.Children.Add(status);
        Orb.Calm = false;

        var budget = Math.Max(4000, Ai.MaxChars / Math.Max(1, _files.Count));
        try { _src = await Task.Run(() => _files.Select(f => AiFiles.Load(f, budget)).ToList()); }
        catch (Exception ex) { stop(); _log.Children.Remove(status); Fail(ex.Message, false, () => _ = StartAsync()); return; }
        stop(); _log.Children.Remove(status);

        if (_src.All(s => s.IsEmpty))
        {
            Orb.Calm = true;
            _log.Children.Add(Note(L("x.ai.no_text", "Mochi couldn't find any readable text in this file. If it is a scan or a picture, use “Explain image”.")));
            return;
        }
        BuildContext();
        _log.Children.Add(ReadCard());
        _input.IsEnabled = true; _send.IsEnabled = true; _input.Focus();

        if (_mode == "ai:ask")
        {
            Orb.Calm = true;
            _log.Children.Add(Note(string.Format(L("x.ai.ask_intro", "Ask anything about {0}. Mochi answers only from what is in the file."), _files.Count == 1 ? Path.GetFileName(_files[0]) : _files.Count + " files")));
            ShowChips(_src.Any(s => s.Images.Count > 0 && s.IsPicture)
                ? new[] { ("What's in this picture?", "What is in this picture?"), ("Read the text", "Transcribe all the text you can see.") }
                : new[] { ("What is this about?", "What is this about, in two sentences?"), ("Key dates & amounts", "List every important date, deadline and amount, with what each refers to."), ("What should I do next?", "Based on this, what should I do next?") });
            return;
        }
        var first = _mode == "ai:explain"
            ? "Describe this picture. Then transcribe any text in it, and if it is a chart, table or receipt, give its data as a Markdown table."
            : _files.Count > 1
                ? "Summarise each file in its own short section, then give one overall takeaway."
                : "Summarise this. Start with one sentence saying what it is, then the key points as bullets, then any dates, amounts, deadlines or action items (leave that out if there are none).";
        await SendAsync(first, _mode == "ai:explain" ? L("x.ai.first_explain", "Explain this picture") : L("x.ai.first_summary", "Summarise this"));
    }

    private void BuildContext()
    {
        var lang = App.Settings.AiLanguage;
        var sb = new StringBuilder();
        sb.Append(_mode switch
        {
            "ai:ask" => "You are Mochi, a careful assistant that answers questions about the user's file(s), using only what they contain. ",
            "ai:explain" => "You are Mochi, an assistant that explains pictures: what they show, the text in them, and the numbers in charts, tables and receipts. ",
            _ => "You are Mochi, an assistant that summarises documents clearly and faithfully. ",
        });
        sb.Append("Reply in Markdown: short headings, bullet points, bold for key terms, tables for tabular data. Be concise. Never invent facts that are not in the file; if the answer is not there, say so plainly. ");
        sb.Append(lang.Length > 0 ? $"Always answer in {lang}. " : "Answer in the language of the file, or of the user's question if it differs. ");
        foreach (var s in _src.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
        {
            sb.Append("\n\n=== File: ").Append(s.Name).Append(" ===\n").Append(s.Text);
            if (s.Truncated) sb.Append("\n[The file is longer; only the first part is shown.]");
        }
        if (_src.Any(s => s.Images.Count > 0)) sb.Append("\n\nThe picture(s) of the file(s) are attached to the user's first message, in the order: ").Append(string.Join(", ", _src.Where(s => s.Images.Count > 0).Select(s => s.Name))).Append('.');
        _system = sb.ToString();
    }

    private Control ReadCard()
    {
        var parts = _src.Select(s =>
            s.Images.Count > 0 && s.IsPicture ? $"{s.Name} · {(s.Ext == ".pdf" ? s.Images.Count + " pages as pictures" : "picture")}"
            : $"{s.Name} · {(s.Text?.Length ?? 0):N0} characters{(s.Truncated ? " (first part)" : "")}");
        return new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8), Background = new SolidColorBrush(P.Text, 0.05),
            Child = new TextBlock { Text = "✓  " + L("x.ai.read", "Read") + ":  " + string.Join("   ·   ", parts), FontSize = 12, Foreground = P.SubBrush, TextWrapping = TextWrapping.Wrap },
        };
    }

    private Control Note(string text) => new TextBlock { Text = text, Foreground = P.SubBrush, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(2, 2) };

    private void ShowChips(IEnumerable<(string Label, string Prompt)> chips)
    {
        _chips.Children.Clear();
        foreach (var (label, prompt) in chips)
        {
            var b = new Button { Content = label, FontSize = 12, Padding = new Thickness(13, 6), CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 6, 6), Background = new SolidColorBrush(P.Text, 0.08), IsEnabled = !_busy };
            b.Click += async (_, _) => { if (!_busy) await SendAsync(prompt, label); };
            _chips.Children.Add(b);
        }
    }

    private void RefineChips() => ShowChips(_mode switch
    {
        "ai:explain" => new[] { ("Extract text", "Transcribe every piece of text in the picture exactly, in reading order."), ("Read the table / chart", "If there is a table or chart, give its data as a Markdown table. Otherwise say there is none."), ("Alt text", "Write one sentence of alt text (max 125 characters) for this picture."), ("More detail", "Describe it in more detail.") },
        "ai:ask" => new[] { ("Shorter", "Say that in two sentences."), ("Show the exact wording", "Quote the exact sentences from the file that support this answer.") },
        _ => new[] { ("Shorter", "Make the summary shorter: at most three bullets."), ("Action items", "List only the action items, deadlines and amounts."), ("Explain simply", "Explain it simply, as if to a 12-year-old."), ("In Hindi", "Translate this summary into Hindi."), ("In English", "Translate this summary into English.") },
    });

    // ---------------------------------------------------------------- the waiting animation

    /// <summary>The "working" block: shimmering lines and a status line that changes every couple of seconds.</summary>
    private Control Waiting(string[] steps, out Action stop)
    {
        var label = new TextBlock { Text = steps[0], FontSize = 13, Foreground = P.SubBrush, Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) } } };
        var shimmer = new AiShimmer { Ink = P.Text, Margin = new Thickness(0, 8, 0, 0) };
        var i = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        timer.Tick += (_, _) =>
        {
            if (steps.Length < 2) return;
            i = (i + 1) % steps.Length;
            label.Opacity = 0;
            DispatcherTimer.RunOnce(() => { label.Text = steps[i]; label.Opacity = 1; }, TimeSpan.FromMilliseconds(230));
        };
        timer.Start();
        stop = timer.Stop;
        return new StackPanel { Children = { Header(), label, shimmer } };
    }

    private Control Header() => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6),
        Children =
        {
            new TextBlock { Text = "✦", Foreground = new LinearGradientBrush { GradientStops = { new GradientStop(Color.Parse("#FF6FB5"), 0), new GradientStop(Color.Parse("#7A5CFF"), 1) } }, FontSize = 14, FontWeight = FontWeight.Bold },
            new TextBlock { Text = "Mochi", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = P.SubBrush },
        },
    };

    // ---------------------------------------------------------------- sending

    private void SendTyped()
    {
        var t = (_input.Text ?? "").Trim();
        if (t.Length == 0) return;
        _input.Text = "";
        _ = SendAsync(t, t);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; Orb.Calm = !busy;
        _send.Content = busy ? "■" : "↑";
        _input.IsEnabled = !busy;
        foreach (var c in _chips.Children.OfType<Button>()) c.IsEnabled = !busy;
        if (!busy) _input.Focus();
    }

    private async Task SendAsync(string prompt, string shown)
    {
        if (_busy) return;
        Cts = new CancellationTokenSource();
        var ct = Cts.Token;
        _chips.Children.Clear();
        _log.Children.Add(UserBubble(shown));
        var images = _hist.Count == 0 ? _src.SelectMany(s => s.Images).Take(Ai.MaxImages).ToList() : null;
        _hist.Add(new AiMsg("user", prompt, images));
        SetBusy(true);

        var steps = _mode == "ai:explain" ? new[] { L("x.ai.looking", "Looking at the picture…"), L("x.ai.thinking", "Thinking…"), L("x.ai.writing", "Writing…") }
            : new[] { L("x.ai.reading_it", "Reading it closely…"), L("x.ai.thinking", "Thinking…"), L("x.ai.writing", "Writing…") };
        var waiting = Waiting(steps, out var stopWaiting);
        var host = new ContentControl { Content = waiting };
        var block = new StackPanel { Children = { host } };
        _log.Children.Add(block);
        ScrollDown(true);

        var text = new StringBuilder();
        var dirty = false; var first = true;
        var gate = new object();
        var ui = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        string Snapshot() { lock (gate) return text.ToString(); }
        void Paint(bool caret)
        {
            var near = NearBottom();
            host.Content = new StackPanel { Children = { Header(), Md.Render(Snapshot() + (caret ? " ▍" : ""), P) } };
            if (near) ScrollDown(false);
        }
        ui.Tick += (_, _) => { if (!dirty) return; dirty = false; if (first) { first = false; stopWaiting(); } Paint(true); };
        ui.Start();

        try
        {
            var msgs = new List<AiMsg> { new("system", _system) };
            msgs.AddRange(_hist);
            var full = await Ai.StreamAsync(msgs, d => { lock (gate) text.Append(d); dirty = true; }, ct);
            ui.Stop(); stopWaiting();
            _lastAnswer = full;
            _hist.Add(new AiMsg("assistant", full));
            Paint(false);
            if (Ai.TakeNote() is { } note) block.Children.Add(Note("↪  " + note));   // a backup answered: say so
            block.Children.Add(Actions());
        }
        catch (OperationCanceledException)
        {
            ui.Stop(); stopWaiting();
            var partial = Snapshot();
            if (partial.Length == 0) { _log.Children.Remove(block); _log.Children.Remove(_log.Children.Last()); _hist.RemoveAt(_hist.Count - 1); }
            else { _lastAnswer = partial; _hist.Add(new AiMsg("assistant", partial)); Paint(false); block.Children.Add(Note(L("x.ai.stopped", "Stopped."))); block.Children.Add(Actions()); }
        }
        catch (Exception ex)
        {
            ui.Stop(); stopWaiting();
            _hist.RemoveAt(_hist.Count - 1);
            var ai = ex as AiException;
            host.Content = ErrorCard(ai?.Message ?? ex.Message, ai?.NeedsSetup ?? false, () => { _log.Children.Remove(block); _log.Children.RemoveAt(_log.Children.Count - 1); _ = SendAsync(prompt, shown); });
        }
        finally
        {
            ui.Stop();
            if (IsVisible) { SetBusy(false); if (_hist.Count > 0) RefineChips(); ScrollDown(false); }
        }
    }

    private Control UserBubble(string text) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 460, CornerRadius = new CornerRadius(18, 18, 4, 18), Padding = new Thickness(14, 9),
        Background = new SolidColorBrush(P.Accent),
        Child = new SelectableTextBlock { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14 },
    };

    // ---------------------------------------------------------------- after an answer: copy, save

    private Control Actions()
    {
        var copy = Action("⧉  " + L("x.ai.copy", "Copy"));
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(_lastAnswer);
            copy.Content = "✓  " + L("x.ai.copied", "Copied");
        };
        var save = Action("⤓  " + L("x.ai.save", "Save as file"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-8, 6, 0, 0), Children = { copy, save } };
        save.Click += (_, _) =>
        {
            try
            {
                var path = SavePath();
                File.WriteAllText(path, _lastAnswer, new UTF8Encoding(false));
                _saved = path;
                save.Content = "✓  " + L("x.ai.saved", "Saved");
                save.IsEnabled = false;
                var show = Action("↗  " + L("x.ai.show", "Show in folder"));
                show.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                row.Children.Add(show);
            }
            catch (Exception ex) { save.Content = ex.Message; }
        };
        return row;
    }

    private static Button Action(string text) => new() { Content = text, Classes = { "plain" }, FontSize = 12, Padding = new Thickness(10, 5) };

    private string SavePath()
    {
        var first = _files[0];
        var dir = !string.IsNullOrWhiteSpace(App.Settings.OutputFolder) && Directory.Exists(App.Settings.OutputFolder) ? App.Settings.OutputFolder : Path.GetDirectoryName(first)!;
        var tag = _mode switch { "ai:ask" => "answer", "ai:explain" => "description", _ => "summary" };
        var stem = Path.GetFileNameWithoutExtension(first) + " - " + tag;
        var path = Path.Combine(dir, stem + ".md");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem} ({n}).md");
        return path;
    }

    // ---------------------------------------------------------------- scrolling and errors

    private bool NearBottom() => _scroll.Offset.Y + _scroll.Viewport.Height >= _scroll.Extent.Height - 90;

    private void ScrollDown(bool force) => Dispatcher.UIThread.Post(() => { if (force || NearBottom()) _scroll.ScrollToEnd(); }, DispatcherPriority.Background);

    private void Fail(string message, bool needsSetup, Action retry)
    {
        Orb.Calm = true;
        _log.Children.Add(ErrorCard(message, needsSetup, () => { _log.Children.Clear(); retry(); }));
    }
}
