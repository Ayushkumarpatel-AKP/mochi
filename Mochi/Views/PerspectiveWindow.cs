using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mochi.Services;
using SkiaSharp;

namespace Mochi.Views;

/// <summary>The picture with four corner handles to drag onto the corners of the thing to straighten.</summary>
public sealed class PerspectiveCanvas : Control
{
    private readonly Bitmap _image;
    public Point[] Corners { get; }   // top-left, top-right, bottom-right, bottom-left, in picture pixels
    private int _drag = -1;

    public PerspectiveCanvas(Bitmap image)
    {
        _image = image;
        Width = image.PixelSize.Width; Height = image.PixelSize.Height;
        double w = Width, h = Height;
        Corners = new[] { new Point(w * .1, h * .1), new Point(w * .9, h * .1), new Point(w * .9, h * .9), new Point(w * .1, h * .9) };
    }

    private double Grab => Math.Max(Width, Height) * 0.012;

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        var best = Enumerable.Range(0, 4).OrderBy(i => Dist(Corners[i], p)).First();
        _drag = Dist(Corners[best], p) < Grab * 3 ? best : -1;
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        if (_drag < 0) return;
        var p = e.GetPosition(this);
        Corners[_drag] = new Point(Math.Clamp(p.X, 0, Width), Math.Clamp(p.Y, 0, Height));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e) => _drag = -1;

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawImage(_image, new Rect(0, 0, Width, Height));
        var accent = Color.TryParse(App.Settings.Accent, out var c) ? c : Colors.DodgerBlue;
        var pen = new Pen(new SolidColorBrush(accent), Grab / 4);
        for (var i = 0; i < 4; i++) ctx.DrawLine(pen, Corners[i], Corners[(i + 1) % 4]);
        foreach (var p in Corners)
        {
            ctx.DrawEllipse(Brushes.White, pen, p, Grab, Grab);
            ctx.DrawEllipse(new SolidColorBrush(accent), null, p, Grab / 3, Grab / 3);
        }
    }
}

/// <summary>Straightens a photographed document, sign or screen: drag the four corners, then crop.</summary>
public sealed class PerspectiveWindow : Window
{
    public string? Saved { get; private set; }

    public PerspectiveWindow(string path)
    {
        Title = Loc.T("x.persp.title") + " — " + Path.GetFileName(path);
        Width = 1000; Height = 720;
        var canvas = new PerspectiveCanvas(new Bitmap(path));

        var cancel = new Button { Content = Loc.T("common.cancel") };
        cancel.Click += (_, _) => Close();
        var crop = new Button { Content = Loc.T("x.persp.crop"), Classes = { "pill" } };
        crop.Click += (_, _) =>
        {
            using var src = SKBitmap.Decode(path);
            var q = canvas.Corners.Select(p => new SKPoint((float)p.X, (float)p.Y)).ToArray();
            using var warped = PerspectiveMath.Warp(src, q);
            var dest = Util.Out(path, Path.GetDirectoryName(path)!, ".png", " (straightened)");
            ImageEngineSave(warped, dest);
            Saved = dest;
            Close();
        };

        var hint = new TextBlock { Text = Loc.T("x.persp.hint"), VerticalAlignment = VerticalAlignment.Center, Classes = { "secondary" } };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12), Children = { hint, cancel, crop } };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(12, 0, 12, 12), Child = canvas });
        Content = root;
    }

    private static void ImageEngineSave(SKBitmap b, string dest)
    {
        using var img = SKImage.FromBitmap(b);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(dest);
        data.SaveTo(fs);
    }

    public static Task<string?> RunAsync(string path)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new PerspectiveWindow(path);
        w.Closed += (_, _) => tcs.TrySetResult(w.Saved);
        w.Show();
        return tcs.Task;
    }
}

/// <summary>The projective warp behind perspective crop: maps a quadrilateral of the picture onto a straight rectangle.</summary>
public static class PerspectiveMath
{
    /// <param name="q">Top-left, top-right, bottom-right, bottom-left corners in picture pixels.</param>
    public static SKBitmap Warp(SKBitmap src, SKPoint[] q)
    {
        static float Len(SKPoint a, SKPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        var w = Math.Max(2, (int)Math.Round((Len(q[0], q[1]) + Len(q[3], q[2])) / 2));
        var h = Math.Max(2, (int)Math.Round((Len(q[0], q[3]) + Len(q[1], q[2])) / 2));

        // Heckbert's unit-square-to-quadrilateral projective mapping
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3, dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        double g, hh, a, b, d, e;
        if (Math.Abs(dx3) < 1e-9 && Math.Abs(dy3) < 1e-9) { g = hh = 0; a = x1 - x0; b = x3 - x0; d = y1 - y0; e = y3 - y0; }
        else
        {
            var den = dx1 * dy2 - dy1 * dx2;
            g = (dx3 * dy2 - dy3 * dx2) / den; hh = (dx1 * dy3 - dy1 * dx3) / den;
            a = x1 - x0 + g * x1; b = x3 - x0 + hh * x3; d = y1 - y0 + g * y1; e = y3 - y0 + hh * y3;
        }

        using var s = src.Copy(SKColorType.Bgra8888);
        var sp = s.Bytes; int sw = s.Width, sh = s.Height;
        var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var dp = new byte[w * h * 4];
        for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                double u = (i + 0.5) / w, v = (j + 0.5) / h, k = g * u + hh * v + 1;
                double X = (a * u + b * v + x0) / k - 0.5, Y = (d * u + e * v + y0) / k - 0.5;
                int ix = (int)Math.Floor(X), iy = (int)Math.Floor(Y); double fx = X - ix, fy = Y - iy;
                for (var c = 0; c < 4; c++)
                {
                    double P(int px, int py) => sp[(Math.Clamp(py, 0, sh - 1) * sw + Math.Clamp(px, 0, sw - 1)) * 4 + c];
                    var val = P(ix, iy) * (1 - fx) * (1 - fy) + P(ix + 1, iy) * fx * (1 - fy) + P(ix, iy + 1) * (1 - fx) * fy + P(ix + 1, iy + 1) * fx * fy;
                    dp[(j * w + i) * 4 + c] = (byte)Math.Clamp(val + 0.5, 0, 255);
                }
            }
        System.Runtime.InteropServices.Marshal.Copy(dp, 0, dst.GetPixels(), dp.Length);
        return dst;
    }
}
