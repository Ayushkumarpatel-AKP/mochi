using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>One slice: a preset, or a group of presets that fan out when you rest on it.</summary>
public sealed record SliceItem(string Label, Preset? Preset, IReadOnlyList<Preset>? Members)
{
    public bool IsGroup => Members is not null;
}

/// <summary>Radial toolbar with a front and a back face. Click a slice or drop files on it; groups open an outer ring.</summary>
public sealed class DonutControl : Control
{
    public const double Outer = 150, Inner = 62, Gap = 2.5, Band = 58;

    public IReadOnlyList<SliceItem> Front { get; set; } = Array.Empty<SliceItem>();
    public IReadOnlyList<SliceItem> Back { get; set; } = Array.Empty<SliceItem>();
    public string Hole { get; set; } = "";
    public Action<Preset>? Clicked { get; set; }
    public Action<Preset, List<string>>? Dropped { get; set; }
    /// <summary>Called when files are dragged over: returns only the slices that can convert them.</summary>
    public Func<IReadOnlyList<string>, (IReadOnlyList<SliceItem> Front, IReadOnlyList<SliceItem> Back)>? Refilter { get; set; }
    /// <summary>Whether a preset can convert all of these files; a drop on one that cannot goes to <see cref="Rejected"/>.</summary>
    public Func<IReadOnlyList<string>, Preset, bool>? Supports { get; set; }
    public Action<IReadOnlyList<string>, Preset>? Rejected { get; set; }
    /// <summary>Files were dropped on the hole or outside the ring: nothing to run, so say how to drop.</summary>
    public Action<IReadOnlyList<string>>? Missed { get; set; }
    /// <summary>The drag carried no files Mochi can read (a picture from a web page, an email attachment…): says what it did carry.</summary>
    public Action<string>? NoFiles { get; set; }

    // files dropped on a group's slice wait here while the group stays open; the next click on one of its tools runs them
    private List<string>? _held;
    private List<string>? _dragFiles;     // what the drag carries, seen while it is over the donut
    private DateTime _dragSeen;
    private bool _dropped;

    // The middle of the donut is its "Back" section: a drag held there for a moment flips to the other face (a drag cannot press Tab),
    // a drop there keeps the files and flips, and a click there flips too.
    private const double HubDwellMs = 450;
    private DateTime? _hubSince;
    private bool _hubDone;

    private bool InHub(Point p)
    {
        var c = Center; double dx = p.X - c.X, dy = p.Y - c.Y;
        return dx * dx + dy * dy < Inner * Inner;
    }

    private void HubDrag(Point p)
    {
        if (!InHub(p)) { _hubSince = null; _hubDone = false; return; }
        if (_flipT < 1) return;   // already turning
        _hubSince ??= DateTime.UtcNow;
        if (!_hubDone && (DateTime.UtcNow - _hubSince.Value).TotalMilliseconds >= HubDwellMs)
        {
            _hubDone = true;
            DropLog.Write("held over the middle: flipping to the " + (_showBack ? "front" : "back"));
            Flip();
        }
        if (!_anim.IsEnabled) _anim.Start();   // draws the filling ring
    }

    private bool Dragging => _dragFiles is not null || _dragKey is not null;
    public bool HoldingFiles => _held is { Count: > 0 };

    /// <summary>A drag is over the donut or files are waiting for a tool: it must not close under the user's hand.</summary>
    public bool DragNear => _dragFiles is not null || HoldingFiles;

    private string? _dragKey;
    private (IReadOnlyList<SliceItem> Front, IReadOnlyList<SliceItem> Back)? _unfiltered;

    private void FilterFor(DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files is not { Count: > 0 }) return;
        if (_dragFiles is null) DropLog.Write($"drag over the donut: {DropLog.Short(files)}");
        _dragFiles = files; _dragSeen = DateTime.UtcNow; _dropped = false;
        ApplyFilter(files);
    }

    /// <summary>
    /// The button went up over a tool while a drag was over the donut, yet no Drop arrived (the system sometimes loses it):
    /// returns that tool and the dragged files so the work still starts. Null when there is nothing to recover.
    /// </summary>
    public (Preset Tool, List<string> Files)? TakeLostDrop()
    {
        if (_dragFiles is null || _dropped) return null;
        var age = (DateTime.UtcNow - _dragSeen).TotalSeconds;
        if (age < 0.4) return null;                                   // the real Drop may still be on its way
        if (age > 2.0 || Pick() is not { } p) { _dragFiles = null; return null; }
        var files = _dragFiles; _dragFiles = null;
        DropLog.Write($"drop was lost; running {p.Name} on {DropLog.Short(files)} from the drag");
        return (p, files);
    }

    /// <summary>Shows only the slices that can convert these files, before the pointer has even reached the donut (the trigger knows what Explorer has selected).</summary>
    public void Suggest(IReadOnlyList<string> files)
    {
        if (files.Count > 0 && _dragKey is null) ApplyFilter(files);
    }

    private void ApplyFilter(IReadOnlyList<string> files)
    {
        if (Refilter is null) return;
        var key = string.Join('|', files);
        if (key == _dragKey) return;
        _unfiltered ??= (Front, Back);
        (Front, Back) = Refilter(files);
        _dragKey = key; _hover = _child = _open = -1;
        // the face on show may have nothing for these files (the back face often has no tool for a PDF): show the other one instead of an empty ring
        if (Slices.Count == 0 && (_showBack ? Front : Back).Count > 0) _showBack = !_showBack;
        InvalidateVisual();
    }

    private void Unfilter()
    {
        if (_unfiltered is { } u) (Front, Back) = u;
        _unfiltered = null; _dragKey = null; _hover = _child = _open = -1;
        InvalidateVisual();
    }

    private bool _showBack;
    private int _hover = -1, _child = -1, _open = -1;
    private double _bounce = 1, _flipT = 1;
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(15) };

    private double[] _hv = new double[0];   // eased hover amount per slice

    private IReadOnlyList<SliceItem> Slices => _showBack ? Back : Front;

    private void SyncHover(int n)
    {
        if (_hv.Length != n) _hv = new double[n];
    }

    public DonutControl()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            FilterFor(e);
            Point(e.GetPosition(this), 22);
            HubDrag(e.GetPosition(this));
            e.DragEffects = DragDropEffects.Copy;   // always accept inside the donut: a drop that lands between slices is told so, instead of silently cancelled
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { Unfilter(); _hubSince = null; _hubDone = false; });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            // drop on what is on screen: swapping slices here would put a different one under the pointer
            Point(e.GetPosition(this), 22);
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (files is not { Count: > 0 })
            {
                var kinds = string.Join(", ", e.Data.GetDataFormats());
                DropLog.Write("drop carried no files; formats: " + kinds);
                e.Handled = true; _dropped = true;
                NoFiles?.Invoke(kinds);
                return;
            }
            _dropped = true; _dragFiles = null;
            DropLog.Write($"dropped {DropLog.Short(files)} on {(_hover >= 0 && _hover < Slices.Count ? (_child >= 0 ? Slices[_hover].Members![_child].Name : Slices[_hover].Label) : "nothing")}");
            e.Handled = true;
            _hubSince = null; _hubDone = false;
            if (InHub(e.GetPosition(this)))
            {
                // dropped on the Back section: the files wait while the other face turns up; the next click on a tool runs them
                _held = files;
                if (_flipT >= 1) Flip();
                InvalidateVisual();
                return;
            }
            if (_hover >= 0 && _hover < Slices.Count && Slices[_hover].IsGroup && _child < 0)
            {
                // dropped on a group itself: keep the files, keep the group open, and run them on the tool the user clicks next
                _held = files; _open = _hover; InvalidateVisual();
                return;
            }
            var p = Pick();
            // the work starts after the drop has returned: Explorer is still waiting on it, and the window may close right after
            Dispatcher.UIThread.Post(() => { if (p is null) Missed?.Invoke(files); else Dropped?.Invoke(p, files); }, DispatcherPriority.Background);
        });
        _anim.Tick += (_, _) => Step();

        // the donut bounces in; "reduce animations" turns that off
        if (App.Settings.ReduceMotion) _bounce = 1; else { _bounce = 0; _anim.Start(); }
    }

    /// <summary>Shows the front or back face at once (the preview on the Presets page).</summary>
    public void SetFace(bool back)
    {
        _showBack = back; _hover = _child = _open = -1;
        InvalidateVisual();
    }

    public void Flip()
    {
        _open = -1;
        if (App.Settings.ReduceMotion) { _showBack = !_showBack; InvalidateVisual(); return; }
        _flipT = 0; _anim.Start();
    }

    private void Step()
    {
        var busy = false;
        for (var i = 0; i < _hv.Length; i++)
        {
            var goal = i == _hover && _child < 0 || (i < Slices.Count && Slices[i].IsGroup && i == _open) ? 1.0 : 0.0;
            if (Math.Abs(_hv[i] - goal) < 0.02) _hv[i] = goal;
            else { _hv[i] += (goal - _hv[i]) * 0.32; busy = true; }
        }
        if (_bounce < 1) { _bounce = Math.Min(1, _bounce + 0.07); busy = true; }
        if (_hubSince is not null && !_hubDone) busy = true;
        if (_flipT < 1)
        {
            var before = _flipT; _flipT = Math.Min(1, _flipT + 0.09); busy = true;
            if (before < 0.5 && _flipT >= 0.5) { _showBack = !_showBack; _hover = _child = -1; }
        }
        InvalidateVisual();
        if (!busy) _anim.Stop();
    }

    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);

    private static double Deg(Point p, Point c) => (Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI + 90 + 360) % 360;   // 0° = top, clockwise

    /// <summary>The slice, and the member of an open group, under the pointer (-1 for none).</summary>
    private (int Slice, int Child) Hit(Point p, double slack = 0)
    {
        var s = Slices; if (s.Count == 0) return (-1, -1);
        var c = Center; double dx = p.X - c.X, dy = p.Y - c.Y, r = Math.Sqrt(dx * dx + dy * dy);
        var step = 360.0 / s.Count; var deg = Deg(p, c);
        if (r >= Inner && r <= Outer + slack) return (Math.Min(s.Count - 1, (int)(deg / step)), -1);   // a drag gets a little extra room at the rim
        if (_open >= 0 && _open < s.Count && s[_open].Members is { Count: > 0 } m && r > Outer && r <= Outer + Band + 8)
        {
            var (from, per) = Fan(_open, s.Count, m.Count);
            var rel = (deg - from + 720) % 360;
            if (rel < per * m.Count) return (_open, Math.Min(m.Count - 1, (int)(rel / per)));
        }
        return (-1, -1);
    }

    /// <summary>
    /// Where an open group's members sit on the outer ring: centred on the group's slice, and wide enough that each member
    /// gets room for its name (a group of five would otherwise squeeze into one slice's 45°). Degrees, 0° = top, clockwise.
    /// </summary>
    private static (double From, double Per) Fan(int slice, int slices, int members)
    {
        var step = 360.0 / slices;
        var mid = slice * step + step / 2;
        var per = Math.Max(step / members, 66 / (Outer + 8 + Band / 2) * 180 / Math.PI);
        return (mid - per * members / 2, per);
    }

    /// <summary>The name on a fanned-out member: "Summarise (AI)" is just "Summarise" inside the AI slice.</summary>
    private static string Short(Preset p) => p.DisplayName.Replace(" (AI)", "");

    private void Point(Point p, double slack = 0)
    {
        var (s, c) = Hit(p, slack);
        if (s >= 0 && c < 0) _open = Slices[s].IsGroup ? s : -1;
        else if (s < 0 && c < 0) _open = -1;
        if (s == _hover && c == _child) return;
        _hover = s; _child = c;
        if (!_anim.IsEnabled) _anim.Start();   // eases the hover glow; Step stops itself once settled
        InvalidateVisual();
    }

    /// <summary>The preset the pointer is on: a plain slice, or a member of the open group.</summary>
    private Preset? Pick()
    {
        if (_hover < 0 || _hover >= Slices.Count) return null;
        return _child >= 0 ? Slices[_hover].Members![_child] : Slices[_hover].Preset;
    }

    protected override void OnPointerMoved(PointerEventArgs e) => Point(e.GetPosition(this));
    protected override void OnPointerExited(PointerEventArgs e) { _hover = _child = -1; InvalidateVisual(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { Flip(); e.Handled = true; }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Point(e.GetPosition(this));
        if (Pick() is not { } p)
        {
            if (InHub(e.GetPosition(this)) && _flipT >= 1) Flip();   // click the middle to turn the donut
            return;
        }
        if (_held is { } files) { _held = null; Dropped?.Invoke(p, files); }
        else Clicked?.Invoke(p);
    }

    public override void Render(DrawingContext ctx)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var textCol = dark ? Colors.White : Color.Parse("#1C1C1E");
        var accent = Color.TryParse(App.Settings.Accent, out var a) ? a : Colors.DodgerBlue;
        var op = Math.Clamp(App.Settings.DonutOpacity, 0.55, 1);
        var c = Center; var slices = Slices; var n = slices.Count;
        SyncHover(n);

        // bounce in (overshoots a little past full size) and squash sideways while flipping
        var t = _bounce - 1; double ease = 1 + 2.70158 * t * t * t + 1.70158 * t * t;
        var scale = 0.5 + 0.5 * ease;
        var flipX = _flipT < 1 ? Math.Max(0.02, Math.Abs(Math.Cos(Math.PI * _flipT))) : 1;
        using var _ = ctx.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateScale(scale * flipX, scale) * Matrix.CreateTranslation(c.X, c.Y));

        // soft layered drop shadow, then a glassy backing disc
        for (var k = 5; k >= 1; k--)
            ctx.DrawEllipse(new SolidColorBrush(Colors.Black, (dark ? 0.10 : 0.05)), null, new Point(c.X, c.Y + 4), Outer + 4 + k * 3.2, Outer + 4 + k * 3.2);
        var plate = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(Color.FromArgb((byte)(op * 235), dark ? (byte)30 : (byte)250, dark ? (byte)30 : (byte)250, dark ? (byte)34 : (byte)252), 0.55),
                              new GradientStop(Color.FromArgb((byte)(op * 250), dark ? (byte)14 : (byte)226, dark ? (byte)14 : (byte)226, dark ? (byte)16 : (byte)230), 1) }
        };
        ctx.DrawEllipse(plate, new Pen(new SolidColorBrush(Colors.White, dark ? 0.16 : 0.7), 1.2), c, Outer + 6, Outer + 6);

        var step = n == 0 ? 360.0 : 360.0 / n;
        const double pad = 3;   // pen radius: draws the slice inset, then strokes it so the corners come out rounded

        for (var i = 0; i < n; i++)
        {
            var hv = _hv[i];
            var hot = i == _hover && _child < 0 || (slices[i].IsGroup && i == _open);
            var r1 = Outer - 3 + 5 * hv;
            var inset = (Gap / 2 + pad + 1.5) / ((Inner + r1) / 2) * 180 / Math.PI;
            var s0 = -90 + i * step + inset; var s1 = -90 + (i + 1) * step - inset;
            var fill = SliceBrush(dark, accent, op, hv);
            var geo = Ring(c, Inner + 4 + pad, r1 - pad, s0, s1);
            ctx.DrawGeometry(fill, new Pen(fill, pad * 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
            // thin glassy highlight along the top rim of each slice
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Colors.White, (dark ? 0.10 : 0.55) + 0.25 * hv), 1), Ring(c, r1 - 1.5, r1 - 1.5, s0 + 0.5, s1 - 0.5));
            var labelR = (Inner + r1) / 2 + 4;
            var chord = 2 * labelR * Math.Sin((s1 - s0) * Math.PI / 360) * 0.86;   // the room the slice really has across
            Label(ctx, slices[i].Label + (slices[i].IsGroup ? " ▸" : ""), c, labelR, (s0 + s1) / 2, hot ? Colors.White : textCol, 13, Math.Clamp(chord, 44, 88), hot);

            // an open group fans its presets out on an outer ring
            if (slices[i].Members is { Count: > 0 } m && i == _open)
            {
                var (from, per) = Fan(i, n, m.Count);
                for (var k = 0; k < m.Count; k++)
                {
                    var q0 = -90 + from + k * per + 0.9; var q1 = -90 + from + (k + 1) * per - 0.9; var on = k == _child;
                    var qf = SliceBrush(dark, accent, 0.97, on ? 1 : 0);
                    ctx.DrawGeometry(qf, new Pen(qf, pad * 2, lineJoin: PenLineJoin.Round), Ring(c, Outer + 10 + pad, Outer + 6 + Band - pad, q0, q1));
                    var memberR = Outer + 8 + Band / 2;
                    var memberChord = 2 * memberR * Math.Sin((q1 - q0) * Math.PI / 360) * 0.9;
                    Label(ctx, Short(m[k]), c, memberR, (q0 + q1) / 2, on ? Colors.White : textCol, 11, Math.Clamp(memberChord, 40, 68), on);
                }
            }
        }

        // frosted hub with a thin accent ring
        var hub = Inner - 2;
        ctx.DrawEllipse(new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(dark ? (byte)60 : (byte)230, 255, 255, 255), 0), new GradientStop(Color.FromArgb(dark ? (byte)14 : (byte)120, 255, 255, 255), 1) } },
            new Pen(new SolidColorBrush(accent, 0.55 + 0.4 * (_hover >= 0 ? 1 : 0)), 1.6), c, hub, hub);
        if (_hubSince is { } hs && !_hubDone)
        {
            var k = Math.Clamp((DateTime.UtcNow - hs).TotalMilliseconds / HubDwellMs, 0, 1);
            if (k > 0.02) ctx.DrawGeometry(new SolidColorBrush(accent), null, Ring(c, hub - 5, hub, -90, -90 + 359.5 * k));
        }
        var other = _showBack ? (Loc.TryGet("x.front") ?? "Front") : (Loc.TryGet("x.back") ?? "Back");
        var hold = Dragging && _held is null ? string.Format(Loc.TryGet("x.hold_for") ?? "Hold here for {0} ⟲", other) : null;
        var hole = _hover >= 0 && _hover < n ? (_child >= 0 ? Short(slices[_hover].Members![_child]) : slices[_hover].Label) : hold ?? (_held is { Count: > 0 } ? string.Format(Loc.TryGet("x.pick_tool") ?? "{0} files: pick a tool", _held.Count) : _showBack ? "⟲ " + Loc.T("x.back") : _dragKey is not null && n == 0 ? (Loc.TryGet("x.nothing") ?? "Nothing can convert this") : Hole);
        var ft = Fit(hole, new SolidColorBrush(textCol, 0.8), FontWeight.Medium, 12.5, Inner * 1.5, 3);
        ctx.DrawText(ft, new Point(c.X - Inner * 0.75, c.Y - ft.Height / 2));
    }

    private static IBrush SliceBrush(bool dark, Color accent, double op, double hv)
    {
        static Color Mix(Color x, Color y, double t) => Color.FromArgb((byte)(x.A + (y.A - x.A) * t), (byte)(x.R + (y.R - x.R) * t), (byte)(x.G + (y.G - x.G) * t), (byte)(x.B + (y.B - x.B) * t));
        var top = dark ? Color.Parse("#46464C") : Colors.White;
        var bot = dark ? Color.Parse("#2A2A2E") : Color.Parse("#E9E9EE");
        var hi = Mix(accent, Colors.White, 0.18); var lo = Mix(accent, Colors.Black, 0.22);
        var a = (byte)(255 * op);
        top = Mix(Color.FromArgb(a, top.R, top.G, top.B), hi, hv); bot = Mix(Color.FromArgb(a, bot.R, bot.G, bot.B), lo, hv);
        return new LinearGradientBrush { StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bot, 1) } };
    }

    /// <summary>
    /// Text that always fits its slice: wraps to at most <paramref name="maxLines"/> lines, and shrinks a little (down to 8.5) before it would
    /// run past them, then trims with an ellipsis as a last resort. Long preset names never spill over the ring.
    /// </summary>
    private static FormattedText Fit(string text, IBrush brush, FontWeight weight, double size, double width, int maxLines, double minSize = 8.5)
    {
        FormattedText Make(double s, int lines, bool trim) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), s, brush)
        { MaxTextWidth = width, MaxLineCount = lines, Trimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None, TextAlignment = TextAlignment.Center };
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double WordW(string w, double s) => new FormattedText(w, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), s, Brushes.Black).Width;
        for (var s = size; s >= minSize; s -= 0.5)
        {
            if (words.Any(w => WordW(w, s) > width)) continue;   // never break a word in the middle: make the text smaller instead
            var ft = Make(s, maxLines + 6, false);   // let it wrap freely, then check it still fits
            if (ft.Height <= s * 1.4 * maxLines + 1 && ft.Width <= width + 1) return Make(s, maxLines, false);
        }
        return Make(minSize, maxLines, true);
    }

    private static void Label(DrawingContext ctx, string text, Point c, double radius, double midDeg, Color col, double size, double width, bool shadow = false, int maxLines = 2)
    {
        var mid = midDeg * Math.PI / 180;
        var pos = new Point(c.X + radius * Math.Cos(mid), c.Y + radius * Math.Sin(mid));
        var ft = Fit(text, new SolidColorBrush(col), FontWeight.SemiBold, size, width, maxLines);
        var at = new Point(pos.X - width / 2, pos.Y - ft.Height / 2);   // centre the whole text box, not the measured line, so every label sits true
        if (shadow) ctx.DrawText(Fit(text, new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), FontWeight.SemiBold, size, width, maxLines), new Point(at.X, at.Y + 1));
        ctx.DrawText(ft, at);
    }

    private static StreamGeometry Ring(Point c, double r0, double r1, double startDeg, double endDeg)
    {
        static Point P(Point c, double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var large = endDeg - startDeg > 180;
        var g = new StreamGeometry();
        using var x = g.Open();
        x.BeginFigure(P(c, r1, startDeg), true);
        x.ArcTo(P(c, r1, endDeg), new Size(r1, r1), 0, large, SweepDirection.Clockwise);
        x.LineTo(P(c, r0, endDeg));
        x.ArcTo(P(c, r0, startDeg), new Size(r0, r0), 0, large, SweepDirection.CounterClockwise);
        x.EndFigure(true);
        return g;
    }
}
