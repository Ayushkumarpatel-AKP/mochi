using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

/// <summary>Colours for the toolbars, taken from the app's theme (light or dark) and accent colour.</summary>
public sealed class Pal
{
    public bool Dark { get; init; }
    public Color Card { get; init; }
    public Color Border { get; init; }
    public Color Text { get; init; }
    public Color Accent { get; init; }
    public IBrush CardBrush => new SolidColorBrush(Card);
    public IBrush BorderBrush => new SolidColorBrush(Border);
    public IBrush TextBrush => new SolidColorBrush(Text);
    public IBrush SubBrush => new SolidColorBrush(Text, 0.62);
    public IBrush Chip(double hot = 0) => new SolidColorBrush(Dark ? Colors.White : Colors.Black, 0.07 + hot);
    public IBrush Field => new SolidColorBrush(Dark ? Color.Parse("#0B0B0C") : Colors.White);

    public static Pal Now()
    {
        var t = ThemeEngine.Current;
        var dark = t.Dark;
        return new Pal
        {
            Dark = dark,
            Card = Color.FromArgb(dark ? (byte)0xF2 : (byte)0xF5, t.Card.R, t.Card.G, t.Card.B),
            Border = dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0),
            Text = t.Text,
            Accent = ThemeEngine.CurrentAccent,
        };
    }
}

/// <summary>Chooses the toolbar style, runs a tool with a result card, and remembers pins and recent tools.</summary>
public static class Toolbar
{
    public static readonly string[] Styles = { "panel", "dock", "command", "pad", "donut" };

    /// <param name="files">Files already known (picked, or selected in Explorer); null opens the toolbar to receive a drop.</param>
    public static void Show(MainViewModel vm, IReadOnlyList<string>? files = null, bool activate = false)
    {
        switch (App.Settings.ToolbarStyle)
        {
            case "dock": new DockBarWindow(vm, files, activate).Show(); break;
            case "command": new CommandBarWindow(vm, files, activate).Show(); break;
            case "pad": new TilePadWindow(vm, files, activate).Show(); break;
            case "donut":
                if (files is { Count: > 0 }) new DonutWindow(vm, files).Show(); else new DonutWindow(vm, activate: activate).Show();
                break;
            default: new QuickPanelWindow(vm, files, activate).Show(); break;
        }
    }

    /// <summary>
    /// Runs a tool on files from the donut or any toolbar. Files the tool cannot convert are left out and said so, instead of
    /// refusing the whole batch; only when none fit is there nothing to do.
    /// </summary>
    public static async Task RunAsync(MainViewModel vm, Preset p, IReadOnlyCollection<string> files)
    {
        try
        {
            var ok = files.Where(f => Converter.IsSupported(f, p)).ToList();
            var skipped = files.Count - ok.Count;
            DropLog.Write($"{p.Name}: {ok.Count} of {files.Count} file(s) can be converted ({DropLog.Short(files)})");
            if (ok.Count == 0)
            {
                if (files.Count == 0) return;
                var kinds = string.Join(", ", files.Select(f => Directory.Exists(f) ? "folder" : Util.Ext(f)).Distinct());
                var first = files.First();
                var msg = string.Format(Loc.TryGet("x.cant_convert") ?? "{0} can't convert {1} files.", p.DisplayName, kinds);
                Dispatcher.UIThread.Post(() => ToastWindow.Notify(new[] { new RecentItem(Path.GetFileName(first.TrimEnd('\\', '/')), first, msg, true, Array.Empty<string>()) }));
                return;
            }
            Remember(p.Id);
            var before = vm.Recent.FirstOrDefault();
            // files that live only in OneDrive (or another cloud folder) are downloaded when read: that can take a while, so say so
            var online = ok.Count(f => { try { return (File.GetAttributes(f) & (FileAttributes)0x00400000) != 0 || (File.GetAttributes(f) & FileAttributes.Offline) != 0; } catch { return false; } });
            if (online > 0) DropLog.Write($"{online} file(s) are online-only (cloud placeholders)");
            long inBytes = 0;
            foreach (var f in ok) { try { if (File.Exists(f)) inBytes += new FileInfo(f).Length; } catch { /* a size is only for the toast */ } }
            BusyToast.Begin(p, ok.Count, online > 0);
            try { await vm.ConvertAsync(ok, p); }
            finally { BusyToast.End(); }
            var n = p.IsMerge ? 1 : ok.Count;
            var done = vm.Recent.TakeWhile(r => !ReferenceEquals(r, before)).Take(n).ToList();   // a tool closed without saving adds nothing
            DropLog.Write($"{p.Name}: finished, {done.Count(d => !d.Failed)} ok, {done.Count(d => d.Failed)} failed");
            var note = skipped == 0 ? null : string.Format(Loc.TryGet("x.skipped") ?? "{0} file(s) skipped: {1} can't convert them.", skipped, p.DisplayName);
            if (done.Count > 0) Dispatcher.UIThread.Post(() => ToastWindow.Notify(done, note, inBytes));
        }
        catch (Exception ex)
        {
            // never leave the user guessing: say what went wrong
            DropLog.Write($"{p.Name}: failed: {ex}");
            BusyToast.End();
            var first = files.FirstOrDefault() ?? "";
            Dispatcher.UIThread.Post(() => ToastWindow.Notify(new[] { new RecentItem(Path.GetFileName(first.TrimEnd('\\', '/')), first, ex.Message, true, Array.Empty<string>()) }));
        }
    }

    /// <summary>The last tools used, newest first, for the Suggested row.</summary>
    public static void Remember(string id)
    {
        var r = App.Settings.RecentPresets;
        r.Remove(id); r.Insert(0, id);
        if (r.Count > 12) r.RemoveRange(12, r.Count - 12);
        App.Settings.Save();
    }

    public static List<string> Pins()
    {
        var s = App.Settings;
        if (!s.PinsSeeded) { s.PinnedPresets.AddRange(s.DonutIds.Where(i => !s.PinnedPresets.Contains(i))); s.PinsSeeded = true; s.Save(); }
        return s.PinnedPresets;
    }
}

/// <summary>
/// What every toolbar style shares: the files it is for, the tools that fit them, drops and clicks, placement near the pointer,
/// and closing. A style only decides how it looks.
/// </summary>
public abstract class ToolbarBase : Window
{
    protected static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    public sealed record Tool(Preset Preset, string Name, string Detail, string Glyph, IBrush Tint, string Category);

    /// <summary>True when it stays until the user acts (it took the focus); false for the hover style that closes by itself.</summary>
    public bool Persistent => Activated;

    protected readonly MainViewModel Vm;
    protected bool Activated { get; private set; }
    protected readonly Pal P = Pal.Now();
    protected readonly List<Tool> All;
    protected readonly TextBlock Headline = new() { FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    protected readonly TextBlock Sub = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    protected readonly StackPanel Thumbs = new() { Orientation = Orientation.Horizontal, Spacing = -10 };
    protected readonly DispatcherTimer Watcher = new() { Interval = TimeSpan.FromMilliseconds(100) };
    protected bool Picking;
    private DateTime _lastActive = DateTime.UtcNow;
    private IReadOnlyList<string> _files;
    private IReadOnlyList<string> _dragFiles = Array.Empty<string>();
    private string _dragKey = "";

    public static ToolbarBase? Current { get; private set; }

    /// <summary>Called when the files it is about change (a drag came over), so the style can rebuild.</summary>
    protected abstract void Refresh();
    protected abstract void BuildContent();
    protected virtual double Width0 => 392;
    protected virtual bool CenterTop => false;

    protected ToolbarBase(MainViewModel vm, IReadOnlyList<string>? files, bool activate)
    {
        Current?.Close();
        Current = this;
        Vm = vm; _files = files ?? Array.Empty<string>(); Activated = (files is { Count: > 0 }) || activate;
        All = Converter.Presets.Where(p => !App.Settings.DisabledFields.Contains(p.Category) && !Ai.Hidden(p)).Select(Make).ToList();

        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true; ShowInTaskbar = false; CanResize = false;
        ShowActivated = Activated;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Headline.Foreground = P.TextBrush; Sub.Foreground = P.SubBrush;
        TextElementHack();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { FilterFor(e); e.DragEffects = DragDropEffects.Copy; });
        // DragLeave also bubbles up from every tool the pointer slides off; only a pointer that really left the window resets things,
        // otherwise the tools would be rebuilt under the pointer at each boundary and the drop could land on a vanished button
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { if (_dragKey.Length > 0 && !CursorInside()) { _dragKey = ""; _dragFiles = Array.Empty<string>(); RefreshHeader(); Refresh(); } });
        // a drop anywhere that is not a tool: the files are taken, and the tools that fit them are shown, ready to search and pick
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            var dropped = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (dropped is not { Count: > 0 })
            {
                var kinds = string.Join(", ", e.Data.GetDataFormats());
                DropLog.Write("drop on the toolbar carried no files; formats: " + kinds);
                ToastWindow.Notify(new[] { new RecentItem(L("x.no_files_title", "Nothing to convert"), "", string.Format(L("x.no_files", "That drag did not carry files Mochi can read (it carried: {0}). Save the file to a folder first, then drag it from there."), kinds), true, Array.Empty<string>()) });
                return;
            }
            DropLog.Write($"dropped {DropLog.Short(dropped)} on the toolbar: choosing a tool next");
            Dispatcher.UIThread.Post(() => Adopt(dropped), DispatcherPriority.Background);
        });
        PointerMoved += (_, _) => _lastActive = DateTime.UtcNow;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) =>
        {
            if (!Activated || Picking) return;
            // waiting for a drop: the click that starts a drag in Explorer takes the focus, which must not close the toolbar
            if (_files.Count == 0 && Native.LeftDown()) { _awaitDrag = DateTime.UtcNow; return; }
            Close();
        };
        Closed += (_, _) => { Watcher.Stop(); if (Current == this) Current = null; };

        var cursor = Native.Cursor();
        Opened += (_, _) =>
        {
            Place(cursor);
            RefreshHeader(); Refresh();
            Dispatcher.UIThread.Post(() => Place(cursor), DispatcherPriority.Loaded);   // again once the real size is known
            if (Activated)
            {
                if (TryGetPlatformHandle()?.Handle is { } h) Native.ForceForeground(h);
                Activate(); OnActivated();
            }
            Watcher.Tick += (_, _) => Watch();
            Watcher.Start();
        };
        BuildContent();
    }

    private void TextElementHack() { }   // text colours are set on the controls themselves (see Wrap)

    protected virtual void OnActivated() { }

    /// <summary>Puts light or dark text on the content, whatever the app theme, by making it the card's foreground.</summary>
    protected Border Wrap(Control inner, double radius = 22, Thickness? padding = null) => new()
    {
        Margin = new Thickness(14), CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(14),
        Background = P.CardBrush, BorderBrush = P.BorderBrush, BorderThickness = new Thickness(1),
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 34, OffsetY = 10, Color = Color.FromArgb(P.Dark ? (byte)150 : (byte)70, 0, 0, 0) }),
        Child = new ContentControl { Foreground = P.TextBrush, Content = inner },
    };

    // ---------------------------------------------------------------- files and tools

    protected IReadOnlyList<string> Files => _dragFiles.Count > 0 ? _dragFiles : _files;
    protected bool HasKnownFiles => _files.Count > 0;
    protected IReadOnlyList<string> KnownFiles => _files;

    private static Tool Make(Preset p)
    {
        var (glyph, tint) = Look.Of(p.Category);
        var detail = p.Extension.Length == 0 ? Loc.T("format.category." + p.Category) : "→ " + p.Extension;
        return new Tool(p, p.DisplayName, detail, glyph, tint, p.Category);
    }

    protected bool Fits(Tool t) => Files.Count == 0 || Files.DistinctBy(x => Util.Ext(x)).Any(x => Converter.IsSupported(x, t.Preset));   // some are enough: the rest are skipped and reported
    protected List<Tool> Usable() => All.Where(Fits).ToList();

    /// <summary>Pins first, then the donut for this kind of file, then recent tools; filled up from the rest.</summary>
    protected List<Tool> Suggested(int n)
    {
        var usable = Usable();
        var profile = Files.Count > 0 ? MainViewModel.ProfileFor(Files) : null;
        var order = Toolbar.Pins().Concat(profile?.PresetIds ?? new()).Concat(App.Settings.RecentPresets).Concat(profile?.BackPresetIds ?? new()).Distinct().ToList();
        var list = order.Select(id => usable.FirstOrDefault(t => t.Preset.Id == id)).OfType<Tool>().Take(n).ToList();
        if (list.Count < n && Files.Count > 0) list.AddRange(usable.Where(t => !list.Contains(t)).Take(n - list.Count));
        // three AI tools lead the list whenever an AI is connected and they fit the files, like the AI slice of the donut
        var ai = AiTools().Take(3).ToList();
        if (ai.Count > 0) { list.RemoveAll(ai.Contains); list.InsertRange(0, ai); if (list.Count > n) list.RemoveRange(n, list.Count - n); }
        return list;
    }

    /// <summary>The AI tools that fit the files, in the order the donut's AI slice shows them.</summary>
    protected List<Tool> AiTools() => new[] { "ai-summary", "ai-ask", "ai-explain", "ai-rename", "ai-command" }
        .Select(id => Usable().FirstOrDefault(t => t.Preset.Id == id)).OfType<Tool>().ToList();

    /// <summary>The "✦ Ask AI" row under search results: the typed words become the request for "Do it by typing".</summary>
    protected Control? AskAiRow(string q)
    {
        q = q.Trim();
        if (q.Length < 3 || All.FirstOrDefault(t => t.Preset.Id == "ai-command") is not { } tool) return null;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        grid.Children.Add(Icon(tool, 32));
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = L("x.ai.ask_ai", "Ask AI") + ":  “" + q + "”", FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = L("x.ai.ask_ai_hint", "Mochi picks and chains the tools, you check the plan first"), FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var b = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(12), Background = P.Chip(), Margin = new Thickness(0, 4, 0, 0) };
        b.Click += (_, _) => RunAskAi(q, tool);
        return b;
    }

    protected void RunAskAi(string q, Tool tool)
    {
        AiCommandWindow.PendingPrompt = q;
        _ = ClickedAsync(tool);
    }

    protected bool CanAskAi(string q) => q.Trim().Length >= 3 && All.Any(t => t.Preset.Id == "ai-command");

    protected static bool Matches(Tool t, string q)
    {
        var s = (t.Name + " " + t.Preset.Id + " " + t.Preset.Extension + " " + Loc.T("format.category." + t.Category) + " " + t.Preset.Mode).ToLowerInvariant();
        return q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => s.Contains(w));
    }

    protected List<Tool> Search(string q, int n)
    {
        var l = q.ToLowerInvariant();
        return Usable().Where(t => Matches(t, q))
            .OrderByDescending(t => (t.Name.ToLowerInvariant().StartsWith(l) ? 100 : t.Name.ToLowerInvariant().Contains(l) ? 60 : 20) + (Toolbar.Pins().Contains(t.Preset.Id) ? 10 : 0))
            .Take(n).ToList();
    }

    /// <summary>Shows what fits these files before the pointer has even reached the toolbar (the trigger knows what Explorer has selected).</summary>
    public void Suggest(IReadOnlyList<string> files)
    {
        if (_files.Count > 0 || files.Count == 0) return;
        var key = string.Join('|', files);
        if (key == _dragKey) return;
        _dragKey = key; _dragFiles = files;
        RefreshHeader(); Refresh();
    }

    /// <summary>Files dropped on the toolbar itself: they become its files, it keeps the focus, and the search box is ready.</summary>
    private void Adopt(IReadOnlyList<string> files)
    {
        _files = files; _dragFiles = Array.Empty<string>(); _dragKey = "";
        _awaitDrag = null; Activated = true;
        if (TryGetPlatformHandle()?.Handle is { } h) Native.ForceForeground(h);
        Activate();
        RefreshHeader(); Refresh(); OnActivated();
    }

    private bool CursorInside()
    {
        var c = Native.Cursor(); var k = RenderScaling;
        return c.X >= Position.X && c.Y >= Position.Y && c.X <= Position.X + (int)(Bounds.Width * k) && c.Y <= Position.Y + (int)(Bounds.Height * k);
    }

    private void FilterFor(DragEventArgs e)
    {
        if (_files.Count > 0) return;
        var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files is not { Count: > 0 }) return;
        var key = string.Join('|', files);
        if (key == _dragKey) return;
        _dragKey = key; _dragFiles = files;
        RefreshHeader(); Refresh();
    }

    // ---------------------------------------------------------------- the header: what files this is about

    protected Control Header()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        g.Children.Add(Thumbs);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Headline, Sub } };
        Grid.SetColumn(titles, 1); g.Children.Add(titles);
        return g;
    }

    protected void RefreshHeader()
    {
        var f = Files;
        Thumbs.Children.Clear();
        if (f.Count == 0)
        {
            Headline.Text = L("x.qp.nofiles", "No files yet");
            Sub.Text = L("x.qp.nofiles_hint", "Drop files anywhere here, or on a tool. Or click a tool to choose files.");
            Thumbs.Children.Add(Tile("＋", "#8E8E93", "#5C5C60", 46));
            return;
        }
        var kinds = f.Select(x => Directory.Exists(x) ? L("x.qp.folder", "folder") : Util.Ext(x).TrimStart('.').ToUpperInvariant()).Distinct().Take(4);
        Headline.Text = f.Count == 1 ? Path.GetFileName(f[0].TrimEnd('\\', '/')) : string.Format(L("x.qp.n_files", "{0} files"), f.Count);
        Sub.Text = string.Join(" · ", kinds);
        foreach (var x in f.Take(3)) Thumbs.Children.Add(Thumb(x, 46));
    }

    protected static Control Tile(string glyph, string a, string b, double size)
    {
        IBrush tint = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
        };
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = tint, Child = new TextBlock { Text = glyph, FontSize = size * 0.43, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    }

    protected Control Thumb(string path, double size)
    {
        var ext = Util.Ext(path);
        var kind = Directory.Exists(path) ? "Archive" : FileIndex.KindOf(ext);
        var (glyph, tint) = Look.Of(kind);
        var box = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = tint, ClipToBounds = true, BorderBrush = P.CardBrush, BorderThickness = new Thickness(2) };
        var img = new Image { Stretch = Stretch.UniformToFill };
        box.Child = new Grid { Children = { new TextBlock { Text = glyph, FontSize = size * 0.43, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, img } };
        if (kind == "Image" && ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
            _ = Task.Run(() =>
            {
                try { using var fs = File.OpenRead(path); return Bitmap.DecodeToWidth(fs, 120, BitmapInterpolationMode.MediumQuality); } catch { return null; }
            }).ContinueWith(t => { if (t.Result is { } b) Dispatcher.UIThread.Post(() => img.Source = b); });
        return box;
    }

    protected static Control Icon(Tool t, double size) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Background = t.Tint,
        Child = new TextBlock { Text = t.Glyph, FontSize = size * 0.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    // ---------------------------------------------------------------- clicking and dropping

    /// <summary>The star that pins a tool to the front of every toolbar, so a tool found by searching can be kept.</summary>
    protected Button PinStar(Tool t)
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
        return star;
    }

    /// <summary>A click runs the tool (or asks for files first); a drop of files runs it on those files.</summary>
    protected void Wire(Button b, Tool t)
    {
        b.Click += async (_, _) => await ClickedAsync(t);
        DragDrop.SetAllowDrop(b, true);
        var rest = b.Background; var restBorder = b.BorderBrush; var restThick = b.BorderThickness;
        void Hot(bool on)
        {
            b.Background = on ? new SolidColorBrush(P.Accent, 0.28) : rest;
            b.BorderBrush = on ? new SolidColorBrush(P.Accent) : restBorder;
            b.BorderThickness = on ? new Thickness(2) : restThick;
        }
        b.AddHandler(DragDrop.DragEnterEvent, (_, _) => Hot(true));
        b.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Hot(false));
        b.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        // right-click a tool to pin it to (or unpin it from) the front of the toolbar
        var pinned = Toolbar.Pins().Contains(t.Preset.Id);
        var pin = new MenuItem { Header = pinned ? L("x.qp.unpin", "Unpin") : L("x.qp.pin", "Pin to the top") };
        pin.Click += (_, _) => { var pins = Toolbar.Pins(); if (!pins.Remove(t.Preset.Id)) pins.Add(t.Preset.Id); App.Settings.Save(); Refresh(); };
        var menu = new ContextMenu { Items = { pin } };
        menu.Opening += (_, _) => Picking = true;
        menu.Closed += (_, _) => Picking = false;
        b.ContextMenu = menu;
        b.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Hot(false);
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (files is not { Count: > 0 })
            {
                var kinds = string.Join(", ", e.Data.GetDataFormats());
                DropLog.Write("drop on a tool carried no files; formats: " + kinds);
                e.Handled = true;
                ToastWindow.Notify(new[] { new RecentItem(L("x.no_files_title", "Nothing to convert"), "", string.Format(L("x.no_files", "That drag did not carry files Mochi can read (it carried: {0}). Save the file to a folder first, then drag it from there."), kinds), true, Array.Empty<string>()) });
                return;
            }
            DropLog.Write($"dropped {DropLog.Short(files)} on {t.Name}");
            e.Handled = true;
            // let the drop finish (Explorer is waiting on it) before this window closes and the work starts
            Dispatcher.UIThread.Post(() => Start(t, files), DispatcherPriority.Background);
        });
    }

    protected async Task ClickedAsync(Tool t)
    {
        if (_files.Count > 0) { Start(t, _files); return; }
        Picking = true;   // choosing files must not count as clicking away
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = t.Name });
        Picking = false;
        var files = picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files.Count == 0) return;
        if (!files.Any(f => Converter.IsSupported(f, t.Preset)))
        {
            var kinds = string.Join(", ", files.Select(f => Util.Ext(f)).Distinct());
            Close();
            ToastWindow.Notify(new[] { new RecentItem(Path.GetFileName(files[0]), files[0], string.Format(L("x.cant_convert", "{0} can't convert {1} files."), t.Name, kinds), true, Array.Empty<string>()) });
            return;
        }
        Start(t, files);
    }

    protected void Start(Tool t, IReadOnlyList<string> files)
    {
        var vm = Vm;
        Close();
        _ = Toolbar.RunAsync(vm, t.Preset, files);
    }

    // ---------------------------------------------------------------- placing and closing

    private void Place(PixelPoint cursor)
    {
        var sc = Screens.ScreenFromPoint(cursor) ?? Screens.Primary;
        if (sc is null) return;
        var k = sc.Scaling; var area = sc.WorkingArea;
        var w = (int)(Math.Max(Bounds.Width, Width0) * k); var h = (int)(Math.Max(Bounds.Height, 120) * k);
        if (CenterTop)
        {
            Position = new PixelPoint(area.X + (area.Width - w) / 2, area.Y + (int)(area.Height * 0.16));
            return;
        }
        var x = Math.Clamp(cursor.X - w / 2, area.X + 8, Math.Max(area.X + 8, area.Right - w - 8));
        var y = cursor.Y + (int)(18 * k) + h > area.Bottom ? Math.Max(area.Y + 8, cursor.Y - h - (int)(18 * k)) : cursor.Y - (int)(10 * k);
        Position = new PixelPoint(x, Math.Clamp(y, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - h - 8)));
    }

    private DateTime? _awaitDrag;

    private void Watch()
    {
        if (Native.LeftDown() || Picking) { _lastActive = DateTime.UtcNow; return; }
        if (_awaitDrag is { } since)
        {
            if (_dragKey.Length > 0) _awaitDrag = null;                                          // the drag arrived
            else if ((DateTime.UtcNow - since).TotalSeconds > 2) { Close(); return; }            // it was only a click
            else return;
        }
        var idle = (DateTime.UtcNow - _lastActive).TotalSeconds > (_files.Count > 0 ? 25 : 10);
        if (Activated) { if (idle && !IsKeyboardFocusWithin) Close(); return; }   // clicking elsewhere closes it through Deactivated
        // a drag in progress keeps the focus where it was: go away when the pointer leaves, or after a quiet while
        var c = Native.Cursor(); var k = RenderScaling; var m = (int)(60 * k);
        var away = c.X < Position.X - m || c.Y < Position.Y - m || c.X > Position.X + (int)(Bounds.Width * k) + m || c.Y > Position.Y + (int)(Bounds.Height * k) + m;
        if (away || idle) Close();
    }
}
