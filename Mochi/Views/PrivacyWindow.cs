using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mochi.Services;
using SkiaSharp;

namespace Mochi.Views;

/// <summary>One page being checked: the picture, what Mochi found on it, and the copy that is shown on screen.</summary>
public sealed class PrivacyPage
{
    public SKBitmap Bmp = null!;
    public WriteableBitmap View = null!;
    public List<Finding> Findings = new();
    public bool Scanned;
}

/// <summary>The picture with the boxes Privacy Guard found. Click a box to untick or tick it; drag on empty space to cover something it missed.</summary>
public sealed class PrivacyCanvas : Control
{
    private PrivacyPage? _page;
    private double _zoom = 1; private Point _off; private bool _userView;
    private Point _panFrom, _offFrom, _dragFrom; private bool _panning, _dragging; private Point _dragNow;

    public Action? Changed { get; set; }

    public PrivacyCanvas() { ClipToBounds = true; Focusable = true; }

    public void Show(PrivacyPage p) { _page = p; _userView = false; Fit(); }

    public void Fit()
    {
        if (_page is null || Bounds.Width < 10 || Bounds.Height < 10) return;
        _zoom = Math.Min((Bounds.Width - 24) / _page.Bmp.Width, (Bounds.Height - 24) / _page.Bmp.Height);
        _off = new Point((Bounds.Width - _page.Bmp.Width * _zoom) / 2, (Bounds.Height - _page.Bmp.Height * _zoom) / 2);
        _userView = false; InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); if (!_userView) Fit(); }

    private Point ToPic(Point p) => new((p.X - _off.X) / _zoom, (p.Y - _off.Y) / _zoom);

    private Finding? Hit(Point pic) =>
        _page?.Findings.Where(f => f.Rect.Contains((float)pic.X, (float)pic.Y)).OrderBy(f => f.Rect.Width * f.Rect.Height).FirstOrDefault();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        if (_page is null) return;
        var pt = e.GetCurrentPoint(this); var pos = e.GetPosition(this);
        if (pt.Properties.IsMiddleButtonPressed) { _panning = true; _panFrom = pos; _offFrom = _off; e.Pointer.Capture(this); return; }
        var pic = ToPic(pos);
        if (pt.Properties.IsRightButtonPressed)
        {
            if (Hit(pic) is { } f) { if (f.Kind == PrivacyKind.Manual) _page.Findings.Remove(f); else f.On = !f.On; Changed?.Invoke(); InvalidateVisual(); }
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        _dragging = true; _dragFrom = pic; _dragNow = pic;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_panning) { _off = _offFrom + (pos - _panFrom); _userView = true; InvalidateVisual(); }
        else if (_dragging) { _dragNow = ToPic(pos); InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_panning) { _panning = false; return; }
        if (!_dragging || _page is null) return;
        _dragging = false;
        var w = Math.Abs(_dragNow.X - _dragFrom.X) * _zoom; var h = Math.Abs(_dragNow.Y - _dragFrom.Y) * _zoom;
        if (w < 8 && h < 8)
        {
            if (Hit(_dragFrom) is { } f) f.On = !f.On;   // a click on a box ticks or unticks it
        }
        else
        {
            var r = new SKRect((float)Math.Min(_dragFrom.X, _dragNow.X), (float)Math.Min(_dragFrom.Y, _dragNow.Y), (float)Math.Max(_dragFrom.X, _dragNow.X), (float)Math.Max(_dragFrom.Y, _dragNow.Y));
            r.Left = Math.Max(0, r.Left); r.Top = Math.Max(0, r.Top); r.Right = Math.Min(_page.Bmp.Width, r.Right); r.Bottom = Math.Min(_page.Bmp.Height, r.Bottom);
            _page.Findings.Add(new Finding { Kind = PrivacyKind.Manual, Label = "Covered by you", Rect = r });
        }
        Changed?.Invoke(); InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this); var pic = ToPic(pos);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.18, e.Delta.Y), 0.03, 40);
        _off = new Point(pos.X - pic.X * _zoom, pos.Y - pic.Y * _zoom); _userView = true;
        InvalidateVisual(); e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        var area = new Rect(Bounds.Size);
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#141416")), area);
        if (_page is null) return;
        var rect = new Rect(_off.X, _off.Y, _page.Bmp.Width * _zoom, _page.Bmp.Height * _zoom);
        using (ctx.PushClip(rect.Intersect(area)))
        {
            RenderOptions.SetBitmapInterpolationMode(this, _zoom >= 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
            ctx.DrawImage(_page.View, new Rect(0, 0, _page.Bmp.Width, _page.Bmp.Height), rect);
            foreach (var f in _page.Findings)
            {
                var r = new Rect(_off.X + f.Rect.Left * _zoom, _off.Y + f.Rect.Top * _zoom, f.Rect.Width * _zoom, f.Rect.Height * _zoom);
                if (f.On)
                {
                    ctx.FillRectangle(new SolidColorBrush(Color.Parse(f.Sure ? "#F2000000" : "#D9000000")), r);
                    ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse(f.Sure ? "#FF453A" : "#FF9F0A")), 2), r);
                }
                else ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#8E8E93")) { }, 1.5, new DashStyle(new double[] { 4, 3 }, 0)), r);
            }
            if (_dragging)
            {
                var d = new Rect(_off.X + Math.Min(_dragFrom.X, _dragNow.X) * _zoom, _off.Y + Math.Min(_dragFrom.Y, _dragNow.Y) * _zoom, Math.Abs(_dragNow.X - _dragFrom.X) * _zoom, Math.Abs(_dragNow.Y - _dragFrom.Y) * _zoom);
                ctx.FillRectangle(new SolidColorBrush(Color.Parse("#55FF453A")), d);
                ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5), d);
            }
        }
    }
}

/// <summary>
/// Privacy Guard: Mochi marks what should not be shared (ID numbers, phone numbers, faces, QR codes, names and addresses), the user checks
/// the boxes, and a safe copy is saved with all of it covered in solid black and the hidden file details gone.
/// </summary>
public sealed class PrivacyWindow : Window
{
    private static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    private readonly string _path;
    private readonly List<PrivacyPage> _pages = new();
    private readonly PrivacyCanvas _canvas = new();
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly StackPanel _notes = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _save, _prev, _next;
    private int _cur;
    private bool _busy, _done;

    public string? Saved { get; private set; }

    public PrivacyWindow(string path)
    {
        _path = path;
        Title = L("x.pg.title", "Privacy Guard") + " — " + Path.GetFileName(path);
        Width = 1080; Height = 700; MinWidth = 860; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _canvas.Changed = RefreshList;
        _prev = new Button { Content = "‹" }; _next = new Button { Content = "›" };
        _prev.Click += (_, _) => Go(-1); _next.Click += (_, _) => Go(1);
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Children = { _prev, _pageLabel, _next } };

        _save = new Button { Content = L("x.pg.save", "Cover and save a safe copy"), Classes = { "pill" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false };
        _save.Click += (_, _) => { if (_done) Close(); else Save(); };
        var cancel = new Button { Content = L("common.cancel", "Cancel") };
        cancel.Click += (_, _) => Close();
        var fit = new Button { Content = L("x.bg.fit", "Fit") };
        fit.Click += (_, _) => _canvas.Fit();

        var panel = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10,
            Children =
            {
                new TextBlock { Text = L("x.pg.title", "Privacy Guard"), FontSize = 20, FontWeight = FontWeight.Bold },
                new TextBlock { Text = L("x.pg.local", "Everything happens on this PC. Nothing is uploaded."), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap },
                pager, _status, _notes,
                new TextBlock { Text = L("x.pg.found", "What Mochi found"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                _list,
                new TextBlock { Text = L("x.pg.hint", "Click a box to cover it or leave it. Drag on the picture to cover something Mochi missed. Right-click a box you drew to remove it. Wheel zooms, middle mouse pans. Orange boxes are guesses: they are covered unless you untick them."), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            },
        };
        var foot = new StackPanel { Margin = new Thickness(16, 10), Spacing = 8, Children = { _save, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { fit, cancel } } } };
        var side = new DockPanel { Width = 340 };
        DockPanel.SetDock(foot, Dock.Bottom);
        side.Children.Add(foot);
        side.Children.Add(new ScrollViewer { Content = panel });
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side); root.Children.Add(_canvas);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opened += async (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } sc)
            {
                Width = Math.Min(Width, sc.WorkingArea.Width / sc.Scaling - 40); Height = Math.Min(Height, sc.WorkingArea.Height / sc.Scaling - 40);
                Position = new PixelPoint(sc.WorkingArea.X + (int)((sc.WorkingArea.Width - Width * sc.Scaling) / 2), sc.WorkingArea.Y + (int)((sc.WorkingArea.Height - Height * sc.Scaling) / 2));
            }
            await Scan(pager);
        };
    }

    private static WriteableBitmap ToView(SKBitmap b)
    {
        var wb = new WriteableBitmap(new PixelSize(b.Width, b.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = wb.Lock();
        var bytes = b.Bytes;
        for (var y = 0; y < b.Height; y++) Marshal.Copy(bytes, y * b.Width * 4, fb.Address + y * fb.RowBytes, b.Width * 4);
        return wb;
    }

    private async Task Scan(StackPanel pager)
    {
        _busy = true; _status.Text = L("x.pg.reading", "Opening the file…");
        try
        {
            var bmps = await Task.Run(() => Privacy.LoadPages(_path));
            foreach (var b in bmps) _pages.Add(new PrivacyPage { Bmp = b, View = ToView(b) });
            pager.IsVisible = _pages.Count > 1;
            _canvas.Show(_pages[0]);
            for (var i = 0; i < _pages.Count; i++)
            {
                _status.Text = _pages.Count > 1 ? string.Format(L("x.pg.scanning_n", "Looking for private details… page {0} of {1}"), i + 1, _pages.Count) : L("x.pg.scanning", "Looking for private details…");
                var page = _pages[i];
                var scan = await Task.Run(() => Privacy.Detect(page.Bmp));
                page.Findings = scan.Findings; page.Scanned = true;
                foreach (var n in scan.Notes) AddNote(n);
                if (i == _cur) { _canvas.Show(page); RefreshList(); }
            }
            if (Privacy.HasHiddenInfo(_path)) AddNote(L("x.pg.meta", "Hidden details in the file (camera, date, location, author) will be removed."));
            var total = _pages.Sum(p => p.Findings.Count);
            _status.Text = total == 0
                ? L("x.pg.none", "Nothing private was found. Look at the picture yourself, and drag a box over anything Mochi missed.")
                : string.Format(L("x.pg.count", "{0} things will be covered. Look through them, then save."), _pages.Sum(p => p.Findings.Count(f => f.On)));
            _save.IsEnabled = true;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        _busy = false;
        RefreshList();
    }

    private void AddNote(string text) =>
        _notes.Children.Add(new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8), Background = new SolidColorBrush(Color.Parse("#26FF9F0A")), Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 } });

    private void Go(int d)
    {
        if (_pages.Count == 0) return;
        _cur = Math.Clamp(_cur + d, 0, _pages.Count - 1);
        _canvas.Show(_pages[_cur]); RefreshList();
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        _pageLabel.Text = string.Format(L("x.pg.page", "Page {0} of {1}"), _cur + 1, Math.Max(1, _pages.Count));
        _prev.IsEnabled = _cur > 0; _next.IsEnabled = _cur < _pages.Count - 1;
        if (_pages.Count == 0) return;
        var page = _pages[_cur];
        if (page.Scanned && page.Findings.Count == 0) _list.Children.Add(new TextBlock { Text = L("x.pg.nothing", "Nothing found on this page."), Classes = { "secondary" } });
        foreach (var f in page.Findings)
        {
            var box = new CheckBox { IsChecked = f.On, Content = f.Label + (f.Sure || f.Kind == PrivacyKind.Manual ? "" : "  (" + L("x.pg.guess", "check") + ")") };
            box.IsCheckedChanged += (_, _) => { f.On = box.IsChecked == true; _canvas.InvalidateVisual(); };
            _list.Children.Add(box);
        }
        _canvas.InvalidateVisual();
    }

    private async void Save()
    {
        if (_busy) return;
        _busy = true; _save.IsEnabled = false;
        _status.Text = L("x.pg.covering", "Covering and double-checking…");
        string? error = null; var extra = 0;
        try
        {
            var safe = await Task.Run(() =>
            {
                var list = new List<SKBitmap>();
                foreach (var p in _pages)
                {
                    var (bmp, more, _) = Privacy.CoverAndCheck(p.Bmp, p.Findings);
                    extra += more; list.Add(bmp);
                }
                return list;
            });
            Saved = await Task.Run(() => { var path = Privacy.Save(_path, safe); foreach (var b in safe) b.Dispose(); return path; });
        }
        catch (Exception ex) { error = ex.Message; }
        _busy = false;
        if (error is not null) { _status.Text = error; _save.IsEnabled = true; return; }
        _status.Text = (extra == 0
            ? L("x.pg.clean", "Saved. Mochi read the safe copy again and found nothing private left.")
            : string.Format(L("x.pg.extra", "Saved. The second look found {0} more thing(s) and covered them too."), extra))
            + "  " + Path.GetFileName(Saved);
        _save.Content = L("x.pg.close", "Close"); _save.IsEnabled = true; _done = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        foreach (var p in _pages) p.Bmp.Dispose();
    }

    public static Task<string?> RunAsync(string path)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new PrivacyWindow(path);
        w.Closed += (_, _) => tcs.TrySetResult(w.Saved);
        w.Show();
        return tcs.Task;
    }
}
