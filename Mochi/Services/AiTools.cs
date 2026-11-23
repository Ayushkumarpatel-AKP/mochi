using System.Runtime.InteropServices;
using Avalonia.Platform;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Mochi.Services;

/// <summary>The offline picture helpers: enlarge, blur faces, auto-enhance, erase an object, read text and QR codes. Models are bundled in the app.</summary>
public static class AiTools
{
    private static readonly Dictionary<string, InferenceSession> Sessions = new();
    private static readonly object Gate = new();

    private static InferenceSession Session(string file)
    {
        lock (Gate)
        {
            if (Sessions.TryGetValue(file, out var s)) return s;
            byte[] bytes;
            try
            {
                using var st = AssetLoader.Open(new Uri("avares://Mochi/Assets/models/" + file));
                using var ms = new MemoryStream();
                st.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch (InvalidOperationException) when (Environment.GetEnvironmentVariable("MOCHI_MODELS") is { } dir)   // command-line runs have no asset loader
            {
                bytes = File.ReadAllBytes(Path.Combine(dir, file));
            }
            return Sessions[file] = new InferenceSession(bytes);
        }
    }

    // ================================================================ enlarge

    public const long MaxEnlargePixels = 4_200_000;

    /// <summary>Enlarges 2× or 4× with Real-ESRGAN (general, small), tile by tile so memory stays low. Straight BGRA in and out.</summary>
    public static (byte[] Px, int W, int H) Enlarge(byte[] px, int w, int h, int scale, Action<double>? progress = null)
    {
        if ((long)w * h > MaxEnlargePixels)
            throw new NotSupportedException($"This picture is {(long)w * h / 1e6:0.#} megapixels. The enlarger works up to 4 megapixels, so shrink it first or pick a smaller picture.");
        const int T = 128, P = 10;
        var sess = Session("realesr-general-x4v3.onnx");
        var input = sess.InputMetadata.Keys.First();
        int ow = w * 4, oh = h * 4;
        var o = new byte[ow * oh * 4];
        var tiles = ((w + T - 1) / T) * ((h + T - 1) / T); var done = 0;

        for (var ty = 0; ty < h; ty += T)
            for (var tx = 0; tx < w; tx += T)
            {
                int x1 = Math.Min(w, tx + T), y1 = Math.Min(h, ty + T);
                int ix0 = Math.Max(0, tx - P), iy0 = Math.Max(0, ty - P), ix1 = Math.Min(w, x1 + P), iy1 = Math.Min(h, y1 + P);
                int tw = ix1 - ix0, th = iy1 - iy0;
                var t = new DenseTensor<float>(new[] { 1, 3, th, tw });
                for (var y = 0; y < th; y++)
                    for (var x = 0; x < tw; x++)
                    {
                        var i = ((iy0 + y) * w + ix0 + x) * 4;
                        var a = px[i + 3] / 255f; var bg = 1 - a;   // see-through pixels are judged on white
                        t[0, 0, y, x] = px[i + 2] / 255f * a + bg; t[0, 1, y, x] = px[i + 1] / 255f * a + bg; t[0, 2, y, x] = px[i] / 255f * a + bg;
                    }
                float[] r;
                lock (Gate)
                {
                    using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(input, t) });
                    r = res.First().AsTensor<float>().ToArray();
                }
                int ow2 = tw * 4, oh2 = th * 4, plane = ow2 * oh2;
                for (var y = ty * 4; y < y1 * 4; y++)
                    for (var x = tx * 4; x < x1 * 4; x++)
                    {
                        var sx = x - ix0 * 4; var sy = y - iy0 * 4;
                        var oi = (y * ow + x) * 4; var si = sy * ow2 + sx;
                        o[oi + 2] = (byte)Math.Clamp(r[si] * 255f + 0.5f, 0, 255);
                        o[oi + 1] = (byte)Math.Clamp(r[plane + si] * 255f + 0.5f, 0, 255);
                        o[oi] = (byte)Math.Clamp(r[2 * plane + si] * 255f + 0.5f, 0, 255);
                        o[oi + 3] = 255;
                    }
                progress?.Invoke(++done / (double)tiles);
            }

        if (px.Where((_, i) => i % 4 == 3).Any(a => a < 255))   // keep transparency: stretch the old alpha smoothly
        {
            var alpha = new float[w * h];
            for (var i = 0; i < alpha.Length; i++) alpha[i] = px[i * 4 + 3];
            var big = Matte.Resample(alpha, w, h, ow, oh);
            for (var i = 0; i < big.Length; i++) o[i * 4 + 3] = (byte)Math.Clamp(big[i] + 0.5f, 0, 255);
        }
        if (scale == 4) return (o, ow, oh);
        return Resize(o, ow, oh, w * 2, h * 2);   // 2×: take the 4× result down, which is sharper than enlarging 2× directly
    }

    public static (byte[] Px, int W, int H) Resize(byte[] px, int w, int h, int nw, int nh)
    {
        using var src = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(px, 0, src.GetPixels(), px.Length);
        using var dst = new SKBitmap(nw, nh, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(dst))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
            c.DrawBitmap(src, new SKRect(0, 0, nw, nh), paint);
        var b = dst.Bytes;
        for (var i = 0; i < b.Length; i += 4)
        {
            var a = b[i + 3];
            if (a == 0 || a == 255) continue;
            b[i] = (byte)Math.Min(255, b[i] * 255 / a); b[i + 1] = (byte)Math.Min(255, b[i + 1] * 255 / a); b[i + 2] = (byte)Math.Min(255, b[i + 2] * 255 / a);
        }
        return (b, nw, nh);
    }

    // ================================================================ faces

    /// <summary>Face boxes in picture pixels, found with a tiny 1 MB detector.</summary>
    public static List<SKRect> FindFaces(byte[] px, int w, int h, float threshold = 0.78f)
    {
        const int W = 320, H = 240;
        var (small, _, _) = Resize(px, w, h, W, H);
        var t = new DenseTensor<float>(new[] { 1, 3, H, W });
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4; var a = small[i + 3] / 255f;
                t[0, 0, y, x] = (small[i + 2] * a + 255 * (1 - a) - 127) / 128f; t[0, 1, y, x] = (small[i + 1] * a + 255 * (1 - a) - 127) / 128f; t[0, 2, y, x] = (small[i] * a + 255 * (1 - a) - 127) / 128f;
            }
        var sess = Session("face.onnx");
        float[] scores, boxes;
        lock (Gate)
        {
            using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(sess.InputMetadata.Keys.First(), t) });
            var outs = res.ToList();
            scores = outs.First(o => o.Name == "scores").AsTensor<float>().ToArray();
            boxes = outs.First(o => o.Name == "boxes").AsTensor<float>().ToArray();
        }
        var cand = new List<(float S, SKRect R)>();
        for (var i = 0; i < scores.Length / 2; i++)
            if (scores[i * 2 + 1] > threshold)
                cand.Add((scores[i * 2 + 1], new SKRect(boxes[i * 4] * w, boxes[i * 4 + 1] * h, boxes[i * 4 + 2] * w, boxes[i * 4 + 3] * h)));
        var keep = new List<SKRect>();
        foreach (var (s, r) in cand.OrderByDescending(c => c.S))
            if (keep.All(k => Iou(k, r) < 0.3f)) keep.Add(r);
        return keep;
    }

    /// <summary>
    /// Faces found with YuNet (a 230 KB detector that keeps small faces better than <see cref="FindFaces"/>). The picture is fitted into a
    /// 640 square; callers look at parts of a big picture one by one so a small face is still big enough to see.
    /// </summary>
    public static List<(float Score, SKRect Box)> FindFacesYunet(byte[] px, int w, int h, float threshold = 0.5f)
    {
        const int N = 640;
        var k = N / (double)Math.Max(w, h);
        int nw = Math.Max(1, (int)(w * k)), nh = Math.Max(1, (int)(h * k));
        var (small, _, _) = Resize(px, w, h, nw, nh);
        var t = new DenseTensor<float>(new[] { 1, 3, N, N });
        for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                var i = (y * nw + x) * 4; var a = small[i + 3] / 255f;
                t[0, 0, y, x] = small[i] * a + 255 * (1 - a); t[0, 1, y, x] = small[i + 1] * a + 255 * (1 - a); t[0, 2, y, x] = small[i + 2] * a + 255 * (1 - a);
            }
        var sess = Session("yunet.onnx");
        var o = new Dictionary<string, float[]>();
        lock (Gate)
        {
            using var res = sess.Run(new[] { NamedOnnxValue.CreateFromTensor("input", t) });
            foreach (var r in res) o[r.Name] = r.AsTensor<float>().ToArray();
        }
        var cand = new List<(float S, SKRect R)>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cls = o["cls_" + stride]; var obj = o["obj_" + stride]; var bb = o["bbox_" + stride];
            var n = N / stride;
            for (var i = 0; i < cls.Length; i++)
            {
                var sc = (float)Math.Sqrt(Math.Clamp(cls[i], 0f, 1f) * Math.Clamp(obj[i], 0f, 1f));
                if (sc < threshold) continue;
                int row = i / n, col = i % n;
                var cx = (col + bb[i * 4]) * stride; var cy = (row + bb[i * 4 + 1]) * stride;
                var bw = (float)Math.Exp(bb[i * 4 + 2]) * stride; var bh = (float)Math.Exp(bb[i * 4 + 3]) * stride;
                cand.Add((sc, new SKRect((float)((cx - bw / 2) / k), (float)((cy - bh / 2) / k), (float)((cx + bw / 2) / k), (float)((cy + bh / 2) / k))));
            }
        }
        var keep = new List<(float, SKRect)>();
        foreach (var c in cand.OrderByDescending(c => c.S))
            if (keep.All(q => Iou(q.Item2, c.R) < 0.3f)) keep.Add(c);
        return keep;
    }

    private static float Iou(SKRect a, SKRect b)
    {
        var i = SKRect.Intersect(a, b);
        if (i.Width <= 0 || i.Height <= 0) return 0;
        var inter = i.Width * i.Height;
        return inter / (a.Width * a.Height + b.Width * b.Height - inter);
    }

    /// <summary>Blurs each box (an oval a little bigger than the face) so nobody can be recognised.</summary>
    public static SKBitmap BlurRegions(byte[] px, int w, int h, IEnumerable<SKRect> boxes)
    {
        using var src = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(px, 0, src.GetPixels(), px.Length);
        var result = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(result);
        c.DrawBitmap(src, 0, 0);
        foreach (var b in boxes)
        {
            var r = b; r.Inflate(b.Width * 0.08f, b.Height * 0.12f);
            using var oval = new SKPath(); oval.AddOval(r);
            var sigma = Math.Max(6f, Math.Max(r.Width, r.Height) / 4f);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma), IsAntialias = true };
            c.Save(); c.ClipPath(oval, SKClipOperation.Intersect, true);
            c.DrawBitmap(src, 0, 0, paint);
            c.Restore();
        }
        return result;
    }

    // ================================================================ enhance

    /// <summary>One-button fix: stretches the tones, evens out a colour cast, lifts dull colours and sharpens a touch. Straight BGRA, in place.</summary>
    public static void Enhance(byte[] px, int w, int h)
    {
        var n = w * h; var hist = new int[256]; double mr = 0, mg = 0, mb = 0; long cnt = 0;
        for (var i = 0; i < n; i++)
        {
            if (px[i * 4 + 3] < 8) continue;
            float b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
            hist[(int)Math.Clamp(0.2126f * r + 0.7152f * g + 0.0722f * b, 0, 255)]++;
            mr += r; mg += g; mb += b; cnt++;
        }
        if (cnt == 0) return;
        int lo = 0, hi = 255; long acc = 0;
        while (lo < 254 && acc + hist[lo] < cnt * 0.004) acc += hist[lo++];
        acc = 0; while (hi > 1 && acc + hist[hi] < cnt * 0.004) acc += hist[hi--];
        if (hi - lo < 40) { lo = 0; hi = 255; }
        mr /= cnt; mg /= cnt; mb /= cnt;
        var m = (mr + mg + mb) / 3;
        float gr = (float)Math.Clamp(m / Math.Max(1, mr), 0.95, 1.05), gg = (float)Math.Clamp(m / Math.Max(1, mg), 0.95, 1.05), gb = (float)Math.Clamp(m / Math.Max(1, mb), 0.95, 1.05);
        var meanY = Math.Clamp(((0.2126 * mr * gr + 0.7152 * mg * gg + 0.0722 * mb * gb) - lo) / Math.Max(1, hi - lo), 0.05, 0.95);
        var gamma = (float)Math.Clamp(Math.Log(0.46) / Math.Log(meanY), 0.8, 1.25);   // brighten a dark picture, calm a washed-out one

        var tmp = new byte[px.Length];
        for (var i = 0; i < n; i++)
        {
            float b = (px[i * 4] * gb - lo) / (hi - lo), g = (px[i * 4 + 1] * gg - lo) / (hi - lo), r = (px[i * 4 + 2] * gr - lo) / (hi - lo);
            b = MathF.Pow(Math.Clamp(b, 0, 1), 1f / gamma); g = MathF.Pow(Math.Clamp(g, 0, 1), 1f / gamma); r = MathF.Pow(Math.Clamp(r, 0, 1), 1f / gamma);
            float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            var sat = mx <= 0 ? 0 : (mx - mn) / mx;
            var boost = 1 + 0.32f * (1 - sat);   // duller colours get more of a lift than vivid ones
            var gray = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            r = Math.Clamp(gray + (r - gray) * boost, 0, 1); g = Math.Clamp(gray + (g - gray) * boost, 0, 1); b = Math.Clamp(gray + (b - gray) * boost, 0, 1);
            tmp[i * 4] = (byte)(b * 255 + 0.5f); tmp[i * 4 + 1] = (byte)(g * 255 + 0.5f); tmp[i * 4 + 2] = (byte)(r * 255 + 0.5f); tmp[i * 4 + 3] = px[i * 4 + 3];
        }
        // gentle unsharp mask: add back a third of what a 3×3 blur takes away
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    float sum = 0; var k = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int yy = Math.Clamp(y + dy, 0, h - 1), xx = Math.Clamp(x + dx, 0, w - 1);
                            sum += tmp[(yy * w + xx) * 4 + c]; k++;
                        }
                    var v = tmp[i + c] + 0.35f * (tmp[i + c] - sum / k);
                    px[i + c] = (byte)Math.Clamp(v + 0.5f, 0, 255);
                }
                px[i + 3] = tmp[i + 3];
            }
    }

    // ================================================================ erase an object

    /// <summary>Fills the masked pixels (mask byte &gt; 0) from their surroundings: smooth multi-scale fill plus matching film grain. Straight BGRA, in place.</summary>
    public static void Inpaint(byte[] px, int w, int h, byte[] mask)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (mask[y * w + x] > 0) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX < 0) return;

        var margin = Math.Max(24, Math.Max(maxX - minX, maxY - minY) / 2);
        int x0 = Math.Max(0, minX - margin), y0 = Math.Max(0, minY - margin), x1 = Math.Min(w, maxX + margin + 1), y1 = Math.Min(h, maxY + margin + 1);
        int cw = x1 - x0, ch = y1 - y0;

        // the hole, grown by 2 pixels so the halo around a brush stroke goes too
        var hole = new float[cw * ch];
        for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
                if (mask[(y0 + y) * w + x0 + x] > 0) hole[y * cw + x] = 1;
        hole = Matte.BoxBlur(hole, cw, ch, 2);
        for (var i = 0; i < hole.Length; i++) hole[i] = hole[i] > 0.02f ? 1 : 0;

        // level 0: sums of known colour and weight
        var levels = new List<(float[] R, float[] G, float[] B, float[] W, int W0, int H0)>();
        var lr = new float[cw * ch]; var lg = new float[cw * ch]; var lb = new float[cw * ch]; var lw = new float[cw * ch];
        for (var i = 0; i < cw * ch; i++)
        {
            if (hole[i] > 0) continue;
            int y = i / cw, x = i % cw; var s = ((y0 + y) * w + x0 + x) * 4;
            lr[i] = px[s + 2]; lg[i] = px[s + 1]; lb[i] = px[s]; lw[i] = 1;
        }
        levels.Add((lr, lg, lb, lw, cw, ch));
        while (levels[^1].W0 > 1 || levels[^1].H0 > 1)
        {
            var (r, g, b, ww, w0, h0) = levels[^1];
            int nw = (w0 + 1) / 2, nh = (h0 + 1) / 2;
            var nr = new float[nw * nh]; var ng = new float[nw * nh]; var nb = new float[nw * nh]; var nww = new float[nw * nh];
            for (var y = 0; y < h0; y++)
                for (var x = 0; x < w0; x++)
                {
                    var i = y * w0 + x; var j = (y / 2) * nw + x / 2;
                    nr[j] += r[i]; ng[j] += g[i]; nb[j] += b[i]; nww[j] += ww[i];
                }
            levels.Add((nr, ng, nb, nww, nw, nh));
        }

        // pull back down, filling what has no known pixels from the coarser level
        float[]? pr = null, pg = null, pb = null; int pw = 0, ph = 0;
        for (var L = levels.Count - 1; L >= 0; L--)
        {
            var (r, g, b, ww, w0, h0) = levels[L];
            var fr = new float[w0 * h0]; var fg = new float[w0 * h0]; var fb = new float[w0 * h0];
            for (var y = 0; y < h0; y++)
                for (var x = 0; x < w0; x++)
                {
                    var i = y * w0 + x;
                    if (ww[i] > 0) { fr[i] = r[i] / ww[i]; fg[i] = g[i] / ww[i]; fb[i] = b[i] / ww[i]; }
                    else if (pr is not null)
                    {
                        float sx = Math.Clamp((x + 0.5f) / 2 - 0.5f, 0, pw - 1), sy = Math.Clamp((y + 0.5f) / 2 - 0.5f, 0, ph - 1);
                        int ax = (int)sx, ay = (int)sy, bx = Math.Min(pw - 1, ax + 1), by = Math.Min(ph - 1, ay + 1); float tx = sx - ax, ty = sy - ay;
                        float Bl(float[] a) => (a[ay * pw + ax] * (1 - tx) + a[ay * pw + bx] * tx) * (1 - ty) + (a[by * pw + ax] * (1 - tx) + a[by * pw + bx] * tx) * ty;
                        fr[i] = Bl(pr); fg[i] = Bl(pg!); fb[i] = Bl(pb!);
                    }
                }
            pr = fr; pg = fg; pb = fb; pw = w0; ph = h0;
        }

        // film grain like the picture's own, measured on known pixels
        double dev = 0; long dn = 0;
        for (var y = 1; y < ch - 1; y += 2)
            for (var x = 1; x < cw - 1; x += 2)
            {
                var i = y * cw + x;
                if (hole[i] > 0 || hole[i - 1] > 0 || hole[i + 1] > 0 || hole[i - cw] > 0 || hole[i + cw] > 0) continue;
                var s = ((y0 + y) * w + x0 + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var avg = (px[s + c - 4] + px[s + c + 4] + px[s + c - w * 4] + px[s + c + w * 4]) / 4f;
                    dev += (px[s + c] - avg) * (px[s + c] - avg); dn++;
                }
            }
        var sigma = dn > 0 ? (float)Math.Sqrt(dev / dn) * 0.75f : 0f;
        var rnd = new Random(7);

        var soft = Matte.BoxBlur(hole, cw, ch, 1);   // a soft edge, so the patch doesn't show
        for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                var i = y * cw + x; var a = Math.Clamp(soft[i] * 1.4f, 0, 1);
                if (a <= 0) continue;
                var s = ((y0 + y) * w + x0 + x) * 4;
                var noise = sigma <= 0 ? 0f : (float)((rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5) * 1.15) * sigma;
                px[s + 2] = (byte)Math.Clamp(px[s + 2] * (1 - a) + (pr![i] + noise) * a + 0.5f, 0, 255);
                px[s + 1] = (byte)Math.Clamp(px[s + 1] * (1 - a) + (pg![i] + noise) * a + 0.5f, 0, 255);
                px[s] = (byte)Math.Clamp(px[s] * (1 - a) + (pb![i] + noise) * a + 0.5f, 0, 255);
            }
    }

    // ================================================================ read text and codes

    /// <summary>Text in the picture, read by the recogniser that ships with Windows (no install, works offline).</summary>
    public static string ReadText(byte[] px, int w, int h)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                     ?? throw new NotSupportedException("Windows has no text-recognition language installed. Add one in Settings → Time & language → Language.");
        var max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        if (Math.Max(w, h) > max) { var k = max / (double)Math.Max(w, h); (px, w, h) = Resize(px, w, h, Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k))); }

        // on white, so see-through pictures are readable
        var flat = new byte[px.Length];
        for (var i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3] / 255f;
            flat[i] = (byte)(px[i] * a + 255 * (1 - a)); flat[i + 1] = (byte)(px[i + 1] * a + 255 * (1 - a)); flat[i + 2] = (byte)(px[i + 2] * a + 255 * (1 - a)); flat[i + 3] = 255;
        }
        var sb = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(flat),
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);
        var result = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }

    /// <summary>QR codes and bar codes found in the picture.</summary>
    public static List<string> ReadCodes(byte[] px, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        var reader = new ZXing.SkiaSharp.BarcodeReader { AutoRotate = true, Options = { TryHarder = true, TryInverted = true } };
        var found = reader.DecodeMultiple(bmp) ?? Array.Empty<ZXing.Result>();
        return found.Select(r => r.Text).Distinct().ToList();
    }

    public static SKBitmap MakeQr(string text, int size = 1024)
    {
        var writer = new ZXing.SkiaSharp.BarcodeWriter
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions { Width = size, Height = size, Margin = 2, ErrorCorrection = ZXing.QrCode.Internal.ErrorCorrectionLevel.M, CharacterSet = "UTF-8" },
        };
        return writer.Write(text);
    }

    // ================================================================ saving

    public static void SaveImage(byte[] straightBgra, int w, int h, string dest, int quality = 95)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(straightBgra, 0, bmp.GetPixels(), straightBgra.Length);
        Save(bmp, dest, quality);
    }

    public static void Save(SKBitmap bmp, string dest, int quality = 95)
    {
        var ext = Util.Ext(dest);
        var fmt = ext is ".jpg" or ".jpeg" ? SKEncodedImageFormat.Jpeg : ext == ".webp" ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png;
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(fmt, fmt == SKEncodedImageFormat.Png ? 100 : quality);
        using var fs = File.Create(dest);
        data.SaveTo(fs);
    }
}
