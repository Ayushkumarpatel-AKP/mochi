using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mochi.Services;

namespace Mochi.Views;

public sealed record Mark(string Kind, Point A, Point B, Color Ink, double Thick, string Text);

/// <summary>The picture with its marks. Sized in picture pixels so saving renders at full resolution.</summary>
public sealed class AnnotateCanvas : Control
{
    private readonly Bitmap _image;
    private readonly string _path;
    private Bitmap? _pixelated, _blurred;
    public List<Mark> Marks { get; } = new();
    public string Tool { get; set; } = "Arrow";
    public Color Ink { get; set; } = Color.Parse("#FF3B30");
    public double Thick { get; set; } = 6;
    public string TextValue { get; set; } = "Text";
    private Mark? _live;
    private Point _start;

    public AnnotateCanvas(Bitmap image, string path)
    {
        _image = image; _path = path;
        Width = image.PixelSize.Width;
        Height = image.PixelSize.Height;
    }

    private int NextNumber => Marks.Count(m => m.Kind == "Number") + 1;

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        _start = e.GetPosition(this);
        if (Tool is "Pixelate" or "Blur") { _pixelated ??= Filtered(true); _blurred ??= Filtered(false); }
        if (Tool == "Text") Marks.Add(new Mark("Text", _start, _start, Ink, Thick, TextValue));
        else if (Tool == "Number") Marks.Add(new Mark("Number", _start, _start, Ink, Thick, NextNumber.ToString()));
        else _live = new Mark(Tool, _start, _start, Ink, Thick, "");
        InvalidateVisual();
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        if (_live is null) return;
        _live = _live with { B = e.GetPosition(this) };
        InvalidateVisual();
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_live is not null && Math.Abs(_live.B.X - _live.A.X) + Math.Abs(_live.B.Y - _live.A.Y) > 4) Marks.Add(_live);
        _live = null;
        InvalidateVisual();
    }

    public void Undo()
    {
        if (Marks.Count > 0) Marks.RemoveAt(Marks.Count - 1);
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawImage(_image, new Rect(0, 0, Width, Height));
        foreach (var m in Marks) Draw(ctx, m);
        if (_live is not null) Draw(ctx, _live);
    }

    /// <summary>A whole-picture copy with the pixelate or blur effect; a rectangle of it is shown where the user marks.</summary>
    private Bitmap Filtered(bool pixelate)
    {
        using var src = SkiaSharp.SKBitmap.Decode(_path);
        using var dst = new SkiaSharp.SKBitmap(src.Width, src.Height);
        using (var c = new SkiaSharp.SKCanvas(dst))
        {
            if (pixelate)
            {
                var block = Math.Max(8, Math.Max(src.Width, src.Height) / 70);
                using var small = src.Resize(new SkiaSharp.SKImageInfo(Math.Max(1, src.Width / block), Math.Max(1, src.Height / block)), SkiaSharp.SKFilterQuality.Medium);
                using var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.None };
                c.DrawBitmap(small, new SkiaSharp.SKRect(0, 0, src.Width, src.Height), paint);
            }
            else
            {
                using var paint = new SkiaSharp.SKPaint { ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(Math.Max(src.Width, src.Height) / 90f, Math.Max(src.Width, src.Height) / 90f) };
                c.DrawBitmap(src, 0, 0, paint);
            }
        }
        using var data = SkiaSharp.SKImage.FromBitmap(dst).Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return new Bitmap(new MemoryStream(data.ToArray()));
    }

    private void Draw(DrawingContext ctx, Mark m)
    {
        if (m.Kind is "Pixelate" or "Blur")
        {
            var img = m.Kind == "Pixelate" ? _pixelated : _blurred;
            if (img is not null) using (ctx.PushClip(new Rect(m.A, m.B).Normalize())) ctx.DrawImage(img, new Rect(0, 0, Width, Height));
            return;
        }
        var brush = new SolidColorBrush(m.Ink);
        var pen = new Pen(brush, m.Thick, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var rect = new Rect(m.A, m.B).Normalize();
        switch (m.Kind)
        {
            case "Box":
                ctx.DrawRectangle(null, pen, rect);
                break;
            case "Highlight":
                ctx.DrawRectangle(new SolidColorBrush(m.Ink, 0.35), null, rect);
                break;
            case "Arrow":
                ctx.DrawLine(pen, m.A, m.B);
                var ang = Math.Atan2(m.B.Y - m.A.Y, m.B.X - m.A.X);
                var len = m.Thick * 4 + 12;
                Point H(double da) => new(m.B.X - len * Math.Cos(ang + da), m.B.Y - len * Math.Sin(ang + da));
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    g.BeginFigure(m.B, true);
                    g.LineTo(H(0.45)); g.LineTo(H(-0.45));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(brush, null, head);
                break;
            case "Text":
                var t = new FormattedText(m.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 14 + m.Thick * 3, brush);
                ctx.DrawText(t, m.A);
                break;
            case "Number":
                var r = 10 + m.Thick * 2;
                ctx.DrawEllipse(brush, null, m.A, r, r);
                var n = new FormattedText(m.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), r * 1.1, Brushes.White);
                ctx.DrawText(n, new Point(m.A.X - n.Width / 2, m.A.Y - n.Height / 2));
                break;
        }
    }
}

/// <summary>Draw arrows, boxes, highlights, text and numbers on a picture and save it as a new PNG.</summary>
public sealed class AnnotateWindow : Window
{
    public string? Saved { get; private set; }

    public AnnotateWindow(string path)
    {
        Title = Loc.T("x.ann.title") + " — " + Path.GetFileName(path);
        Width = 1000; Height = 720;
        var canvas = new AnnotateCanvas(new Bitmap(path), path);

        var toolIds = new[] { "Arrow", "Box", "Highlight", "Text", "Number", "Pixelate", "Blur" };
        var first = Math.Max(0, Array.IndexOf(toolIds, Services.Engines.ToolHooks.InitialTool ?? "Arrow"));
        Services.Engines.ToolHooks.InitialTool = null;
        var tool = new ComboBox { ItemsSource = new[] { "arrow", "box", "highlight", "text", "number", "pixelate", "blur" }.Select(k => Loc.T("x.ann." + k)).ToList(), SelectedIndex = first, Width = 130 };
        canvas.Tool = toolIds[first];
        tool.SelectionChanged += (_, _) => canvas.Tool = toolIds[Math.Max(0, tool.SelectedIndex)];

        var colors = new[] { ("🔴", "#FF3B30"), ("🟡", "#FFCC00"), ("🔵", "#0A84FF"), ("🟢", "#30D158"), ("⚫", "#000000"), ("⚪", "#FFFFFF") };
        var ink = new ComboBox { ItemsSource = colors.Select(c => c.Item1).ToList(), SelectedIndex = 0, Width = 90 };
        ink.SelectionChanged += (_, _) => canvas.Ink = Color.Parse(colors[ink.SelectedIndex].Item2);

        var thick = new Slider { Minimum = 2, Maximum = 24, Value = 6, Width = 130 };
        thick.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) canvas.Thick = thick.Value; };

        var text = new TextBox { Text = Loc.T("x.ann.text"), Width = 150, Watermark = Loc.T("x.ann.text_hint") };
        text.TextChanged += (_, _) => canvas.TextValue = text.Text ?? "";

        var undo = new Button { Content = Loc.T("x.ann.undo") };
        undo.Click += (_, _) => canvas.Undo();
        var cancel = new Button { Content = Loc.T("common.cancel") };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = Loc.T("common.save"), Classes = { "pill" } };
        save.Click += (_, _) =>
        {
            var dest = Util.Out(path, Path.GetDirectoryName(path)!, ".png", " (annotated)");
            var rtb = new RenderTargetBitmap(canvas.Bounds.Size is { Width: > 0 } ? new PixelSize((int)canvas.Width, (int)canvas.Height) : new PixelSize(1, 1), new Vector(96, 96));
            rtb.Render(canvas);
            rtb.Save(dest);
            Saved = dest;
            Close();
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12) };
        bar.Children.AddRange(new Control[] { tool, ink, new TextBlock { Text = Loc.T("x.ann.size"), VerticalAlignment = VerticalAlignment.Center }, thick, text, undo, cancel, save });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(12, 0, 12, 12), Child = canvas });
        Content = root;
    }

    /// <summary>Shows the window and completes when it closes, with the saved file or null.</summary>
    public static Task<string?> RunAsync(string path)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new AnnotateWindow(path);
        w.Closed += (_, _) => tcs.TrySetResult(w.Saved);
        w.Show();
        return tcs.Task;
    }
}
