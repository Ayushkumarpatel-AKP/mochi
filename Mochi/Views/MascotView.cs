using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>How a mascot moves while it sits on a page.</summary>
public enum MascotMood
{
    /// <summary>A soft hop with a squashed landing, every couple of seconds.</summary>
    Hop,
    /// <summary>A slow float, for a calm page or a sleeping Mochi.</summary>
    Float,
    /// <summary>No idle motion (it still pops in and squishes when touched).</summary>
    Still,
}

/// <summary>
/// One of Mochi's stickers (Assets/Mascot). It pops in when it appears or changes, moves a little all the time (squash and stretch,
/// anchored at its feet) and squishes when clicked. The motion is driven once per display frame, so it follows the monitor's refresh rate.
/// Set <see cref="Mascot"/> from XAML or code.
/// </summary>
public sealed class MascotView : Image
{
    public static readonly StyledProperty<string?> MascotProperty = AvaloniaProperty.Register<MascotView, string?>(nameof(Mascot));
    public static readonly StyledProperty<MascotMood> MoodProperty = AvaloniaProperty.Register<MascotView, MascotMood>(nameof(Mood), MascotMood.Hop);

    private static readonly Dictionary<string, Bitmap?> Cache = new();

    // t (0..1), lift in px (up is negative), scale x, scale y, rotation in degrees, opacity
    private readonly record struct Key(double T, double Y, double Sx, double Sy, double Rot, double Op);

    private static readonly Key[] PopKeys =
    {
        new(0, 0, .55, .55, 0, 0), new(.55, 0, 1.08, .94, 0, 1), new(.8, 0, .97, 1.03, 0, 1), new(1, 0, 1, 1, 0, 1),
    };
    private static readonly Key[] SquishKeys =
    {
        new(0, 0, 1, 1, 0, 1), new(.25, 0, 1.15, .82, 0, 1), new(.55, -6, .92, 1.1, 0, 1), new(.8, 0, 1.05, .95, 0, 1), new(1, 0, 1, 1, 0, 1),
    };
    private static readonly Key[] HopKeys =
    {
        new(0, 0, 1, 1, 0, 1), new(.12, 0, 1.06, .92, 0, 1), new(.34, -7, .98, 1.03, -1.5, 1), new(.5, -8, .98, 1.03, 1.5, 1),
        new(.7, 0, 1.08, .9, 0, 1), new(.78, 0, 1, 1, 0, 1), new(1, 0, 1, 1, 0, 1),
    };
    private static readonly Key[] FloatKeys = { new(0, 0, 1, 1, 0, 1), new(.5, -4, .995, 1.01, 0, 1), new(1, 0, 1, 1, 0, 1) };

    private enum Phase { Pop, Squish, Idle }

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly RotateTransform _rotate = new();
    private readonly TranslateTransform _move = new();
    private Phase _phase = Phase.Pop;
    private TimeSpan _start;
    private bool _fresh = true, _scheduled;

    public string? Mascot { get => GetValue(MascotProperty); set => SetValue(MascotProperty, value); }
    public MascotMood Mood { get => GetValue(MoodProperty); set => SetValue(MoodProperty, value); }

    static MascotView()
    {
        MascotProperty.Changed.AddClassHandler<MascotView>((v, _) => v.OnMascotChanged());
        MoodProperty.Changed.AddClassHandler<MascotView>((v, _) => v.Restart(Phase.Idle));
    }

    public MascotView()
    {
        Stretch = Stretch.Uniform;
        RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative);
        RenderTransform = new TransformGroup { Children = { _scale, _rotate, _move } };
        Cursor = new Cursor(StandardCursorType.Hand);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        Focusable = false;
    }

    public MascotView(string mascot, double height, MascotMood mood = MascotMood.Hop) : this()
    {
        Height = height; Mood = mood; Mascot = mascot;
    }

    private static Bitmap? Load(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!Cache.TryGetValue(name, out var bmp)) Cache[name] = bmp = Brand.Mascot(name);
        return bmp;
    }

    private void OnMascotChanged()
    {
        Source = Load(Mascot);
        Restart(Phase.Pop);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Source is null) Source = Load(Mascot);
        Restart(Phase.Pop);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // a page that was hidden shows again: pop back in
        if (change.Property.Name == "IsEffectivelyVisible" && change.NewValue is true) Restart(Phase.Pop);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Restart(Phase.Squish);
    }

    private void Restart(Phase p)
    {
        _phase = p; _fresh = true;
        if (p == Phase.Pop) Apply(PopKeys[0]);
        Schedule();
    }

    private void Schedule()
    {
        if (_scheduled || Source is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        _scheduled = true;
        top.RequestAnimationFrame(Tick);
    }

    private void Tick(TimeSpan now)
    {
        _scheduled = false;
        if (TopLevel.GetTopLevel(this) is null || !IsEffectivelyVisible) return;
        if (_fresh) { _start = now; _fresh = false; }
        var el = (now - _start).TotalSeconds;

        switch (_phase)
        {
            case Phase.Pop:
                if (el >= 0.52) { Apply(PopKeys[^1]); _phase = Phase.Idle; _start = now; Schedule(); return; }
                Apply(Sample(PopKeys, el / 0.52));
                break;
            case Phase.Squish:
                if (el >= 0.48) { Apply(SquishKeys[^1]); _phase = Phase.Idle; _start = now; Schedule(); return; }
                Apply(Sample(SquishKeys, el / 0.48));
                break;
            default:
                if (Mood == MascotMood.Still) { Apply(HopKeys[0]); return; }
                var period = Mood == MascotMood.Float ? 4.2 : 2.6;
                Apply(Sample(Mood == MascotMood.Float ? FloatKeys : HopKeys, el % period / period));
                break;
        }
        Schedule();
    }

    private static Key Sample(Key[] k, double t)
    {
        t = Math.Clamp(t, 0, 1);
        var i = 1;
        while (i < k.Length - 1 && k[i].T < t) i++;
        var a = k[i - 1]; var b = k[i];
        var u = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 1;
        u = u * u * (3 - 2 * u);
        double L(double x, double y) => x + (y - x) * u;
        return new Key(t, L(a.Y, b.Y), L(a.Sx, b.Sx), L(a.Sy, b.Sy), L(a.Rot, b.Rot), L(a.Op, b.Op));
    }

    private void Apply(Key k)
    {
        _move.Y = k.Y; _scale.ScaleX = k.Sx; _scale.ScaleY = k.Sy; _rotate.Angle = k.Rot; Opacity = k.Op;
    }
}
