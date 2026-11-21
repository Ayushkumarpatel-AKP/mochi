using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Mochi.Services;
using Mochi.Services.Engines;
using Mochi.ViewModels;
using Mochi.Views;

namespace Mochi;

public partial class App : Application
{
    public static Settings Settings { get; private set; } = new();
    public static MainViewModel? Vm { get; private set; }
    public static bool Quitting { get; private set; }
    private static Window? _mainWin;
    public static Window? MainWindowRef => _mainWin;
    public static MainWindow? Main => _mainWin as MainWindow;

    /// <summary>Brings the main window forward on one of its pages (e.g. "ai").</summary>
    public static void ShowPage(string key)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Vm is null) return;
            Vm.Selected = Vm.Nav.FirstOrDefault(n => n.Key == key) ?? Vm.Selected;
            _mainWin?.Show(); _mainWin?.Activate();
        });
    }

    /// <summary>Raised when the window size (Ctrl + wheel, Ctrl +/-, or the Theme slider) changes.</summary>
    public static event Action? ScaleChanged;

    public static void SetScale(double v)
    {
        Settings.WindowScale = Math.Round(Math.Clamp(v, 0.7, 1.8), 2);
        Settings.Save();
        ScaleChanged?.Invoke();
    }

    // files sent to the donut within a moment of each other (Explorer opens one process per selected file) share one donut
    private readonly List<string> _donutFiles = new();
    private readonly Avalonia.Threading.DispatcherTimer _donutWait = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public void QueueDonut(IEnumerable<string> files)
    {
        _donutFiles.AddRange(files);
        _donutWait.Stop(); _donutWait.Start();
    }

    private NativeMenuItem? _trayUpdate;
    private Triggers? _triggers;
    private WatchService? _watch;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Settings only, for the command-line mode (no window, no theme).</summary>
    public static void InitHeadless() => Settings = Settings.Load();

    /// <summary>Errors in event handlers are logged to %APPDATA%\Mochi\crash.log and the app keeps running.</summary>
    private static void LogCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:s}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => { LogCrash(e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) LogCrash(ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };
        Settings = Settings.Load();
        Loc.Load(Settings.Language);
        Ai.SeedUi();
        ApplyTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            Vm = new MainViewModel();
            var win = new MainWindow { DataContext = Vm };
            _mainWin = win;
            ThemeEngine.Apply();   // again now that the window exists (glass needs it)
            if (Program.Background) d.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            else d.MainWindow = win;

            _watch = new WatchService(Vm);
            Vm.OnWatchChanged = _watch.Sync;
            _watch.Sync();
            _triggers = new Triggers(Vm);
            SetupTray(d, win);

            ToolHooks.Annotate = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => AnnotateWindow.RunAsync(p));
            Prompts.Hook = async (t, l, d, s) => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => PromptWindow.AskAsync(t, l, d, s));
            ToolHooks.SheetStudio = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => SheetStudioWindow.RunAsync(p));
            ToolHooks.Perspective = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => PerspectiveWindow.RunAsync(p));
            ToolHooks.RemoveBg = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => BgRemoveWindow.RunAsync(p));
            ToolHooks.Erase = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => EraseWindow.RunAsync(p));
            ToolHooks.Dupes = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => DupesWindow.RunAsync(p));
            SmartIndexService.Start();
            ToolHooks.SmartSearch = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => SmartSearchWindow.RunAsync(p));
            ToolHooks.Organize = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => OrganizeWindow.RunAsync(p));
            ToolHooks.Ai = async (mode, files) => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => AiLauncher.RunAsync(mode, files));
            ToolHooks.PdfEdit = async p => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => PdfEditorWindow.RunAsync(p));
            Vm.ShowWhatsNew = Guide.ShowWhatsNew;
            Vm.ShowTour = Guide.ShowTour;
            // the welcome tour for a new user, then each update's "What's new" pages once
            if (!Program.Background && Program.StartDonut.Count == 0 && Program.StartTool is null && Program.StartAi is null)
                Avalonia.Threading.Dispatcher.UIThread.Post(Guide.ShowAtStartup);

            if (Program.StartTool is { } st)
                Avalonia.Threading.Dispatcher.UIThread.Post(async () => { _ = st.Tool switch { "annotate" => await ToolHooks.Annotate!(st.File), "perspective" => await ToolHooks.Perspective!(st.File), "removebg" => await ToolHooks.RemoveBg!(st.File), "erase" => await ToolHooks.Erase!(st.File), "dupes" => await ToolHooks.Dupes!(st.File), "smartsearch" => await ToolHooks.SmartSearch!(st.File), "organize" => await ToolHooks.Organize!(st.File), "sheet" => await ToolHooks.SheetStudio!(st.File), _ => await ToolHooks.PdfEdit!(st.File) }; });
            if (Program.StartAi is { } ai)
                Avalonia.Threading.Dispatcher.UIThread.Post(async () => { _ = await ToolHooks.Ai!(ai.Mode, ai.Files); });
            if (Program.StartPage is { } page && Vm.Nav.FirstOrDefault(n => n.Key == page) is { } nav) Vm.Selected = nav;
            _donutWait.Tick += (_, _) =>
            {
                _donutWait.Stop();
                var files = _donutFiles.Distinct().ToList(); _donutFiles.Clear();
                if (files.Count > 0) Toolbar.Show(Vm!, files);
            };
            if (Program.StartDonut.Count > 0) QueueDonut(Program.StartDonut);

            SingleInstance.Listen(line => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (line.StartsWith("donut\t")) QueueDonut(line[6..].Split('\t', StringSplitOptions.RemoveEmptyEntries));
                else { win.Show(); win.Activate(); }
            }));

            // once a day, look for a newer release; the tray gets an "Update to x" entry when there is one
            if (Settings.CheckUpdates && DateTime.UtcNow - Settings.LastUpdateCheck > TimeSpan.FromHours(24))
                _ = Task.Run(async () =>
                {
                    var r = await Updater.CheckAsync();
                    Settings.LastUpdateCheck = DateTime.UtcNow; Settings.Save();
                    if (r.Newer) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_trayUpdate is { } m) { m.Header = Loc.T("tray.update", r.Version); m.IsVisible = true; } });
                });
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime d, Window win)
    {
        var open = new NativeMenuItem(Loc.T("tray.open"));
        open.Click += (_, _) => { win.Show(); win.Activate(); };
        var pause = new NativeMenuItem(Loc.T("tray.pause"));
        pause.Click += (_, _) =>
        {
            _triggers!.Paused = !_triggers.Paused;
            pause.Header = Loc.T(_triggers.Paused ? "tray.resume" : "tray.pause");
        };
        var quit = new NativeMenuItem(Loc.T("tray.quit"));
        quit.Click += (_, _) => { Quitting = true; d.Shutdown(); };

        Loc.Changed += () =>
        {
            open.Header = Loc.T("tray.open");
            pause.Header = Loc.T(_triggers!.Paused ? "tray.resume" : "tray.pause");
            quit.Header = Loc.T("tray.quit");
            if (TrayIcon.GetIcons(this) is { Count: > 0 } icons) icons[0].ToolTipText = Loc.T("tray.tooltip", Settings.TriggerModifier);
        };

        _trayUpdate = new NativeMenuItem("") { IsVisible = false };
        _trayUpdate.Click += (_, _) => { Vm!.Selected = Vm.Nav.First(n => n.Key == "about"); win.Show(); win.Activate(); };

        var menu = new NativeMenu();
        menu.Add(_trayUpdate); menu.Add(open); menu.Add(pause); menu.Add(new NativeMenuItemSeparator()); menu.Add(quit);

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Mochi/Assets/Icons/mochi.png"))),
            ToolTipText = Loc.T("tray.tooltip", Settings.TriggerModifier),
            Menu = menu,
        };
        tray.Clicked += (_, _) => { win.Show(); win.Activate(); };
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    public static void ApplyTheme()
    {
        var app = Current!;
        ThemeEngine.Apply();
    }
}
