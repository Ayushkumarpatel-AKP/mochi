using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>The "connect an AI" form: provider, key, model, answer language, and a button that tests the connection. Used in Settings and inside the AI windows.</summary>
public sealed class AiSetupCard : Border
{
    private static readonly string[] Languages = { "", "English", "Hindi", "Spanish", "French", "German", "Portuguese", "Japanese", "Korean", "Chinese", "Arabic", "Russian" };

    private readonly Pal _p;
    private readonly ComboBox _provider = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _key = new() { PasswordChar = '•', HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _url = new() { Watermark = "https://your-server/v1", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoCompleteBox _model = new() { MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.Contains, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _lang = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _keyRow = new() { Spacing = 6 };
    private readonly StackPanel _urlRow = new() { Spacing = 6 };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly AiOrb _busy = new() { Width = 26, Height = 26, IsVisible = false };
    private readonly CheckBox _fallback = new() { IsChecked = App.Settings.AiFallback };
    private readonly Button _save = new() { Classes = { "pill" }, Padding = new Thickness(22, 9) };
    private readonly Action? _onSaved;
    private bool _loading;

    private static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    public AiSetupCard(Pal p, bool embedded, Action? onSaved = null)
    {
        _p = p; _onSaved = onSaved;
        CornerRadius = new CornerRadius(18);
        Padding = embedded ? new Thickness(0) : new Thickness(24);
        if (!embedded) { Background = new SolidColorBrush(p.Text, 0.05); BorderBrush = p.BorderBrush; BorderThickness = new Thickness(1); }

        _provider.ItemsSource = Ai.Providers.Select(x => x.Name).ToList();
        _lang.ItemsSource = Languages.Select(x => x.Length == 0 ? L("x.ai.lang_same", "Same language as the file") : x).ToList();
        _hint.Foreground = p.SubBrush;
        _save.Content = L("x.ai.test_save", "Test and save");

        _keyRow.Children.Add(Label(L("x.ai.key", "API key")));
        var keyLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        keyLine.Children.Add(_key);
        var get = new Button { Content = L("x.ai.get_key", "Get a key ↗"), Classes = { "plain" }, Padding = new Thickness(10, 6) };
        get.Click += (_, _) => { var u = Ai.Find(SelectedId()).KeyUrl; if (u.Length > 0) Native.OpenUrl(u); };
        Grid.SetColumn(get, 1); keyLine.Children.Add(get);
        _keyRow.Children.Add(keyLine);
        _urlRow.Children.Add(Label(L("x.ai.url", "Server address")));
        _urlRow.Children.Add(_url);

        var actions = new StackPanel { Spacing = 10, Margin = new Thickness(0, 6, 0, 0), Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _save, _busy } }, _status } };
        var form = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Field(L("x.ai.provider", "AI provider"), _provider), _urlRow, _keyRow,
                Field(L("x.ai.model", "Model (you can type another)"), _model),
                Field(L("x.ai.lang", "Answer language"), _lang),
                _hint, FallbackRow(), actions,
            },
        };
        if (!embedded)
            form.Children.Insert(0, new StackPanel
            {
                Spacing = 4, Margin = new Thickness(0, 0, 0, 6),
                Children =
                {
                    new TextBlock { Text = L("x.ai.setup_title", "Connect an AI"), FontSize = 20, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = L("x.ai.setup_text", "Paste an API key from Grok, OpenAI or Gemini, or use Ollama on this PC. You only do this once."), Foreground = p.SubBrush, TextWrapping = TextWrapping.Wrap },
                },
            });
        Child = form;

        var cur = Array.FindIndex(Ai.Providers, x => x.Id == App.Settings.AiProvider);
        _provider.SelectedIndex = cur < 0 ? 0 : cur;
        _lang.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, App.Settings.AiLanguage));
        LoadProvider();
        _provider.SelectionChanged += (_, _) => { if (!_loading) { App.Settings.AiProvider = SelectedId(); App.Settings.Save(); LoadProvider(); } };
        _model.GotFocus += (_, _) => { if (string.IsNullOrEmpty(_model.Text) || _model.ItemsSource is not null) _model.IsDropDownOpen = true; };
        _save.Click += async (_, _) => await SaveAsync();
        // a key says who it belongs to: pasting a gsk_… key switches to Groq, xai-… to Grok, AIza… to Gemini, sk-… to OpenAI
        _key.TextChanged += (_, _) =>
        {
            var k = _key.Text ?? "";
            if (Ai.Detect(k) is { } d && d.Id != SelectedId())
            {
                _provider.SelectedIndex = Array.FindIndex(Ai.Providers, x => x.Id == d.Id);   // reloading the form clears the box…
                _key.Text = k;                                                                    // …so the key goes back in
            }
        };
        _key.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SaveAsync(); };
    }

    /// <summary>"If this AI hits its limit, carry on with a backup": the same provider's other models, other connected AIs, then Ollama.</summary>
    private Control FallbackRow()
    {
        _fallback.Content = new StackPanel
        {
            Spacing = 2, Margin = new Thickness(4, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = L("x.ai.fallback", "Use a backup when the free limit runs out"), FontSize = 13.5 },
                new TextBlock { Text = L("x.ai.fallback_hint", "Mochi tries this AI's other models, then any other AI you connected, then Ollama, so the answer still comes."), FontSize = 12, Foreground = _p.SubBrush, TextWrapping = TextWrapping.Wrap },
            },
        };
        _fallback.IsCheckedChanged += (_, _) => { App.Settings.AiFallback = _fallback.IsChecked == true; App.Settings.Save(); };
        return _fallback;
    }

    private string SelectedId() => Ai.Providers[Math.Max(0, _provider.SelectedIndex)].Id;

    private static Control Label(string t) => new TextBlock { Text = t, FontWeight = FontWeight.SemiBold, FontSize = 13 };
    private static Control Field(string label, Control c) => new StackPanel { Spacing = 6, Children = { Label(label), c } };

    private void LoadProvider()
    {
        _loading = true;
        var p = Ai.Find(SelectedId());
        _keyRow.IsVisible = p.Online || p.Id == "custom";
        _urlRow.IsVisible = p.Id == "custom";
        _url.Text = App.Settings.AiBaseUrl;
        _key.Text = "";
        _key.Watermark = Ai.Key(p).Length > 0 ? L("x.ai.key_saved", "A key is saved (type a new one to replace it)") : L("x.ai.key_hint", "Paste your key here");
        var model = App.Settings.AiModels.TryGetValue(p.Id, out var m) ? m : "";
        _model.ItemsSource = new[] { p.DefaultModel }.Where(x => x.Length > 0).ToList();
        _model.Text = model.Length > 0 ? model : p.DefaultModel;
        _hint.Text = p.Id switch
        {
            "ollama" => L("x.ai.hint_ollama", "Runs on this PC: nothing leaves it. For pictures, install a vision model such as llama3.2-vision."),
            "custom" => L("x.ai.hint_custom", "Any server that speaks the OpenAI chat protocol (LM Studio, OpenRouter, Groq, your own)."),
            _ => L("x.ai.hint_online", "Your key is stored encrypted for your Windows account. Only the file you pick is sent, and only when you run an AI tool."),
        };
        _status.Text = "";
        _loading = false;
    }

    private void Say(string text, bool ok) { _status.Text = text; _status.Foreground = new SolidColorBrush(Color.Parse(ok ? "#34C759" : "#FF6B60")); }

    private async Task SaveAsync()
    {
        var p = Ai.Find(SelectedId());
        App.Settings.AiProvider = p.Id;
        App.Settings.AiLanguage = Languages[Math.Max(0, _lang.SelectedIndex)];
        if (p.Id == "custom") App.Settings.AiBaseUrl = (_url.Text ?? "").Trim();
        if ((_key.Text ?? "").Trim().Length > 0) Ai.SetKey(p, _key.Text!);
        Ai.SetModel(p, _model.Text ?? "");
        App.Settings.Save();

        if (!Ai.IsConfigured) { Say(p.Id == "custom" && Ai.BaseUrl(p).Length == 0 ? L("x.ai.need_url", "Enter the server address.") : L("x.ai.need_key", "Paste your API key first."), false); return; }

        _save.IsEnabled = false; _busy.IsVisible = true; Say(L("x.ai.testing", "Connecting…"), true); _status.Foreground = _p.SubBrush;
        try
        {
            var models = await Ai.ModelsAsync(p, CancellationToken.None);
            if (models.Count > 0)
            {
                _model.ItemsSource = models;
                Ai.RememberModels(p, models);
                // the user only brings a key: Mochi picks the best model for text and the best one that can see pictures
                var typed = (_model.Text ?? "").Trim();
                var keep = typed.Length > 0 && typed != p.DefaultModel && models.Contains(typed);
                var text = keep ? typed : Ai.Pick(p, models, false) ?? (models.Contains(Ai.Model(p)) ? Ai.Model(p) : models[0]);
                var vision = Ai.Pick(p, models, true) ?? text;
                if (p.Id == "ollama") text = Ai.Model(p);
                Ai.SetModel(p, text); Ai.SetModel(p, vision, true); _model.Text = text;
                Say(string.Format(L("x.ai.connected_auto", "Connected ✓  Text: {0}  ·  Pictures: {1}"), text, vision), true);
            }
            else Say(L("x.ai.connected_plain", "Connected ✓"), true);

            Ai.SeedUi();
            App.Vm?.ReloadGroups(); App.Vm?.RefreshPresets();
            _onSaved?.Invoke();
        }
        catch (AiException ex) { Say(ex.Message, false); }
        catch (Exception ex) { Say(ex.Message, false); }
        finally { _save.IsEnabled = true; _busy.IsVisible = false; }
    }
}

/// <summary>
/// What every AI window shares: the orb and title on top, a body that is swapped as the job moves along,
/// and the first-run steps (connect an AI, agree that the file is sent).
/// </summary>
public abstract class AiShell : Window
{
    protected static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    /// <summary>Raised when an AI window appears (ends the "Opening AI…" pill).</summary>
    public static event Action? FirstWindowOpened;

    protected readonly Pal P = Pal.Now();
    protected readonly AiOrb Orb = new() { Width = 46, Height = 46 };
    protected readonly TextBlock TitleText = new() { FontSize = 17, FontWeight = FontWeight.Bold };
    protected readonly TextBlock SubText = new() { FontSize = 12 };
    protected readonly ContentControl Body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    protected CancellationTokenSource Cts = new();

    protected AiShell(string title, double width, double height)
    {
        Title = "Mochi AI · " + title;
        Width = width; Height = height; MinWidth = 460; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(P.Dark ? Color.Parse("#131316") : Color.Parse("#F4F4F8"));
        Foreground = P.TextBrush;
        TitleText.Text = title; SubText.Foreground = P.SubBrush; SubText.Text = Ai.IsConfigured ? Ai.ModelLabel : "";

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, Margin = new Thickness(22, 18, 22, 10) };
        head.Children.Add(Orb);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { TitleText, SubText } };
        Grid.SetColumn(titles, 1); head.Children.Add(titles);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head); root.Children.Add(Body);
        Content = root;
        Closed += (_, _) => { try { Cts.Cancel(); } catch (ObjectDisposedException) { } };
        Opened += (_, _) =>
        {
            // never taller than the screen (title bar included), and centred on it
            if ((Screens.ScreenFromVisual(this) ?? Screens.Primary) is { } sc)
            {
                var k = sc.Scaling; const double Title = 40;

                Height = Math.Min(Height, sc.WorkingArea.Height / k - Title - 16);
                Position = new PixelPoint(sc.WorkingArea.X + (int)((sc.WorkingArea.Width - Width * k) / 2), sc.WorkingArea.Y + (int)((sc.WorkingArea.Height - (Height + Title) * k) / 2));
            }
            FirstWindowOpened?.Invoke();
            if (TryGetPlatformHandle()?.Handle is { } h) Native.ForceForeground(h);   // comes to the front even when started from the toolbar
            Activate();
        };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && !(FocusManager?.GetFocusedElement() is TextBox)) Close(); };
    }

    protected void SetSub(string text) => SubText.Text = text;

    /// <summary>Waits until an AI is connected and (for online providers) the user has agreed. False if the window was closed first.</summary>
    protected Task<bool> GateAsync()
    {
        if (Ai.IsConfigured && !Ai.NeedsConsent) { SetSub(Ai.ModelLabel); return Task.FromResult(true); }
        var tcs = new TaskCompletionSource<bool>();
        Closed += (_, _) => tcs.TrySetResult(false);
        Orb.Calm = true;
        void Step()
        {
            if (!Ai.IsConfigured) { Body.Content = Scroll(new AiSetupCard(P, false, Step)); return; }
            if (Ai.NeedsConsent) { Body.Content = Scroll(Consent(Step)); return; }
            SetSub(Ai.ModelLabel); Orb.Calm = false;
            tcs.TrySetResult(true);
        }
        Step();
        return tcs.Task;
    }

    private static Control Scroll(Control c) => new ScrollViewer { Content = new Border { Padding = new Thickness(22, 6, 22, 22), Child = c } };

    private Control Consent(Action next)
    {
        var allow = new Button { Content = L("x.ai.allow", "Allow and continue"), Classes = { "pill" }, Padding = new Thickness(22, 9) };
        var cancel = new Button { Content = L("common.cancel", "Cancel"), Padding = new Thickness(18, 9) };
        allow.Click += (_, _) => { App.Settings.AiConsent = true; App.Settings.Save(); next(); };
        cancel.Click += (_, _) => Close();
        return new Border
        {
            CornerRadius = new CornerRadius(18), Padding = new Thickness(24), Background = new SolidColorBrush(P.Text, 0.05), BorderBrush = P.BorderBrush, BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = L("x.ai.consent_title", "Before the first use"), FontSize = 20, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = string.Format(L("x.ai.consent_text", "To answer, Mochi sends the text or picture of the file you choose to {0}. Nothing is sent until you run an AI tool, and only for that file. For a fully offline option, choose Ollama in Settings → AI."), Ai.Provider.Name), TextWrapping = TextWrapping.Wrap, Foreground = P.SubBrush },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { allow, cancel } },
                },
            },
        };
    }

    /// <summary>A friendly error with the way forward: try again, or fix the connection.</summary>
    protected Control ErrorCard(string message, bool needsSetup, Action retry)
    {
        var again = new Button { Content = L("x.ai.retry", "Try again"), Classes = { "pill" }, Padding = new Thickness(18, 8) };
        again.Click += (_, _) => retry();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { again } };
        if (needsSetup)
        {
            var open = new Button { Content = L("x.ai.open_settings", "Open AI settings"), Padding = new Thickness(16, 8) };
            open.Click += (_, _) => { App.ShowPage("ai"); Close(); };
            row.Children.Add(open);
        }
        return new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Background = new SolidColorBrush(Color.Parse("#FF453A"), 0.12), BorderBrush = new SolidColorBrush(Color.Parse("#FF453A"), 0.4), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 10,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, row },
            },
        };
    }

    /// <summary>"Report.pdf" or "Report.pdf +2 more".</summary>
    protected static string FilesLine(IReadOnlyList<string> files) =>
        files.Count == 1 ? Path.GetFileName(files[0]) : Path.GetFileName(files[0]) + "  +" + (files.Count - 1);
}
