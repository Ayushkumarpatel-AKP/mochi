using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>The cut-out being edited at working size: raw mask → matte, plus the user's brush edits layered on top.</summary>
public sealed class MatteSession
{
    public const int WorkSide = 1400;
    public readonly byte[] Px;
    public readonly int W, H;
    public MatteParams P = new(true, 18, 0.5f, 0, 0.8f, true, 255, 255, 255);
    public float[]? AiRaw;
    public (byte R, byte G, byte B)? Fill;       // null: transparent
    public readonly WriteableBitmap Bitmap;

    private float[]? _colRaw; private (float, bool, byte, byte, byte) _colKey;
    private float[] _matte, _eff;
    private byte[] _mT, _mW;
    private readonly byte[] _prev;
    private readonly List<(byte[] T, byte[] W)> _undo = new(), _redo = new();

    public MatteSession(byte[] px, int w, int h)
    {
        Px = px; W = w; H = h;
        _matte = Enumerable.Repeat(1f, w * h).ToArray(); _eff = (float[])_matte.Clone();
        _mT = new byte[w * h]; _mW = new byte[w * h]; _prev = new byte[w * h * 4];
        Bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Repaint(0, 0, W, H);
    }

    public byte[] EditT => _mT;
    public byte[] EditW => _mW;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private float EffAt(int i)
    {
        var mw = _mW[i] / 255f;
        return _matte[i] * (1 - mw) + _mT[i] / 255f * mw;
    }

    /// <summary>Rebuilds the matte from the current settings (the mask is cached; only a changed colour key recomputes it).</summary>
    public void Recalc()
    {
        float[]? raw;
        if (P.Ai) raw = AiRaw;
        else
        {
            var key = (P.Tol, P.Contiguous, P.BgR, P.BgG, P.BgB);
            if (_colRaw is null || _colKey != key) { _colRaw = Matte.ColourMask(Px, W, H, P); _colKey = key; }
            raw = _colRaw;
        }
        if (raw is null) return;
        _matte = Matte.Refine(raw, W, H, P, 1);
        for (var i = 0; i < _eff.Length; i++) _eff[i] = EffAt(i);
        Repaint(0, 0, W, H);
    }

    public void Repaint(int x0, int y0, int x1, int y1)
    {
        Matte.Paint(Px, W, _eff, _matte, P, true, Fill, _prev, W, 0, 0, x0, y0, x1, y1);
        using var fb = Bitmap.Lock();
        for (var y = 0; y < H; y++) Marshal.Copy(_prev, y * W * 4, fb.Address + y * fb.RowBytes, W * 4);
    }

    public void Begin() { _undo.Add(((byte[])_mT.Clone(), (byte[])_mW.Clone())); if (_undo.Count > 40) _undo.RemoveAt(0); _redo.Clear(); }

    public void Undo() => Swap(_undo, _redo);
    public void Redo() => Swap(_redo, _undo);

    private void Swap(List<(byte[] T, byte[] W)> from, List<(byte[] T, byte[] W)> to)
    {
        if (from.Count == 0) return;
        to.Add(((byte[])_mT.Clone(), (byte[])_mW.Clone()));
        (_mT, _mW) = from[^1]; from.RemoveAt(from.Count - 1);
        for (var i = 0; i < _eff.Length; i++) _eff[i] = EffAt(i);
        Repaint(0, 0, W, H);
    }

    public void ResetEdits()
    {
        if (!_mW.Any(v => v > 0)) return;
        Begin();
        _mT = new byte[W * H]; _mW = new byte[W * H];
        for (var i = 0; i < _eff.Length; i++) _eff[i] = _matte[i];
        Repaint(0, 0, W, H);
    }

    /// <summary>Moves one pixel toward erased (0) or restored (1) by <paramref name="w"/>.</summary>
    private void Nudge(int i, float w, float target)
    {
        float mw = _mW[i] / 255f, mt = _mT[i] / 255f, r = _matte[i];
        var now = (r * (1 - mw) + mt * mw) * (1 - w) + target * w;
        var mw2 = mw + w * (1 - mw);
        if (mw2 < 1e-3f) return;
        _mW[i] = (byte)(mw2 * 255 + 0.5f);
        _mT[i] = (byte)(Math.Clamp((now - r * (1 - mw2)) / mw2, 0, 1) * 255 + 0.5f);
        _eff[i] = EffAt(i);
    }

    public void Stamp(double cx, double cy, double radius, double hard, bool restore)
    {
        int x0 = Math.Max(0, (int)Math.Floor(cx - radius)), x1 = Math.Min(W - 1, (int)Math.Ceiling(cx + radius));
        int y0 = Math.Max(0, (int)Math.Floor(cy - radius)), y1 = Math.Min(H - 1, (int)Math.Ceiling(cy + radius));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy)) / radius;
                if (d >= 1) continue;
                var w = d <= hard ? 1f : 1f - Matte.Smooth((float)hard, 1f, (float)d);
                Nudge(y * W + x, w, restore ? 1 : 0);
            }
        Repaint(x0, y0, x1 + 1, y1 + 1);
    }

    public void WandApply(int x, int y, bool restore)
    {
        Begin();
        var m = Matte.Wand(Px, W, H, Math.Clamp(x, 0, W - 1), Math.Clamp(y, 0, H - 1), P.Tol);
        for (var i = 0; i < m.Length; i++) if (m[i] > 0.01f) Nudge(i, Math.Min(1, m[i]), restore ? 1 : 0);
        Repaint(0, 0, W, H);
    }

    public (byte R, byte G, byte B) ColourAt(int x, int y)
    {
        var i = (Math.Clamp(y, 0, H - 1) * W + Math.Clamp(x, 0, W - 1)) * 4;
        return (Px[i + 2], Px[i + 1], Px[i]);
    }
}

/// <summary>The picture with the cut-out, a zoom and pan view, and the brush, wand and colour-picker tools.</summary>
public sealed class MatteCanvas : Control
{
    private static readonly IBrush Checker = new DrawingBrush
    {
        TileMode = TileMode.Tile,
        DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#FFFFFF")), Geometry = Geometry.Parse("M0,0 H16 V16 H0 Z") },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#DCDCE1")), Geometry = Geometry.Parse("M0,0 H8 V8 H0 Z M8,8 H16 V16 H8 Z") },
            },
        },
    };

    private readonly MatteSession _s;
    private double _zoom = 1; private Point _off; private bool _userView;   // true once the user has zoomed or panned
    private Point _mouse; private bool _inside, _painting, _panning; private Point _last, _panFrom, _offFrom;

    public string Tool { get; set; } = "erase";   // erase, restore, wand, pick, pan
    public double Brush { get; set; } = 48;       // diameter in picture pixels
    public double Hardness { get; set; } = 0.7;
    public Action? Edited { get; set; }
    public Action<(byte R, byte G, byte B)>? Picked { get; set; }

    public MatteCanvas(MatteSession s)
    {
        _s = s; ClipToBounds = true; Focusable = true;
    }

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
        var pt = e.GetCurrentPoint(this); var pos = e.GetPosition(this); var pic = ToPic(pos);
        if (pt.Properties.IsMiddleButtonPressed || (pt.Properties.IsLeftButtonPressed && Tool == "pan"))
        {
            _panning = true; _panFrom = pos; _offFrom = _off; e.Pointer.Capture(this); return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        switch (Tool)
        {
            case "erase" or "restore":
                _s.Begin(); _painting = true; _last = pic; e.Pointer.Capture(this);
                _s.Stamp(pic.X, pic.Y, Brush / 2, Hardness, Tool == "restore"); Edited?.Invoke(); break;
            case "wand":
                if (pic.X >= 0 && pic.Y >= 0 && pic.X < _s.W && pic.Y < _s.H) { _s.WandApply((int)pic.X, (int)pic.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); Edited?.Invoke(); }
                break;
            case "pick":
                if (pic.X >= 0 && pic.Y >= 0 && pic.X < _s.W && pic.Y < _s.H) Picked?.Invoke(_s.ColourAt((int)pic.X, (int)pic.Y));
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this); _mouse = pos; _inside = true;
        if (_panning) { _off = _offFrom + (pos - _panFrom); _userView = true; }
        else if (_painting)
        {
            var pic = ToPic(pos); var r = Brush / 2;
            var dist = Math.Sqrt((pic.X - _last.X) * (pic.X - _last.X) + (pic.Y - _last.Y) * (pic.Y - _last.Y));
            var step = Math.Max(1, r * 0.2); var n = Math.Max(1, (int)(dist / step));
            for (var k = 1; k <= n; k++)
                _s.Stamp(_last.X + (pic.X - _last.X) * k / n, _last.Y + (pic.Y - _last.Y) * k / n, r, Hardness, Tool == "restore");
            _last = pic; Edited?.Invoke();
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _painting = _panning = false; e.Pointer.Capture(null); }
    protected override void OnPointerExited(PointerEventArgs e) { _inside = false; InvalidateVisual(); }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this); var pic = ToPic(pos);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.18, e.Delta.Y), 0.05, 40);
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
            ctx.FillRectangle(_s.Fill is { } f ? new SolidColorBrush(Color.FromRgb(f.R, f.G, f.B)) : Checker, rect);
            RenderOptions.SetBitmapInterpolationMode(this, _zoom >= 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
            ctx.DrawImage(_s.Bitmap, new Rect(0, 0, _s.W, _s.H), rect);
        }
        if (_inside && Tool is "erase" or "restore")
        {
            var r = Brush / 2 * _zoom; var col = Tool == "erase" ? Color.Parse("#FF453A") : Color.Parse("#30D158");
            ctx.DrawEllipse(null, new Pen(Brushes.Black, 3), _mouse, r, r);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(col), 1.5), _mouse, r, r);
        }
    }
}

/// <summary>Remove background: an AI subject mask or a colour key, then refine with sliders and the brush before saving a transparent PNG.</summary>
public sealed class BgRemoveWindow : Window
{
    private static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    private readonly string _path;
    private readonly MatteSession _s;
    private readonly MatteCanvas _canvas;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly TextBlock _status = new() { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap };
    private readonly ToggleButton _ai, _col;
    private readonly List<ToggleButton> _tools = new();
    private readonly Border _swatch = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly Control[] _aiOnly, _colOnly;
    private readonly Button _undo = new(), _redo = new(), _save;
    private bool _busy;
    private CheckBox _crop = null!;
    private ComboBox _bg = null!;

    public string? Saved { get; private set; }

    public BgRemoveWindow(string path)
    {
        _path = path;
        Title = L("x.bg.title", "Remove background") + " — " + Path.GetFileName(path);
        Width = 1100; Height = 700; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var (px, w, h) = Matte.Load(path, MatteSession.WorkSide);
        _s = new MatteSession(px, w, h);
        var bg = Matte.EstimateBg(px, w, h);
        _s.P = _s.P with { BgR = bg.R, BgG = bg.G, BgB = bg.B };
        _swatch.Background = new SolidColorBrush(Color.FromRgb(bg.R, bg.G, bg.B));

        _canvas = new MatteCanvas(_s) { Edited = RefreshHistory, Picked = c => { SetBg(c); } };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _s.Recalc(); _canvas.InvalidateVisual(); };

        // ---- method
        _ai = new ToggleButton { Content = L("x.bg.ai", "AI subject"), IsChecked = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        _col = new ToggleButton { Content = L("x.bg.colour", "By colour"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        _ai.Click += (_, _) => SetMethod(true);
        _col.Click += (_, _) => SetMethod(false);
        var method = new UniformGrid { Columns = 2, Children = { _ai, _col } };

        var sens = Slide(L("x.bg.sens", "Subject sensitivity"), 0.1, 0.9, 0.5, v => _s.P = _s.P with { Sens = (float)v }, "0.00", out var sensRow);
        var tol = Slide(L("x.bg.tol", "Colour tolerance"), 3, 70, 18, v => _s.P = _s.P with { Tol = (float)v }, "0", out var tolRow);
        var contig = new CheckBox { Content = L("x.bg.contig", "Only the background touching the edges"), IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        contig.IsCheckedChanged += (_, _) => { _s.P = _s.P with { Contiguous = contig.IsChecked == true }; Later(); };
        var swatchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _swatch, new TextBlock { Text = L("x.bg.bgcolour", "Background colour (use Pick colour to change)"), Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 } },
        };
        var edge = Slide(L("x.bg.edge", "Edge: shrink ← → grow"), -12, 12, 0, v => _s.P = _s.P with { Shift = (float)v }, "0", out var edgeRow);
        var feather = Slide(L("x.bg.feather", "Feather"), 0, 8, 0.8, v => _s.P = _s.P with { Feather = (float)v }, "0.0", out var featherRow);
        _aiOnly = new Control[] { sensRow };
        _colOnly = new Control[] { tolRow, contig, swatchRow };

        // ---- tools
        var tools = new WrapPanel();
        foreach (var (id, text) in new[] { ("erase", L("x.bg.erase", "Erase")), ("restore", L("x.bg.restore", "Restore")), ("wand", L("x.bg.wand", "Wand")), ("pick", L("x.bg.pick", "Pick colour")), ("pan", L("x.bg.pan", "Pan")) })
        {
            var b = new ToggleButton { Content = text, Margin = new Thickness(0, 0, 6, 6), Tag = id, IsChecked = id == "erase" };
            b.Click += (_, _) => { foreach (var o in _tools) o.IsChecked = o == b; _canvas.Tool = id; };
            _tools.Add(b); tools.Children.Add(b);
        }
        var brush = Slide(L("x.bg.brush", "Brush size"), 4, 300, 48, v => _canvas.Brush = v, "0", out var brushRow, false);
        var hard = Slide(L("x.bg.hard", "Brush hardness"), 0, 100, 70, v => _canvas.Hardness = v / 100, "0", out var hardRow, false);

        _undo.Content = L("x.ann.undo", "Undo"); _undo.Click += (_, _) => { _s.Undo(); RefreshHistory(); };
        _redo.Content = L("x.bg.redo", "Redo"); _redo.Click += (_, _) => { _s.Redo(); RefreshHistory(); };
        var reset = new Button { Content = L("x.bg.reset", "Reset edits") };
        reset.Click += (_, _) => { _s.ResetEdits(); RefreshHistory(); };
        var fit = new Button { Content = L("x.bg.fit", "Fit") };
        fit.Click += (_, _) => _canvas.Fit();
        var history = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _undo, _redo, reset, fit } };

        // ---- export
        _bg = new ComboBox
        {
            ItemsSource = new[] { L("x.bg.transparent", "Transparent"), L("x.bg.white", "White"), L("x.bg.black", "Black"), L("x.bg.grey", "Light grey") },
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _bg.SelectionChanged += (_, _) =>
        {
            _s.Fill = _bg.SelectedIndex switch { 1 => ((byte)255, (byte)255, (byte)255), 2 => ((byte)0, (byte)0, (byte)0), 3 => ((byte)235, (byte)235, (byte)238), _ => null };
            _s.Repaint(0, 0, _s.W, _s.H); _canvas.InvalidateVisual();
        };
        _crop = new CheckBox { Content = L("x.bg.crop", "Crop to the subject"), IsChecked = false };
        var cancel = new Button { Content = L("common.cancel", "Cancel") };
        cancel.Click += (_, _) => Close();
        _save = new Button { Content = L("x.bg.save", "Save PNG"), Classes = { "pill" } };
        _save.Click += (_, _) => Save();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _save } };

        Control Title_(string t) => new TextBlock { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
        var panel = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 6,
            Children =
            {
                new TextBlock { Text = L("x.bg.title", "Remove background"), FontSize = 20, FontWeight = FontWeight.Bold },
                Title_(L("x.bg.method", "Method")), method, sensRow, tolRow, contig, swatchRow,
                Title_(L("x.bg.refine", "Refine the edge")), edgeRow, featherRow,
                Title_(L("x.bg.touch", "Touch up")), tools, brushRow, hardRow, history,
                Title_(L("x.bg.export", "Export")), _bg, _crop, _status,
                new TextBlock { Text = L("x.bg.hint", "Wheel zooms · middle mouse pans · Shift+click with Wand restores · [ and ] resize the brush · Ctrl+Z undoes"), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) },
            },
        };
        panel.Children.Add(new Border { Height = 8 });
        var side = new DockPanel { Width = 310 };
        var foot = new Border { Padding = new Thickness(16, 10), Child = actions };
        DockPanel.SetDock(foot, Dock.Bottom);
        side.Children.Add(foot);
        side.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(_canvas);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Z) { _s.Undo(); RefreshHistory(); }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Y) { _s.Redo(); RefreshHistory(); }
            else if (e.Key == Key.OemOpenBrackets) brush.Value = Math.Max(4, brush.Value * 0.85);
            else if (e.Key == Key.OemCloseBrackets) brush.Value = Math.Min(300, brush.Value * 1.18);
            else if (e.Key == Key.Escape) Close();
        };

        SetMethod(true, first: true);
        RefreshHistory();
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } sc)   // never taller or wider than the screen
            {
                Width = Math.Min(Width, sc.WorkingArea.Width / sc.Scaling - 40); Height = Math.Min(Height, sc.WorkingArea.Height / sc.Scaling - 40);
                Position = new PixelPoint(sc.WorkingArea.X + (int)((sc.WorkingArea.Width - Width * sc.Scaling) / 2), sc.WorkingArea.Y + (int)((sc.WorkingArea.Height - Height * sc.Scaling) / 2));
            }
            _canvas.Fit(); FindSubject();
        };
    }

    private Slider Slide(string label, double min, double max, double value, Action<double> on, string format, out StackPanel row, bool recalc = true)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value };
        var val = new TextBlock { Text = value.ToString(format), Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Right };
        s.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            val.Text = s.Value.ToString(format); on(s.Value);
            if (recalc) Later();
        };
        var head = new DockPanel { Children = { val, new TextBlock { Text = label } } };
        DockPanel.SetDock(val, Dock.Right);
        row = new StackPanel { Children = { head, s } };
        return s;
    }

    private void Later() { if (_busy) return; _debounce.Stop(); _debounce.Start(); }

    private void RefreshHistory() { _undo.IsEnabled = _s.CanUndo; _redo.IsEnabled = _s.CanRedo; }

    private void SetMethod(bool ai, bool first = false)
    {
        _ai.IsChecked = ai; _col.IsChecked = !ai;
        _s.P = _s.P with { Ai = ai };
        foreach (var c in _aiOnly) c.IsVisible = ai;
        foreach (var c in _colOnly) c.IsVisible = !ai;
        if (first) return;
        if (ai && _s.AiRaw is null) FindSubject(); else { _s.Recalc(); _canvas.InvalidateVisual(); }
    }

    private void SetBg((byte R, byte G, byte B) c)
    {
        _s.P = _s.P with { BgR = c.R, BgG = c.G, BgB = c.B };
        _swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        SetMethod(false);
    }

    /// <summary>Runs the AI model off the UI thread; falls back to colour mode when the model cannot run.</summary>
    private async void FindSubject()
    {
        if (_busy) return;
        _busy = true; _save.IsEnabled = false; _status.Text = L("x.bg.finding", "Finding the subject…");
        float[]? mask = null; string? error = null;
        await Task.Run(() => { try { mask = Matte.RunAi(_s.Px, _s.W, _s.H); } catch (Exception ex) { error = ex.Message; } });
        _busy = false; _save.IsEnabled = true;
        if (mask is null)
        {
            _status.Text = L("x.bg.ai_failed", "The AI model could not run here, so the colour method is on: ") + error;
            SetMethod(false);
            return;
        }
        _status.Text = "";
        _s.AiRaw = mask;
        if (_s.P.Ai) { _s.Recalc(); _canvas.InvalidateVisual(); }
    }

    private async void Save()
    {
        if (_busy) return;
        _busy = true; _save.IsEnabled = false; _status.Text = L("x.bg.saving", "Saving at full size…");
        var p = _s.P; var fill = _s.Fill; var crop = _crop.IsChecked == true;
        var t = (byte[])_s.EditT.Clone(); var wgt = (byte[])_s.EditW.Clone(); var ai = _s.AiRaw;
        int ww = _s.W, wh = _s.H;
        var dest = Util.Out(_path, Path.GetDirectoryName(_path)!, ".png", fill is null ? " (no background)" : " (new background)");
        string? error = null;
        await Task.Run(() =>
        {
            try
            {
                var (fpx, fw, fh) = Matte.Load(_path, 0);
                var scale = fw / (float)ww;
                var raw = p.Ai ? Matte.Resample(ai!, ww, wh, fw, fh) : Matte.ColourMask(fpx, fw, fh, p);
                var matte = Matte.Refine(raw, fw, fh, p, scale);
                var mt = Matte.Resample(t.Select(v => v / 255f).ToArray(), ww, wh, fw, fh);
                var mw = Matte.Resample(wgt.Select(v => v / 255f).ToArray(), ww, wh, fw, fh);
                var eff = new float[matte.Length];
                for (var i = 0; i < eff.Length; i++) eff[i] = matte[i] * (1 - mw[i]) + mt[i] * mw[i];

                int x0 = 0, y0 = 0, x1 = fw, y1 = fh;
                if (crop)
                {
                    x0 = fw; y0 = fh; x1 = 0; y1 = 0;
                    for (var y = 0; y < fh; y++)
                        for (var x = 0; x < fw; x++)
                            if (eff[y * fw + x] * fpx[(y * fw + x) * 4 + 3] / 255f > 0.03f) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + 1); y1 = Math.Max(y1, y + 1); }
                    if (x1 <= x0 || y1 <= y0) { x0 = 0; y0 = 0; x1 = fw; y1 = fh; }
                }
                var ow = x1 - x0; var oh = y1 - y0;
                var outPx = new byte[ow * oh * 4];
                Matte.Paint(fpx, fw, eff, matte, p, false, fill, outPx, ow, x0, y0, x0, y0, x1, y1);
                Matte.SavePng(outPx, ow, oh, dest);
            }
            catch (Exception ex) { error = ex.Message; }
        });
        _busy = false; _save.IsEnabled = true;
        if (error is not null) { _status.Text = error; return; }
        Saved = dest;
        Close();
    }

    public static Task<string?> RunAsync(string path)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new BgRemoveWindow(path);
        w.Closed += (_, _) => tcs.TrySetResult(w.Saved);
        w.Show();
        return tcs.Task;
    }
}
