using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mochi.Services;
using Microsoft.VisualBasic.FileIO;
using SkiaSharp;

namespace Mochi.Views;

/// <summary>One picture found in the folder, with a 64-bit "fingerprint" of how it looks.</summary>
public sealed record PhotoInfo(string Path, long Size, int W, int H, ulong Hash, DateTime Taken)
{
    public long Pixels => (long)W * H;
}

/// <summary>Finds pictures that are the same or nearly the same (resized, re-saved, slightly edited) and lets you recycle the extras.</summary>
public sealed class DupesWindow : Window
{
    private static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff" };
    private static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    private readonly string _folder;
    private List<PhotoInfo> _photos = new();
    private readonly StackPanel _list = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Width = 160, Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly Button _recycle = new() { Classes = { "pill" }, IsEnabled = false };
    private readonly ComboBox _level = new() { ItemsSource = new[] { "Exact copies", "Same picture, resized or re-saved", "Looks similar" }, SelectedIndex = 1, Width = 270 };
    private readonly CheckBox _sub = new() { Content = "Include subfolders", IsChecked = true };
    private readonly List<(PhotoInfo Photo, CheckBox Box, bool Best)> _boxes = new();
    private bool _busy;

    public static Task<string?> RunAsync(string folder)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new DupesWindow(folder);
        w.Closed += (_, _) => tcs.TrySetResult(null);   // nothing to open afterwards, so no history entry
        w.Show();
        return tcs.Task;
    }

    public DupesWindow(string folder)
    {
        _folder = folder;
        Title = L("x.du.title", "Find duplicate photos") + " — " + Path.GetFileName(folder.TrimEnd('\\', '/'));
        Width = 1000; Height = 680; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _level.ItemsSource = new[] { L("x.du.exact", "Exact copies"), L("x.du.same", "Same picture, resized or re-saved"), L("x.du.similar", "Looks similar") };
        _level.SelectedIndex = 1;
        _sub.Content = L("x.du.sub", "Include subfolders");
        _level.SelectionChanged += (_, _) => Regroup();
        _sub.IsCheckedChanged += async (_, _) => await ScanAsync();

        var auto = new Button { Content = L("x.du.auto", "Select extras (keep the best)") };
        auto.Click += (_, _) => AutoSelect();
        var none = new Button { Content = L("x.du.none", "Select none") };
        none.Click += (_, _) => { foreach (var (_, b, _) in _boxes) b.IsChecked = false; UpdateButton(); };
        _recycle.Click += (_, _) => RecycleSelected();
        var close = new Button { Content = L("common.close", "Close") };
        close.Click += (_, _) => Close();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(16, 12), VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(new TextBlock { Text = L("x.du.how", "How alike?"), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_level); top.Children.Add(_sub); top.Children.Add(_bar); top.Children.Add(_status);

        var bottom = new DockPanel { Margin = new Thickness(16, 10) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { close, _recycle } };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { auto, none } };
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right); bottom.Children.Add(left);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(16, 4), Child = _list } });
        Content = root;

        Opened += async (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } sc)
            {
                Width = Math.Min(Width, sc.WorkingArea.Width / sc.Scaling - 40); Height = Math.Min(Height, sc.WorkingArea.Height / sc.Scaling - 40);
                Position = new PixelPoint(sc.WorkingArea.X + (int)((sc.WorkingArea.Width - Width * sc.Scaling) / 2), sc.WorkingArea.Y + (int)((sc.WorkingArea.Height - Height * sc.Scaling) / 2));
            }
            await ScanAsync();
        };
        UpdateButton();
    }

    // ---------------------------------------------------------------- scanning

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true; _bar.IsVisible = true; _bar.Value = 0; _list.Children.Clear(); _boxes.Clear(); UpdateButton();
        _status.Text = L("x.du.looking", "Looking at your pictures…");
        var include = _sub.IsChecked == true; var progress = new Progress<double>(v => _bar.Value = v);
        _photos = await Task.Run(() => Scan(include, progress));
        _busy = false; _bar.IsVisible = false;
        Regroup();
    }

    private List<PhotoInfo> Scan(bool sub, IProgress<double> progress)
    {
        var files = new List<string>();
        try
        {
            files = Directory.EnumerateFiles(_folder, "*.*", new EnumerationOptions { RecurseSubdirectories = sub, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                .Where(f => Exts.Contains(Util.Ext(f))).ToList();
        }
        catch { /* unreadable folder: show nothing */ }
        var result = new PhotoInfo?[files.Count]; var done = 0;
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
        {
            try { result[i] = Fingerprint(files[i]); } catch { /* damaged or unsupported picture: skipped */ }
            progress.Report(Interlocked.Increment(ref done) / (double)Math.Max(1, files.Count));
        });
        return result.OfType<PhotoInfo>().ToList();
    }

    /// <summary>dHash: shrink to 9×8 grey and note whether each pixel is brighter than its right neighbour. Survives resizing and re-saving.</summary>
    private static PhotoInfo Fingerprint(string path)
    {
        var fi = new FileInfo(path);
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException();
        int ow = codec.Info.Width, oh = codec.Info.Height;
        var scaled = codec.GetScaledDimensions(Math.Min(1f, 96f / Math.Max(ow, oh)));   // JPEGs decode small and fast
        using var bmp = SKBitmap.Decode(codec, new SKImageInfo(Math.Max(1, scaled.Width), Math.Max(1, scaled.Height), SKColorType.Bgra8888, SKAlphaType.Premul)) ?? throw new InvalidDataException();
        using var tiny = new SKBitmap(9, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(tiny))
        {
            c.Clear(SKColors.White);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
            c.DrawBitmap(bmp, new SKRect(0, 0, 9, 8), paint);
        }
        var b = tiny.Bytes; ulong hash = 0; var bit = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                float Y(int xx) { var i = (y * 9 + xx) * 4; return 0.2126f * b[i + 2] + 0.7152f * b[i + 1] + 0.0722f * b[i]; }
                if (Y(x) > Y(x + 1)) hash |= 1UL << bit;
                bit++;
            }
        return new PhotoInfo(path, fi.Length, ow, oh, hash, fi.LastWriteTime);
    }

    // ---------------------------------------------------------------- grouping and display

    private int Threshold => _level.SelectedIndex switch { 0 => 0, 2 => 12, _ => 5 };

    private void Regroup()
    {
        _list.Children.Clear(); _boxes.Clear();
        var n = _photos.Count; var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var t = Threshold;
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var same = t == 0
                    ? _photos[i].Hash == _photos[j].Hash && _photos[i].Size == _photos[j].Size
                    : BitOperations.PopCount(_photos[i].Hash ^ _photos[j].Hash) <= t;
                if (same) parent[Find(i)] = Find(j);
            }
        var groups = Enumerable.Range(0, n).GroupBy(Find).Where(g => g.Count() > 1)
            .Select(g => g.Select(i => _photos[i]).OrderByDescending(p => p.Pixels).ThenByDescending(p => p.Size).ThenBy(p => p.Taken).ToList())
            .OrderByDescending(g => g.Count).ToList();

        if (groups.Count == 0)
        {
            _status.Text = n == 0 ? L("x.du.none_found", "No pictures found in this folder.") : L("x.du.no_dupes", "No duplicates found. Everything here is different.");
            UpdateButton(); return;
        }
        var extra = groups.Sum(g => g.Count - 1);
        _status.Text = string.Format(L("x.du.found", "{0} groups, {1} extra copies"), groups.Count, extra);
        var gi = 0;
        foreach (var g in groups)
        {
            var wrap = new WrapPanel();
            for (var k = 0; k < g.Count; k++) wrap.Children.Add(Card(g[k], k == 0));
            _list.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10), Background = new SolidColorBrush(Color.Parse("#1C1C1E")),
                Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = string.Format(L("x.du.group", "Group {0} · {1} pictures"), ++gi, g.Count), FontWeight = FontWeight.SemiBold }, wrap } },
            });
        }
        AutoSelect();
    }

    private Control Card(PhotoInfo p, bool best)
    {
        var img = new Image { Stretch = Stretch.UniformToFill, Height = 120, Width = 168 };
        _ = LoadThumb(p.Path, img);
        var box = new CheckBox { Content = best ? L("x.du.keep", "Best: keep") : L("x.du.remove", "Move to Recycle Bin"), FontSize = 12 };
        box.IsCheckedChanged += (_, _) => UpdateButton();
        _boxes.Add((p, box, best));
        var card = new Border
        {
            Width = 168, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#2C2C2E")),
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Height = 120, ClipToBounds = true, Child = img },
                    new StackPanel
                    {
                        Margin = new Thickness(8, 6), Spacing = 1,
                        Children =
                        {
                            new TextBlock { Text = Path.GetFileName(p.Path), FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = $"{p.W}×{p.H} · {Size(p.Size)}", FontSize = 11, Classes = { "secondary" } },
                            box,
                        },
                    },
                },
            },
        };
        ToolTip.SetTip(card, p.Path);
        return card;
    }

    private static string Size(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.#} MB" : $"{Math.Max(1, b / 1024)} KB";

    private static async Task LoadThumb(string path, Image target)
    {
        var bmp = await Task.Run(() =>
        {
            try { using var fs = File.OpenRead(path); return Bitmap.DecodeToWidth(fs, 220, BitmapInterpolationMode.HighQuality); }
            catch { return null; }
        });
        if (bmp is not null) Dispatcher.UIThread.Post(() => target.Source = bmp);
    }

    /// <summary>In each group the first picture (the biggest, then the largest file) stays; the others get ticked.</summary>
    private void AutoSelect()
    {
        foreach (var (_, box, best) in _boxes) box.IsChecked = !best;
        UpdateButton();
    }

    private void UpdateButton()
    {
        var picked = _boxes.Where(b => b.Box.IsChecked == true).ToList();
        _recycle.IsEnabled = picked.Count > 0;
        _recycle.Content = picked.Count == 0 ? L("x.du.recycle0", "Move to Recycle Bin")
            : string.Format(L("x.du.recycle", "Move {0} to Recycle Bin ({1})"), picked.Count, Size(picked.Sum(b => b.Photo.Size)));
    }

    private void RecycleSelected()
    {
        var picked = _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Photo).ToList();
        var failed = 0;
        foreach (var p in picked)
        {
            try { FileSystem.DeleteFile(p.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); _photos.Remove(p); }
            catch { failed++; }
        }
        Regroup();
        _status.Text = failed == 0 ? string.Format(L("x.du.moved", "Moved {0} pictures to the Recycle Bin."), picked.Count) : string.Format(L("x.du.moved_some", "{0} could not be moved."), failed);
    }
}
