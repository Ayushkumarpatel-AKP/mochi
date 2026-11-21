using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>
/// The on-screen guide: it dims the real app, lights up the button or menu entry a page is about, and shows Mochi's speech card next to it.
/// There is no separate window. Pages without a target sit in the middle of the screen. Left and Right turn pages, Enter goes on, Esc closes.
/// Pages come from Services/Guide.cs (JSON in Assets/Guide); a page's "target" is turned into a control by <paramref name="resolve"/>.
/// </summary>
public sealed class GuideOverlay : Grid
{
    private const double CardW = 400, Gap = 18, Edge = 16, HolePad = 7, Dur = 0.30;

    private readonly GuideDeck _deck;
    private readonly string _doneLabel;
    private readonly Func<string?, Control?> _resolve;
    private readonly Action<string?> _prepare;
    private readonly Action _closed;

    private readonly Avalonia.Controls.Shapes.Path _scrim = new() { Fill = new SolidColorBrush(Color.FromArgb(150, 12, 6, 10)) };
    private readonly Border _ring = new() { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(16), IsHitTestVisible = false, Opacity = 0 };
    private readonly Canvas _layer = new();   // hit-testable: its children (the card and its buttons) must be clickable; empty canvas areas are not hit
    private readonly Border _card = new() { CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 16, 18, 14), Width = CardW, BorderThickness = new Thickness(1), IsHitTestVisible = true };
    private readonly MascotView _mascot = new() { Width = 96, Height = 88, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Classes = { "headline" } };
    private readonly TextBlock _body = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } };
    private readonly StackPanel _points = new() { Spacing = 5 };
    private readonly TextBlock _tip = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back = new() { Content = "Back", Padding = new Thickness(14, 6) };
    private readonly Button _next = new() { Classes = { "pill" }, Padding = new Thickness(18, 7) };
    private readonly Button _skip = new() { Content = "Skip", Classes = { "plain" }, Padding = new Thickness(6, 6) };
    private readonly StackPanel _content = new() { Spacing = 8 };

    private int _i = -1, _token;
    private Rect _holeNow, _holeTo, _holeFrom;
    private Point _cardNow, _cardTo, _cardFrom;
    private bool _hasHole, _animating, _fresh;
    private TimeSpan _t0;

    public GuideOverlay(GuideDeck deck, string doneLabel, Func<string?, Control?> resolve, Action<string?> prepare, Action closed)
    {
        _deck = deck; _doneLabel = doneLabel; _resolve = resolve; _prepare = prepare; _closed = closed;
        Focusable = true;
        ZIndex = 1000;

        _ring.Bind(Border.BorderBrushProperty, _ring.GetResourceObservable("IosAccentBrush"));
        _card.Bind(Border.BackgroundProperty, _card.GetResourceObservable("IosCardBrush"));
        _card.Bind(Border.BorderBrushProperty, _card.GetResourceObservable("IosSeparatorBrush"));
        _card.BoxShadow = new BoxShadows(new BoxShadow { Blur = 40, OffsetY = 14, Color = Color.FromArgb(110, 0, 0, 0) });
        _tip.Foreground = new SolidColorBrush(ThemeEngine.CurrentAccent);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        head.Children.Add(_mascot);
        var texts = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _body } };
        Grid.SetColumn(texts, 1); head.Children.Add(texts);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        footer.Children.Add(_skip);
        Grid.SetColumn(_dots, 1); _dots.HorizontalAlignment = HorizontalAlignment.Center; footer.Children.Add(_dots);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _back, _next } };
        Grid.SetColumn(buttons, 2); footer.Children.Add(buttons);

        _content.Children.Add(head); _content.Children.Add(_points); _content.Children.Add(_tip); _content.Children.Add(footer);
        _content.Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(170) } };
        _card.Child = _content;

        _ring.Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) } };
        _layer.Children.Add(_ring); _layer.Children.Add(_card);
        Children.Add(_scrim); Children.Add(_layer);

        _back.Click += (_, _) => Go(-1);
        _next.Click += (_, _) => { if (_i >= _deck.Slides.Count - 1) Close(); else Go(1); };
        _skip.Click += (_, _) => Close();
        _scrim.PointerPressed += (_, _) => Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Right or Key.Enter or Key.Space) { if (_i >= _deck.Slides.Count - 1) Close(); else Go(1); e.Handled = true; }
            else if (e.Key == Key.Left) { Go(-1); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        SizeChanged += (_, _) => { if (_i >= 0) Place(_deck.Slides[_i], false); };
    }

    public void Begin()
    {
        Focus();
        Show(0);
    }

    private void Close() { _token++; _closed(); }
    private void Go(int d) => Show(Math.Clamp(_i + d, 0, _deck.Slides.Count - 1));

    private void Show(int i)
    {
        if (i == _i || _deck.Slides.Count == 0) return;
        var first = _i < 0;
        _i = i;
        var my = ++_token;
        var s = _deck.Slides[i];
        _prepare(s.Target);   // e.g. go back to Home before pointing at the drop box

        // the old words fade out, the new page is filled in, and the spotlight and card glide to the new place
        _content.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (my != _token) return;
            Fill(s);
            Place(s, !first);
            _content.Opacity = 1;
            for (var k = 0; k < _points.Children.Count; k++)
            {
                var row = _points.Children[k];
                DispatcherTimer.RunOnce(() => { if (my == _token) row.Opacity = 1; }, TimeSpan.FromMilliseconds(120 + k * 100));
            }
        }, TimeSpan.FromMilliseconds(first ? 30 : 170));
    }

    private void Fill(GuideSlide s)
    {
        _mascot.Mood = s.Mascot is "sleep" ? MascotMood.Float : MascotMood.Hop;
        _mascot.Mascot = s.Mascot;
        _title.Text = s.Title;
        _body.Text = s.Body; _body.IsVisible = s.Body.Length > 0;
        _points.Children.Clear();
        foreach (var b in s.Bullets)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Opacity = 0, Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(240) } } };
            row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(ThemeEngine.CurrentAccent), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0) });
            row.Children.Add(new TextBlock { Text = b, TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = CardW - 70 });
            _points.Children.Add(row);
        }
        _tip.IsVisible = !string.IsNullOrEmpty(s.Tip); _tip.Text = string.IsNullOrEmpty(s.Tip) ? "" : "Tip: " + s.Tip;
        _back.IsEnabled = _i > 0;
        _next.Content = _i >= _deck.Slides.Count - 1 ? _doneLabel : "Next";
        _skip.IsVisible = _i < _deck.Slides.Count - 1;
        BuildDots();
    }

    private void BuildDots()
    {
        _dots.Children.Clear();
        for (var k = 0; k < _deck.Slides.Count; k++)
        {
            var on = k == _i; var idx = k;
            var dot = new Border { Width = on ? 18 : 6, Height = 6, CornerRadius = new CornerRadius(3), Cursor = new Cursor(StandardCursorType.Hand), Background = new SolidColorBrush(on ? ThemeEngine.CurrentAccent : ThemeEngine.Current.Secondary, on ? 1 : 0.4) };
            dot.PointerPressed += (_, _) => Show(idx);
            _dots.Children.Add(dot);
        }
    }

    // ---------------------------------------------------------------- where things go

    private void Place(GuideSlide s, bool animate)
    {
        var size = Bounds.Size;
        if (size.Width < 50 || size.Height < 50) return;
        _card.Measure(new Size(CardW, double.PositiveInfinity));
        var cs = _card.DesiredSize;

        Rect? target = null;
        if (!string.IsNullOrEmpty(s.Target) && _resolve(s.Target) is { } c && c.IsEffectivelyVisible)
        {
            var m = c.TransformToVisual(this);
            if (m is not null) target = new Rect(new Point(), c.Bounds.Size).TransformToAABB(m.Value).Inflate(HolePad).Intersect(new Rect(size));
        }

        Point cardAt;
        if (target is { } t && t.Width > 4 && t.Height > 4)
        {
            // beside the target if there is room (right, left, below, above), otherwise in the middle
            if (t.Right + Gap + cs.Width + Edge <= size.Width) cardAt = new Point(t.Right + Gap, t.Center.Y - cs.Height / 2);
            else if (t.Left - Gap - cs.Width >= Edge) cardAt = new Point(t.Left - Gap - cs.Width, t.Center.Y - cs.Height / 2);
            else if (t.Bottom + Gap + cs.Height + Edge <= size.Height) cardAt = new Point(t.Center.X - cs.Width / 2, t.Bottom + Gap);
            else if (t.Top - Gap - cs.Height >= Edge) cardAt = new Point(t.Center.X - cs.Width / 2, t.Top - Gap - cs.Height);
            else cardAt = new Point((size.Width - cs.Width) / 2, (size.Height - cs.Height) / 2);
            cardAt = new Point(Math.Clamp(cardAt.X, Edge, Math.Max(Edge, size.Width - cs.Width - Edge)), Math.Clamp(cardAt.Y, Edge, Math.Max(Edge, size.Height - cs.Height - Edge)));
            _holeTo = t; _hasHole = true;
        }
        else
        {
            cardAt = new Point((size.Width - cs.Width) / 2, (size.Height - cs.Height) / 2);
            _holeTo = new Rect(size.Width / 2, size.Height / 2, 0, 0);
            _hasHole = false;
        }
        _cardTo = cardAt;

        if (!animate)
        {
            _holeNow = _holeTo; _cardNow = _cardTo; Apply();
            return;
        }
        _holeFrom = _holeNow; _cardFrom = _cardNow; _fresh = true;
        if (!_animating) { _animating = true; TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Tick); }
    }

    private void Tick(TimeSpan now)
    {
        if (_fresh) { _t0 = now; _fresh = false; }
        var k = Math.Clamp((now - _t0).TotalSeconds / Dur, 0, 1);
        var e = 1 - Math.Pow(1 - k, 3);   // ease out
        double L(double a, double b) => a + (b - a) * e;
        _holeNow = new Rect(L(_holeFrom.X, _holeTo.X), L(_holeFrom.Y, _holeTo.Y), L(_holeFrom.Width, _holeTo.Width), L(_holeFrom.Height, _holeTo.Height));
        _cardNow = new Point(L(_cardFrom.X, _cardTo.X), L(_cardFrom.Y, _cardTo.Y));
        Apply();
        if (k < 1) TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Tick); else _animating = false;
    }

    private void Apply()
    {
        var size = Bounds.Size;
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(size)));
        if (_hasHole || _holeNow.Width > 1) group.Children.Add(new RectangleGeometry(_holeNow) { RadiusX = 16, RadiusY = 16 });
        _scrim.Data = group;

        Canvas.SetLeft(_ring, _holeNow.X); Canvas.SetTop(_ring, _holeNow.Y);
        _ring.Width = Math.Max(0, _holeNow.Width); _ring.Height = Math.Max(0, _holeNow.Height);
        _ring.Opacity = _hasHole ? 1 : 0;
        Canvas.SetLeft(_card, _cardNow.X); Canvas.SetTop(_card, _cardNow.Y);
    }
}
