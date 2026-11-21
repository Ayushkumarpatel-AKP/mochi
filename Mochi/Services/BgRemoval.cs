using System.Runtime.InteropServices;
using Avalonia.Platform;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Mochi.Services;

/// <param name="Ai">True: the U²-Net subject mask. False: distance from the background colour.</param>
/// <param name="Sens">AI cut-off, 0..1; lower keeps more of the soft edge.</param>
/// <param name="Tol">Colour tolerance in Lab units.</param>
/// <param name="Shift">Edge shift in picture pixels at the working size: negative shrinks the subject, positive grows it.</param>
public sealed record MatteParams(bool Ai, float Tol, float Sens, float Shift, float Feather, bool Contiguous, byte BgR, byte BgG, byte BgB);

/// <summary>Background removal: an AI subject mask (U²-Net-small, runs offline) or a colour key, then edge shift, feather and colour clean-up.</summary>
public static class Matte
{
    private static InferenceSession? _session;
    private static readonly object Gate = new();

    // ---------------------------------------------------------------- loading

    /// <summary>Decodes a picture upright into straight (unpremultiplied) BGRA bytes, scaled down to <paramref name="maxSide"/> when that is above zero.</summary>
    public static (byte[] Px, int W, int H) Load(string path, int maxSide)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("Cannot decode picture");
        using var raw = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul)) ?? throw new InvalidDataException("Cannot decode picture");
        using var up = Orient(raw, codec.EncodedOrigin);
        var s = maxSide > 0 ? Math.Min(1.0, maxSide / (double)Math.Max(up.Width, up.Height)) : 1.0;
        var w = Math.Max(1, (int)Math.Round(up.Width * s)); var h = Math.Max(1, (int)Math.Round(up.Height * s));
        using var fit = s < 1 ? Scaled(up, w, h) : null;
        var bytes = (fit ?? up).Bytes;
        for (var i = 0; i < bytes.Length; i += 4)   // premultiplied → straight
        {
            var a = bytes[i + 3];
            if (a == 0 || a == 255) continue;
            bytes[i] = (byte)Math.Min(255, bytes[i] * 255 / a); bytes[i + 1] = (byte)Math.Min(255, bytes[i + 1] * 255 / a); bytes[i + 2] = (byte)Math.Min(255, bytes[i + 2] * 255 / a);
        }
        return (bytes, w, h);
    }

    private static SKBitmap Scaled(SKBitmap b, int w, int h)
    {
        var r = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(r);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        c.DrawBitmap(b, new SKRect(0, 0, w, h), paint);
        return r;
    }

    private static SKBitmap Orient(SKBitmap b, SKEncodedOrigin o)
    {
        if (o is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return b.Copy();
        float w = b.Width, h = b.Height;
        var swap = o >= SKEncodedOrigin.LeftTop;
        var m = o switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            _ => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        };
        var r = new SKBitmap(swap ? b.Height : b.Width, swap ? b.Width : b.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(r);
        c.SetMatrix(m);
        c.DrawBitmap(b, 0, 0);
        return r;
    }

    // ---------------------------------------------------------------- the two masks

    private static InferenceSession Session()
    {
        lock (Gate)
        {
            if (_session is not null) return _session;
            using var s = AssetLoader.Open(new Uri("avares://Mochi/Assets/models/u2netp.onnx"));
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return _session = new InferenceSession(ms.ToArray());
        }
    }

    /// <summary>Subject probability per pixel (0..1) from U²-Net-small, upsampled to the picture size.</summary>
    public static float[] RunAi(byte[] px, int w, int h)
    {
        const int N = 320;
        using var src = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(px, 0, src.GetPixels(), px.Length);
        using var small = new SKBitmap(N, N, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(small))
        {
            c.Clear(SKColors.White);   // see-through pictures are judged on white
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            c.DrawBitmap(src, new SKRect(0, 0, N, N), paint);
        }
        var b = small.Bytes;
        float max = 1;
        for (var i = 0; i < b.Length; i += 4) max = Math.Max(max, Math.Max(b[i], Math.Max(b[i + 1], b[i + 2])));
        var t = new DenseTensor<float>(new[] { 1, 3, N, N });
        float[] mean = { 0.485f, 0.456f, 0.406f }, std = { 0.229f, 0.224f, 0.225f };
        for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
            {
                var i = (y * N + x) * 4;
                t[0, 0, y, x] = (b[i + 2] / max - mean[0]) / std[0];
                t[0, 1, y, x] = (b[i + 1] / max - mean[1]) / std[1];
                t[0, 2, y, x] = (b[i] / max - mean[2]) / std[2];
            }
        var sess = Session();
        float[] m;
        lock (Gate)
        {
            using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(sess.InputMetadata.Keys.First(), t) });
            m = res.First().AsTensor<float>().ToArray();
        }
        float lo = m.Min(), hi = m.Max();
        for (var i = 0; i < m.Length; i++) m[i] = (m[i] - lo) / Math.Max(1e-6f, hi - lo);
        return Resample(m, N, N, w, h);
    }

    /// <summary>The most common colour along the picture's border: the background to key out.</summary>
    public static (byte R, byte G, byte B) EstimateBg(byte[] px, int w, int h)
    {
        var n = new int[32768]; var sr = new long[32768]; var sg = new long[32768]; var sb = new long[32768];
        void Add(int x, int y)
        {
            var i = (y * w + x) * 4;
            if (px[i + 3] < 128) return;
            var k = (px[i + 2] >> 3) << 10 | (px[i + 1] >> 3) << 5 | px[i] >> 3;
            n[k]++; sr[k] += px[i + 2]; sg[k] += px[i + 1]; sb[k] += px[i];
        }
        for (var x = 0; x < w; x++) { Add(x, 0); Add(x, h - 1); if (h > 2) { Add(x, 1); Add(x, h - 2); } }
        for (var y = 0; y < h; y++) { Add(0, y); Add(w - 1, y); if (w > 2) { Add(1, y); Add(w - 2, y); } }
        var best = 0;
        for (var k = 1; k < n.Length; k++) if (n[k] > n[best]) best = k;
        return n[best] == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)(sr[best] / n[best]), (byte)(sg[best] / n[best]), (byte)(sb[best] / n[best]));
    }

    /// <summary>Colour key: 0 where a pixel matches the background (reached from the border unless <see cref="MatteParams.Contiguous"/> is off), 1 elsewhere, soft in between.</summary>
    public static float[] ColourMask(byte[] px, int w, int h, MatteParams p)
    {
        var n = w * h; var d = new float[n];
        Lab(p.BgR, p.BgG, p.BgB, out var l0, out var a0, out var b0);
        for (var i = 0; i < n; i++)
        {
            Lab(px[i * 4 + 2], px[i * 4 + 1], px[i * 4], out var l, out var a, out var b);
            d[i] = MathF.Sqrt((l - l0) * (l - l0) + (a - a0) * (a - a0) + (b - b0) * (b - b0));
        }
        float lo = p.Tol * 0.7f, hi = p.Tol * 1.3f + 0.01f;
        var m = new float[n];
        for (var i = 0; i < n; i++) m[i] = Smooth(lo, hi, d[i]);
        if (!p.Contiguous) return m;

        var reach = new bool[n]; var stack = new int[n]; var sp = 0;
        void Seed(int i) { if (!reach[i] && d[i] < hi) { reach[i] = true; stack[sp++] = i; } }
        for (var x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (var y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (sp > 0)
        {
            var i = stack[--sp]; int x = i % w, y = i / w;
            if (x > 0) Seed(i - 1); if (x < w - 1) Seed(i + 1); if (y > 0) Seed(i - w); if (y < h - 1) Seed(i + w);
        }
        for (var i = 0; i < n; i++) if (!reach[i]) m[i] = 1;
        return m;
    }

    /// <summary>Pixels joined to (x, y) whose colour is within <paramref name="tol"/> of it, softened by a pixel: the magic wand.</summary>
    public static float[] Wand(byte[] px, int w, int h, int sx, int sy, float tol)
    {
        var n = w * h; var m = new float[n];
        Lab(px[(sy * w + sx) * 4 + 2], px[(sy * w + sx) * 4 + 1], px[(sy * w + sx) * 4], out var l0, out var a0, out var b0);
        bool Close(int i)
        {
            Lab(px[i * 4 + 2], px[i * 4 + 1], px[i * 4], out var l, out var a, out var b);
            return (l - l0) * (l - l0) + (a - a0) * (a - a0) + (b - b0) * (b - b0) < tol * tol;
        }
        var stack = new int[n]; var sp = 0; m[sy * w + sx] = 1; stack[sp++] = sy * w + sx;
        void Go(int i) { if (m[i] == 0 && Close(i)) { m[i] = 1; stack[sp++] = i; } }
        while (sp > 0)
        {
            var i = stack[--sp]; int x = i % w, y = i / w;
            if (x > 0) Go(i - 1); if (x < w - 1) Go(i + 1); if (y > 0) Go(i - w); if (y < h - 1) Go(i + w);
        }
        return BoxBlur(m, w, h, 1);
    }

    // ---------------------------------------------------------------- refine and paint

    /// <summary>Turns a raw mask into the final matte: AI cut-off, edge shift, feather. <paramref name="scale"/> converts working-size pixels to this picture's.</summary>
    public static float[] Refine(float[] raw, int w, int h, MatteParams p, float scale)
    {
        var a = raw;
        if (p.Ai)
        {
            a = new float[raw.Length];
            float lo = Math.Clamp(p.Sens - 0.22f, 0, 1), hi = Math.Clamp(p.Sens + 0.22f, 0, 1) + 0.001f;
            for (var i = 0; i < a.Length; i++) a[i] = Smooth(lo, hi, raw[i]);
        }
        var s = (int)MathF.Round(p.Shift * scale);
        if (s != 0) a = MinMax(a, w, h, Math.Min(60, Math.Abs(s)), s > 0);
        var f = p.Feather * scale;
        if (f >= 0.5f) { var r = (int)MathF.Round(f); a = BoxBlur(BoxBlur(a, w, h, r), w, h, Math.Max(1, r / 2)); }
        return a;
    }

    /// <summary>Writes the cut-out into <paramref name="dst"/> (BGRA) for the rectangle x0..x1 × y0..y1, starting at (ox, oy) in a buffer dw wide.</summary>
    public static void Paint(byte[] px, int w, float[] eff, float[] matte, MatteParams p, bool premul, (byte R, byte G, byte B)? fill,
        byte[] dst, int dw, int ox, int oy, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                int i = y * w + x, o = ((y - oy) * dw + (x - ox)) * 4;
                var a = eff[i] * px[i * 4 + 3] / 255f;
                float b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
                var m = matte[i];
                if (!p.Ai && m > 0.02f && m < 0.999f)   // edge pixels carry a little of the old background: take it back out
                {
                    var k = 1 / m;
                    r = Math.Clamp((r - p.BgR * (1 - m)) * k, 0, 255); g = Math.Clamp((g - p.BgG * (1 - m)) * k, 0, 255); b = Math.Clamp((b - p.BgB * (1 - m)) * k, 0, 255);
                }
                if (fill is { } f)
                {
                    dst[o] = (byte)(b * a + f.B * (1 - a) + 0.5f); dst[o + 1] = (byte)(g * a + f.G * (1 - a) + 0.5f); dst[o + 2] = (byte)(r * a + f.R * (1 - a) + 0.5f); dst[o + 3] = 255;
                }
                else if (premul)
                {
                    dst[o] = (byte)(b * a + 0.5f); dst[o + 1] = (byte)(g * a + 0.5f); dst[o + 2] = (byte)(r * a + 0.5f); dst[o + 3] = (byte)(a * 255 + 0.5f);
                }
                else { dst[o] = (byte)b; dst[o + 1] = (byte)g; dst[o + 2] = (byte)r; dst[o + 3] = (byte)(a * 255 + 0.5f); }
            }
    }

    public static void SavePng(byte[] straightBgra, int w, int h, string dest)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(straightBgra, 0, bmp.GetPixels(), straightBgra.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(dest);
        data.SaveTo(fs);
    }

    // ---------------------------------------------------------------- maths

    public static float Smooth(float lo, float hi, float x)
    {
        var t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    public static float[] Resample(float[] a, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return (float[])a.Clone();
        var r = new float[dw * dh];
        float fx = sw / (float)dw, fy = sh / (float)dh;
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Clamp((y + 0.5f) * fy - 0.5f, 0, sh - 1); var y0 = (int)sy; var y1 = Math.Min(sh - 1, y0 + 1); var ty = sy - y0;
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Clamp((x + 0.5f) * fx - 0.5f, 0, sw - 1); var x0 = (int)sx; var x1 = Math.Min(sw - 1, x0 + 1); var tx = sx - x0;
                r[y * dw + x] = (a[y0 * sw + x0] * (1 - tx) + a[y0 * sw + x1] * tx) * (1 - ty) + (a[y1 * sw + x0] * (1 - tx) + a[y1 * sw + x1] * tx) * ty;
            }
        }
        return r;
    }

    private static readonly float[] Lin = Enumerable.Range(0, 256).Select(i => { var c = i / 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }).ToArray();

    public static void Lab(byte r, byte g, byte b, out float L, out float A, out float B)
    {
        float lr = Lin[r], lg = Lin[g], lb = Lin[b];
        float x = (0.4124f * lr + 0.3576f * lg + 0.1805f * lb) / 0.95047f, y = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb, z = (0.0193f * lr + 0.1192f * lg + 0.9505f * lb) / 1.08883f;
        static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        float fx = F(x), fy = F(y), fz = F(z);
        L = 116 * fy - 16; A = 500 * (fx - fy); B = 200 * (fy - fz);
    }

    public static float[] BoxBlur(float[] a, int w, int h, int r)
    {
        if (r < 1) return a;
        var t = new float[a.Length]; var o = new float[a.Length]; var win = 2 * r + 1f;
        for (var y = 0; y < h; y++)
        {
            float sum = 0; var row = y * w;
            for (var k = -r; k <= r; k++) sum += a[row + Math.Clamp(k, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                t[row + x] = sum / win;
                sum += a[row + Math.Min(w - 1, x + r + 1)] - a[row + Math.Max(0, x - r)];
            }
        }
        for (var x = 0; x < w; x++)
        {
            float sum = 0;
            for (var k = -r; k <= r; k++) sum += t[Math.Clamp(k, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                o[y * w + x] = sum / win;
                sum += t[Math.Min(h - 1, y + r + 1) * w + x] - t[Math.Max(0, y - r) * w + x];
            }
        }
        return o;
    }

    private static float[] MinMax(float[] a, int w, int h, int r, bool max)
    {
        var t = new float[a.Length]; var o = new float[a.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = a[y * w + x];
                for (var k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) v = max ? Math.Max(v, a[y * w + k]) : Math.Min(v, a[y * w + k]);
                t[y * w + x] = v;
            }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = t[y * w + x];
                for (var k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) v = max ? Math.Max(v, t[k * w + x]) : Math.Min(v, t[k * w + x]);
                o[y * w + x] = v;
            }
        return o;
    }
}
