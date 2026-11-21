using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Mochi.Services;
using SkiaSharp;

namespace Mochi.Views;

/// <summary>The "What's new" slides of Filee 2.3.0: a still from each animated picture, with Filee's own titles and text.</summary>
public sealed class WhatsNewWindow : Window
{
    private sealed record Slide(string Media, int Poster, string Title, string Text);

    private readonly List<Slide> _slides = new();
    private readonly Image _image = new() { MaxHeight = 380, Stretch = Stretch.Uniform };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _dots = new() { HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Gray };
    private int _i;
    private Action? _relabel;

    public const string Version = "2.3.0";

    public WhatsNewWindow()
    {
        Title = Loc.T("whats_new.title", Version);
        Width = 720; Height = 700; CanResize = false;

        using (var s = AssetLoader.Open(new Uri($"avares://Mochi/Assets/WhatsNew/{Version}/slides.json")))
        using (var doc = JsonDocument.Parse(s))
            foreach (var sl in doc.RootElement.GetProperty("slides").EnumerateArray())
                _slides.Add(new Slide(sl.GetProperty("media").GetString()!, sl.TryGetProperty("poster", out var p) ? p.GetInt32() : 0,
                    Loc.T(sl.GetProperty("title").GetString()!), Loc.T(sl.GetProperty("text").GetString()!)));

        var prev = new Button { Content = "‹ " + Loc.T("whats_new.previous") };
        prev.Click += (_, _) => Go(-1);
        var next = new Button { Content = Loc.T("whats_new.next") + " ›", Classes = { "pill" } };
        next.Click += (_, _) => { if (_i == _slides.Count - 1) Close(); else Go(1); };
        void Label() => next.Content = _i == _slides.Count - 1 ? Loc.T("whats_new.done") : Loc.T("whats_new.next") + " ›";
        Closed += (_, _) => { };
        _relabel = Label;

        var nav = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        nav.Children.Add(prev);
        Grid.SetColumn(_dots, 1); nav.Children.Add(_dots);
        Grid.SetColumn(next, 2); nav.Children.Add(next);

        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 12,
            Children = { new Border { CornerRadius = new CornerRadius(16), ClipToBounds = true, Child = _image }, _title, _text, nav },
        };
        Show(0);
    }

    private void Go(int d) => Show(Math.Clamp(_i + d, 0, _slides.Count - 1));

    private void Show(int i)
    {
        _i = i;
        var s = _slides[i];
        _title.Text = s.Title; _text.Text = s.Text;
        _dots.Text = string.Concat(Enumerable.Range(0, _slides.Count).Select(k => k == i ? "● " : "○ "));
        _relabel?.Invoke();
        _image.Source = Still($"avares://Mochi/Assets/WhatsNew/{Version}/{s.Media}", s.Poster);
    }

    /// <summary>One frame of an animated WEBP, decoded in order because each frame builds on the one before.</summary>
    private static Bitmap? Still(string uri, int frame)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri(uri));
            using var ms = new MemoryStream(); s.CopyTo(ms);
            using var codec = SKCodec.Create(new SKMemoryStream(ms.ToArray()));
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap(info);
            var last = Math.Min(frame, Math.Max(0, codec.FrameCount - 1));
            for (var f = 0; f <= last; f++)
                codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(f) { PriorFrame = f - 1 });
            using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
            return new Bitmap(new MemoryStream(data.ToArray()));
        }
        catch { return null; }
    }
}
