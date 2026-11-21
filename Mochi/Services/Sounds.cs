using System.Runtime.InteropServices;

namespace Mochi.Services;

/// <summary>
/// The sounds Mochi makes: a soft three-note glass chime when a job is done, a low double note when one fails.
/// They are synthesised here (bell partials with a gentle decay and a little echo), so there are no audio files to ship,
/// and played straight from memory through Windows' own mixer.
/// </summary>
public static class Sounds
{
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(IntPtr data, IntPtr module, uint flags);

    private const uint Memory = 0x0004, Async = 0x0001, NoDefault = 0x0002;
    private const int Rate = 44100;

    // a sound being played from memory must stay where it is until it ends, so each one is pinned for good (a handful of small buffers)
    private static readonly Dictionary<string, GCHandle> Cache = new();
    private static readonly object Gate = new();

    /// <summary>The "done" chime. <paramref name="force"/> plays it even when sounds are switched off (the preview button).</summary>
    public static void Done(bool force = false) => Play("done", force, Chime);

    /// <summary>A short, low, gentle "that didn't work".</summary>
    public static void Fail() => Play("fail", false, Low);

    private static void Play(string kind, bool force, Func<double, double[]> make)
    {
        var s = App.Settings;
        if (!force && !s.SoundEffects) return;
        try
        {
            var vol = Math.Clamp(s.SoundVolume, 0.05, 1.0);
            var key = kind + Math.Round(vol * 10);
            GCHandle h;
            lock (Gate)
            {
                if (!Cache.TryGetValue(key, out h))
                    Cache[key] = h = GCHandle.Alloc(Wav(make(vol)), GCHandleType.Pinned);
            }
            PlaySound(h.AddrOfPinnedObject(), IntPtr.Zero, Memory | Async | NoDefault);
        }
        catch { /* a missing sound device must never get in the way of the work */ }
    }

    // ---------------------------------------------------------------- the sounds

    /// <summary>E6 → B6 → E7 struck like small glass bells, each ringing out with a soft echo behind.</summary>
    private static double[] Chime(double vol)
    {
        var s = new double[(int)(Rate * 1.5)];
        Bell(s, 1318.51, 0.00, 0.55);
        Bell(s, 1975.53, 0.11, 0.50);
        Bell(s, 2637.02, 0.23, 0.38);
        Echo(s, 0.17, 0.26);
        Echo(s, 0.31, 0.12);
        return Finish(s, 0.5 * vol);
    }

    /// <summary>Two soft low notes, falling.</summary>
    private static double[] Low(double vol)
    {
        var s = new double[(int)(Rate * 0.7)];
        Tone(s, 392.0, 0.00, 0.5, 0.16);
        Tone(s, 293.66, 0.13, 0.5, 0.22);
        return Finish(s, 0.4 * vol);
    }

    /// <summary>A bell: a fundamental plus the slightly out-of-tune overtones that make metal and glass sound like metal and glass.</summary>
    private static void Bell(double[] s, double hz, double start, double amp)
    {
        double[] ratio = { 1, 2.756, 5.404 }, level = { 1, 0.30, 0.11 }, ring = { 0.45, 0.24, 0.12 };
        var from = (int)(start * Rate);
        for (var i = from; i < s.Length; i++)
        {
            var t = (i - from) / (double)Rate;
            var attack = 1 - Math.Exp(-t / 0.0035);   // a soft strike, not a click
            double v = 0;
            for (var p = 0; p < ratio.Length; p++) v += level[p] * Math.Sin(2 * Math.PI * hz * ratio[p] * t) * Math.Exp(-t / ring[p]);
            s[i] += amp * attack * v;
        }
    }

    private static void Tone(double[] s, double hz, double start, double amp, double ring)
    {
        var from = (int)(start * Rate);
        for (var i = from; i < s.Length; i++)
        {
            var t = (i - from) / (double)Rate;
            var attack = 1 - Math.Exp(-t / 0.006);
            s[i] += amp * attack * Math.Exp(-t / ring) * (Math.Sin(2 * Math.PI * hz * t) + 0.25 * Math.Sin(2 * Math.PI * hz * 2 * t));
        }
    }

    private static void Echo(double[] s, double delay, double gain)
    {
        var d = (int)(delay * Rate);
        for (var i = s.Length - 1; i >= d; i--) s[i] += gain * s[i - d];
    }

    /// <summary>Evens out the level, rounds off the very end, and applies the volume.</summary>
    private static double[] Finish(double[] s, double peak)
    {
        var max = s.Max(Math.Abs);
        var k = max > 0 ? peak / max : 1;
        var fade = (int)(Rate * 0.08);
        for (var i = 0; i < s.Length; i++)
        {
            var edge = Math.Min(1.0, (s.Length - i) / (double)fade);
            s[i] = Math.Tanh(s[i] * k * 1.1) * edge;   // tanh: a soft ceiling instead of harsh clipping
        }
        return s;
    }

    private static byte[] Wav(double[] samples)
    {
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp(samples[i] * 32767, -32768, 32767);
            data[i * 2] = (byte)(v & 0xFF); data[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        return ms.ToArray();
    }
}
