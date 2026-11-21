using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>The picture being cleaned: pixels, the red marks the user painted, and what each erase changed (for undo).</summary>
public sealed class EraseSession
{
    public readonly byte[] Px;
    public readonly int W, H;
    public readonly byte[] Mask;
    public readonly WriteableBitmap Bitmap;
    private readonly byte[] _prev;
    private readonly List<(int X, int Y, int W, int H, byte[] Old)> _undo = new();

    public EraseSession(byte[] px, int w, int h)
    {
        Px = px; W = w; H = h; Mask = new byte[w * h]; _prev = new byte[w * h * 4];
        Bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Repaint(0, 0, w, h);
    }

    public bool HasMarks => Mask.Any(m => m > 0);
    public bool CanUndo => _undo.Count > 0;

    public void Repaint(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(W, x1); y1 = Math.Min(H, y1);
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                int i = y * W + x, s = i * 4;
                float a = Px[s + 3] / 255f, b = Px[s] * a, g = Px[s + 1] * a, r = Px[s + 2] * a;
                if (Mask[i] > 0) { r = r * 0.55f + 255 * 0.45f * a; g *= 0.55f; b *= 0.55f; }   // marked: tinted red
                _prev[s] = (byte)b; _prev[s + 1] = (byte)g; _prev[s + 2] = (byte)r; _prev[s + 3] = Px[s + 3];
            }
        using var fb = Bitmap.Lock();
        for (var y = y0; y < y1; y++) Marshal.Copy(_prev, (y * W + x0) * 4, fb.Address + y * fb.RowBytes + x0 * 4, (x1 - x0) * 4);
    }

    public void Paint(double cx, double cy, double r, bool add)
    {
        int x0 = Math.Max(0, (int)(cx - r)), x1 = Math.Min(W - 1, (int)Math.Ceiling(cx + r)), y0 = Math.Max(0, (int)(cy - r)), y1 = Math.Min(H - 1, (int)Math.Ceiling(cy + r));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if ((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) <= r * r) Mask[y * W + x] = add ? (byte)255 : (byte)0;
        Repaint(x0, y0, x1 + 1, y1 + 1);
    }

    public void ClearMarks() { Array.Clear(Mask); Repaint(0, 0, W, H); }

    /// <summary>Fills the marked area from its surroundings; remembers the area so Undo can put it back.</summary>
    public void Erase()
    {
        int minX = W, minY = H, maxX = -1, maxY = -1;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (Mask[y * W + x] > 0) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX < 0) return;
        var margin = Math.Max(24, Math.Max(maxX - minX, maxY - minY) / 2);
        int x0 = Math.Max(0, minX - margin), y0 = Math.Max(0, minY - margin), x1 = Math.Min(W, maxX + margin + 1), y1 = Math.Min(H, maxY + margin + 1);
        var old = new byte[(x1 - x0) * (y1 - y0) * 4];
        for (var y = y0; y < y1; y++) Buffer.BlockCopy(Px, (y * W + x0) * 4, old, (y - y0) * (x1 - x0) * 4, (x1 - x0) * 4);
        _undo.Add((x0, y0, x1 - x0, y1 - y0, old));
        if (_undo.Count > 12) _undo.RemoveAt(0);

        AiTools.Inpaint(Px, W, H, Mask);
        Array.Clear(Mask);
        Repaint(x0, y0, x1, y1);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (x, y, w, h, old) = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        for (var r = 0; r < h; r++) Buffer.BlockCopy(old, r * w * 4, Px, ((y + r) * W + x) * 4, w * 4);
        Repaint(x, y, x + w, y + h);
    }
}

/// <summary>The picture with a zoom and pan view; the left button paints marks, the right button rubs them out, the middle button pans.</summary>
public sealed class EraseCanvas : Control
{
    private static readonly IBrush Checker = new DrawingBrush
    {
        TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#FFFFFF")), Geometry = Geometry.Parse("M0,0 H16 V16 H0 Z") },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#DCDCE1")), Geometry = Geometry.Parse("M0,0 H8 V8 H0 Z M8,8 H16 V16 H8 Z") },
            },
        },
    };

    private readonly EraseSession _s;
    private double _zoom = 1; private Point _off; private bool _userView;
    private Point _mouse, _last, _panFrom, _offFrom; private bool _inside, _painting, _panning, _adding;

    public double Brush { get; set; } = 40;   // diameter in picture pixels
    public Action? Marked { get; set; }

    public EraseCanvas(EraseSession s) { _s = s; ClipToBounds = true; Focusable = true; }

    public void Fit()
    {
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        _zoom = Math.Min((Bounds.Width - 24) / _s.W, (Bounds.Height - 24) / _s.H);
        _off = new Point((Bounds.Width - _s.W * _zoom) / 2, (Bounds.Height - _s.H * _zoom) / 2);
        _userView = false; InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); if (!_userView) Fit(); }

    private Point ToPic(Point p) => new((p.X - _off.X) / _zoom, (p.Y - _off.Y) / _zoom);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var pt = e.GetCurrentPoint(this); var pos = e.GetPosition(this);
        if (pt.Properties.IsMiddleButtonPressed) { _panning = true; _panFrom = pos; _offFrom = _off; e.Pointer.Capture(this); return; }
        if (!pt.Properties.IsLeftButtonPressed && !pt.Properties.IsRightButtonPressed) return;
        _adding = pt.Properties.IsLeftButtonPressed; _painting = true; _last = ToPic(pos);
        e.Pointer.Capture(this);
        _s.Paint(_last.X, _last.Y, Brush / 2, _adding); Marked?.Invoke(); InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this); _mouse = pos; _inside = true;
        if (_panning) { _off = _offFrom + (pos - _panFrom); _userView = true; }
        else if (_painting)
        {
            var pic = ToPic(pos); var r = Brush / 2;
            var dist = Math.Sqrt((pic.X - _last.X) * (pic.X - _last.X) + (pic.Y - _last.Y) * (pic.Y - _last.Y));
            var n = Math.Max(1, (int)(dist / Math.Max(1, r * 0.25)));
            for (var k = 1; k <= n; k++) _s.Paint(_last.X + (pic.X - _last.X) * k / n, _last.Y + (pic.Y - _last.Y) * k / n, r, _adding);
            _last = pic; Marked?.Invoke();
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _painting = _panning = false; e.Pointer.Capture(null); }
    protected override void OnPointerExited(PointerEventArgs e) { _inside = false; InvalidateVisual(); }

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
        var rect = new Rect(_off.X, _off.Y, _s.W * _zoom, _s.H * _zoom);
        using (ctx.PushClip(rect.Intersect(area)))
        {
            ctx.FillRectangle(Checker, rect);
            RenderOptions.SetBitmapInterpolationMode(this, _zoom >= 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
            ctx.DrawImage(_s.Bitmap, new Rect(0, 0, _s.W, _s.H), rect);
        }
        if (_inside)
        {
            var r = Brush / 2 * _zoom;
            ctx.DrawEllipse(null, new Pen(Brushes.Black, 3), _mouse, r, r);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#FF453A")), 1.5), _mouse, r, r);
        }
    }
}

/// <summary>Erase object: paint over what should go (a person, a sign, a watermark, a blemish) and Mochi fills the gap from the surroundings.</summary>
public sealed class EraseWindow : Window
{
    private static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    private readonly string _path;
    private readonly EraseSession _s;
    private readonly EraseCanvas _canvas;
    private readonly TextBlock _status = new() { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _erase, _undo, _save;
    private bool _busy, _changed;

    public string? Saved { get; private set; }

    public EraseWindow(string path)
    {
        _path = path;
        Title = L("x.er.title", "Erase object") + " — " + Path.GetFileName(path);
        Width = 1000; Height = 640; MinWidth = 820; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var (px, w, h) = Matte.Load(path, 6000);
        _s = new EraseSession(px, w, h);
        _canvas = new EraseCanvas(_s) { Marked = Refresh };

        var brush = new Slider { Minimum = 4, Maximum = 400, Value = 40 };
        var brushVal = new TextBlock { Text = "40", Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Right };
        brush.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { _canvas.Brush = brush.Value; brushVal.Text = ((int)brush.Value).ToString(); } };
        var brushHead = new DockPanel { Children = { brushVal, new TextBlock { Text = L("x.bg.brush", "Brush size") } } };
        DockPanel.SetDock(brushVal, Dock.Right);

        _erase = new Button { Content = L("x.er.erase", "Erase the marked area"), Classes = { "pill" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false };
        _erase.Click += (_, _) => RunErase();
        _undo = new Button { Content = L("x.ann.undo", "Undo"), IsEnabled = false };
        _undo.Click += (_, _) => { _s.Undo(); Refresh(); _canvas.InvalidateVisual(); };
        var clear = new Button { Content = L("x.er.clear", "Clear marks") };
        clear.Click += (_, _) => { _s.ClearMarks(); Refresh(); _canvas.InvalidateVisual(); };
        var fit = new Button { Content = L("x.bg.fit", "Fit") };
        fit.Click += (_, _) => _canvas.Fit();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _undo, clear, fit } };

        var cancel = new Button { Content = L("common.cancel", "Cancel") };
        cancel.Click += (_, _) => Close();
        _save = new Button { Content = L("x.er.save", "Save picture"), Classes = { "pill" }, IsEnabled = false };
        _save.Click += (_, _) => Save();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _save } };

        TextBlock Step(string n, string text) => new() { Text = n + "  " + text, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10,
            Children =
            {
                new TextBlock { Text = L("x.er.title", "Erase object"), FontSize = 20, FontWeight = FontWeight.Bold },
                Step("1", L("x.er.s1", "Paint over what should disappear. Right button rubs a mark out.")),
                Step("2", L("x.er.s2", "Press “Erase the marked area”.")),
                Step("3", L("x.er.s3", "Not happy? Undo, or erase in smaller bites. Then save.")),
                new Border { Height = 4 },
                brushHead, brush, _erase, row, _status,
                new TextBlock { Text = L("x.er.hint", "Wheel zooms · middle mouse pans · [ and ] resize the brush · Enter erases · Ctrl+Z undoes. Works best on plain backgrounds such as walls, sky, grass and sand."), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            },
        };
        var side = new DockPanel { Width = 310 };
        var foot = new Border { Padding = new Thickness(16, 10), Child = actions };
        DockPanel.SetDock(foot, Dock.Bottom);
        side.Children.Add(foot);
        side.Children.Add(new ScrollViewer { Content = panel });
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side); root.Children.Add(_canvas);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Z) { _s.Undo(); Refresh(); _canvas.InvalidateVisual(); }
            else if (e.Key == Key.Enter) RunErase();
            else if (e.Key == Key.OemOpenBrackets) brush.Value = Math.Max(4, brush.Value * 0.85);
            else if (e.Key == Key.OemCloseBrackets) brush.Value = Math.Min(400, brush.Value * 1.18);
            else if (e.Key == Key.Escape) Close();
        };
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } sc)
            {
                Width = Math.Min(Width, sc.WorkingArea.Width / sc.Scaling - 40); Height = Math.Min(Height, sc.WorkingArea.Height / sc.Scaling - 40);
                Position = new PixelPoint(sc.WorkingArea.X + (int)((sc.WorkingArea.Width - Width * sc.Scaling) / 2), sc.WorkingArea.Y + (int)((sc.WorkingArea.Height - Height * sc.Scaling) / 2));
            }
            _canvas.Fit();
        };
    }

    private void Refresh()
    {
        _erase.IsEnabled = !_busy && _s.HasMarks;
        _undo.IsEnabled = !_busy && _s.CanUndo;
        _save.IsEnabled = !_busy && _changed;
    }

    private async void RunErase()
    {
        if (_busy || !_s.HasMarks) return;
        _busy = true; _status.Text = L("x.er.working", "Filling the gap…"); Refresh();
        string? error = null;
        await Task.Run(() => { try { _s.Erase(); } catch (Exception ex) { error = ex.Message; } });
        _busy = false; _status.Text = error ?? ""; _changed = true; Refresh(); _canvas.InvalidateVisual();
    }

    private async void Save()
    {
        if (_busy) return;
        _busy = true; _status.Text = L("x.bg.saving", "Saving…"); Refresh();
        var ext = Util.Ext(_path) is ".jpg" or ".jpeg" ? ".jpg" : Util.Ext(_path) == ".webp" ? ".webp" : ".png";
        var dest = Util.Out(_path, Path.GetDirectoryName(_path)!, ext, " (erased)");
        string? error = null;
        await Task.Run(() => { try { AiTools.SaveImage(_s.Px, _s.W, _s.H, dest, 95); } catch (Exception ex) { error = ex.Message; } });
        _busy = false;
        if (error is not null) { _status.Text = error; Refresh(); return; }
        Saved = dest; Close();
    }

    public static Task<string?> RunAsync(string path)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new EraseWindow(path);
        w.Closed += (_, _) => tcs.TrySetResult(w.Saved);
        w.Show();
        return tcs.Task;
    }
}
