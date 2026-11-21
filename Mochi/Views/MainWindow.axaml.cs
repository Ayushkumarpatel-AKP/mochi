using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

public partial class MainWindow : Window
{
    private LayoutTransformControl? _scaler;

    /// <summary>Which sticker sits next to each page's title.</summary>
    private static readonly Dictionary<string, (string Name, MascotMood Mood)> PageMascots = new()
    {
        ["home"] = ("hello", MascotMood.Hop), ["presets"] = ("convert", MascotMood.Hop), ["smart"] = ("search", MascotMood.Float),
        ["ai"] = ("idea", MascotMood.Hop), ["engines"] = ("gear", MascotMood.Float), ["watch"] = ("folder", MascotMood.Float),
        ["triggers"] = ("donut", MascotMood.Hop), ["toolbar"] = ("laptop", MascotMood.Float), ["fields"] = ("doc", MascotMood.Float),
        ["general"] = ("cloud", MascotMood.Float), ["theme"] = ("cool", MascotMood.Hop), ["about"] = ("cheer", MascotMood.Hop),
    };

    /// <summary>Puts the right Mochi next to the page title and swaps it (with a pop) when the page changes.</summary>
    private void SetupPageMascot()
    {
        var m = this.FindControl<MascotView>("PageMascot");
        if (m is null) return;
        void Apply(MainViewModel vm)
        {
            var key = vm.Selected?.Key ?? "home";
            var (name, mood) = PageMascots.TryGetValue(key, out var v) ? v : ("hello", MascotMood.Hop);
            m.Mood = mood;
            if (m.Mascot != name) m.Mascot = name;
        }
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            Apply(vm);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Selected)) Apply(vm); };
        };
    }

    public MainWindow()
    {
        InitializeComponent();

        // Window size: everything in the window scales together (Ctrl + wheel, Ctrl +/-, Ctrl 0, or the Theme slider)
        if (Content is Control inner)
        {
            Content = null;
            _scaler = new LayoutTransformControl { Child = inner };
            Content = _scaler;
            ApplyScale();
            App.ScaleChanged += ApplyScale;
        }

        SetupPageMascot();

        var zone = this.FindControl<Border>("DropZone")!;
        zone.AddHandler(DragDrop.DropEvent, OnDrop);
        zone.DoubleTapped += async (_, _) => await PickAndConvertAsync();

        this.FindControl<Button>("DonutBtn")!.Click += async (_, _) =>
        {
            var files = await PickAsync();
            if (files.Count > 0 && DataContext is MainViewModel vm) Toolbar.Show(vm, files, true);
        };
        this.FindControl<Button>("PreviewDonutBtn")!.Click += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            // the donut previews the face being edited; every other style opens as it does on the shortcut
            if (App.Settings.ToolbarStyle == "donut") new DonutWindow(vm, null, vm.FrontIds.ToList(), vm.BackIds.ToList()).Show();
            else Toolbar.Show(vm, null, true);
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel m) return;
            m.PickExe = async () =>
            {
                var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    FileTypeFilter = new[] { new FilePickerFileType("Programs") { Patterns = new[] { "*.exe" } } },
                });
                return r.FirstOrDefault()?.TryGetLocalPath();
            };
            m.PickFolder = async () =>
            {
                var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
                return d.FirstOrDefault()?.TryGetLocalPath();
            };
        };
        SetupDonutPreview();
        SetupAiPage();
        this.FindControl<Button>("SmartSearchBtn")!.Click += async (_, _) =>
        {
            var folders = SmartIndexService.Folders();
            if (folders.Count == 0)
            {
                var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
                if (d.FirstOrDefault()?.TryGetLocalPath() is { } one) { (DataContext as MainViewModel)?.AddSearchFolder(one); folders = new() { one }; }
                else return;
            }
            SmartSearchWindow.RunAsync(folders);
        };
        this.FindControl<Button>("SmartSearchOtherBtn")!.Click += async (_, _) =>
        {
            var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            if (d.FirstOrDefault()?.TryGetLocalPath() is { } f) SmartSearchWindow.RunAsync(f);
        };
        this.FindControl<Button>("AddSearchFolderBtn")!.Click += async (_, _) =>
        {
            var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            if (d.FirstOrDefault()?.TryGetLocalPath() is { } f) (DataContext as MainViewModel)?.AddSearchFolder(f);
        };
        this.FindControl<Button>("OrganizeBtn")!.Click += async (_, _) =>
        {
            var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            if (d.FirstOrDefault()?.TryGetLocalPath() is { } f) OrganizeWindow.RunAsync(f);
        };
        this.FindControl<Button>("AddWatchBtn")!.Click += async (_, _) =>
        {
            var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
            var path = d.FirstOrDefault()?.TryGetLocalPath();
            if (path is not null && DataContext is MainViewModel vm) vm.AddWatch(path);
        };

        AddHandler(KeyDownEvent, OnScaleKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnScaleWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void ApplyScale()
    {
        if (_scaler is null) return;
        var s = App.Settings.WindowScale <= 0 ? 1 : App.Settings.WindowScale;
        _scaler.LayoutTransform = new ScaleTransform(s, s);
    }

    private void OnScaleKey(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var s = App.Settings.WindowScale;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: App.SetScale(s + 0.1); break;
            case Key.OemMinus or Key.Subtract: App.SetScale(s - 0.1); break;
            case Key.D0 or Key.NumPad0: App.SetScale(1); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnScaleWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        App.SetScale(App.Settings.WindowScale + (e.Delta.Y > 0 ? 0.1 : -0.1));
        e.Handled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Keep the shortcuts, watchers and tray alive: closing only hides the window.
        if (App.Settings.CloseToTray && !e.IsProgrammatic && App.Quitting == false)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files is { Count: > 0 } && DataContext is MainViewModel vm) await vm.ConvertAsync(files);
    }

    private async Task<List<string>> PickAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
        return picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    private async Task PickAndConvertAsync()
    {
        var files = await PickAsync();
        if (files.Count > 0 && DataContext is MainViewModel vm) await vm.ConvertAsync(files);
    }

    /// <summary>The AI page: the connect form is rebuilt each time the page is opened, so it shows what the AI windows saved.</summary>
    private void SetupAiPage()
    {
        var host = this.FindControl<Border>("AiSetupHost")!;
        host.Child = new AiSetupCard(Pal.Now(), true);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsAi) && vm.IsAi) host.Child = new AiSetupCard(Pal.Now(), true);
            };
        };
    }

    /// <summary>The live donut on the Presets page: shows the face being edited and takes a slice out when clicked.</summary>
    private void SetupDonutPreview()
    {
        var host = this.FindControl<Border>("DonutPreviewHost")!;
        var donut = new DonutControl { Width = 440, Height = 440, Hole = Loc.T("x.donut_click") }; // room for the fanned-out group ring (Outer + Band), so it is not clipped;
        host.Child = new Viewbox { Stretch = Stretch.Uniform, Child = donut };
        var styleHost = this.FindControl<Border>("StylePreviewHost")!;

        // wide: the preview sits beside the editor; narrow: it drops underneath
        var grid = this.FindControl<Grid>("EditorGrid")!;
        var pane = this.FindControl<Border>("PreviewPane")!;
        grid.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 700;
            Grid.SetColumn(pane, narrow ? 0 : 1);
            Grid.SetRow(pane, narrow ? 1 : 0);
        };
        var pending = false;

        void Sync()
        {
            pending = false;
            if (DataContext is not MainViewModel vm) return;
            var classic = App.Settings.ToolbarStyle == "donut";
            host.IsVisible = classic; styleHost.IsVisible = !classic;
            if (!classic)
            {
                // the other styles show the same presets (this face of this donut) the way that toolbar lays them out
                styleHost.Child = StylePreview.Build(App.Settings.ToolbarStyle, vm.CurrentIds.Select(Converter.Find).OfType<Preset>());
                return;
            }
            donut.Front = DonutWindow.Build(vm.FrontIds);
            donut.Back = DonutWindow.Build(vm.BackIds);
            donut.SetFace(vm.FaceIndex == 1);
        }

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.ToolbarStyleIndex) or nameof(MainViewModel.FaceIndex) or nameof(MainViewModel.ProfileIndex)) Sync();
            };
            donut.Clicked = p => vm.RemoveFromDonut(p.Id);
            // the list is cleared and refilled on every change: redraw once afterwards
            vm.DonutList.CollectionChanged += (_, _) =>
            {
                if (pending) return;
                pending = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(Sync, Avalonia.Threading.DispatcherPriority.Background);
            };
            Sync();
        };
    }
}
