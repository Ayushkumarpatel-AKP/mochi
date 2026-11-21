using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mochi.Services;
using PDFtoImage;

namespace Mochi.ViewModels;

/// <summary>One preview card on the Home page: the converted file's picture (or a coloured type tile), its name and what was done.</summary>
public sealed partial class RecentTile : ObservableObject
{
    private static readonly string[] Pictures = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".heic", ".avif", ".svg" };
    private static readonly string[] Videos = { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".wmv", ".flv" };
    private static readonly string[] Sounds = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma" };
    private static readonly SemaphoreSlim Gate = new(3);   // a few previews at a time, so a long history doesn't stall the app

    public RecentItem Item { get; }
    public string Name => Item.Name;
    public string Detail => Item.Failed && Item.Detail.Length > 110 ? Item.Detail[..110] + "…" : Item.Detail;
    public bool Failed => Item.Failed;
    public string Kind { get; }
    public string Badge { get; }
    public string Glyph { get; }
    public IBrush Tint { get; }
    public bool HasExtra => Item.Outputs.Count > 1;
    public string Extra => "+" + (Item.Outputs.Count - 1);

    [ObservableProperty] private IImage? _thumb;
    [ObservableProperty] private bool _shown = true;

    public RecentTile(RecentItem item, bool loadPreview)
    {
        Item = item;
        var ext = Directory.Exists(item.Path) ? "" : Util.Ext(item.Path);
        Kind = item.Failed ? "failed" : Pictures.Contains(ext) ? "image" : Videos.Contains(ext) ? "video" : Sounds.Contains(ext) ? "audio" : ext == ".pdf" ? "pdf" : "doc";
        Badge = item.Failed ? "FAILED" : ext.Length > 1 ? ext[1..].ToUpperInvariant() : "FOLDER";
        (Glyph, var a, var b) = Kind switch
        {
            "image" => ("▨", "#5E9BFF", "#3A5BD9"),
            "video" => ("▶", "#B07CFF", "#6C3FD1"),
            "audio" => ("♪", "#FFB25E", "#E0642E"),
            "pdf" => ("❏", "#FF7A70", "#D13A3A"),
            "failed" => ("!", "#5A2A2E", "#3A1C20"),
            _ => (Directory.Exists(item.Path) ? "▣" : "☰", "#7C8794", "#4B5563"),
        };
        Tint = new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative), EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
        };
        if (loadPreview && !item.Failed) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        await Gate.WaitAsync();
        IImage? img;
        try { img = await Task.Run(() => Make(Item.Path)); }
        finally { Gate.Release(); }
        if (img is not null) Dispatcher.UIThread.Post(() => Thumb = img);
    }

    private static IImage? Make(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var ext = Util.Ext(path);
            if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".ico")
            {
                using var fs = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fs, 360, BitmapInterpolationMode.HighQuality);
            }
            if (ext == ".pdf")
            {
                using var fs = File.OpenRead(path);
                using var sk = Conversion.ToImage(fs, options: new PDFtoImage.RenderOptions(Width: 360));
                using var data = SkiaSharp.SKImage.FromBitmap(sk).Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                return new Bitmap(data.AsStream());
            }
            if (Videos.Contains(ext) && Tools.Find("ffmpeg") is { } ff)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "mochi-th-" + Guid.NewGuid().ToString("N") + ".jpg");
                try
                {
                    foreach (var at in new[] { "1", "0" })   // a frame a second in, or the first one for very short clips
                    {
                        using var p = Process.Start(new ProcessStartInfo(ff, $"-y -loglevel error -ss {at} -i \"{path}\" -frames:v 1 -vf scale=360:-2 \"{tmp}\"") { CreateNoWindow = true, UseShellExecute = false })!;
                        if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { /* already gone */ } }
                        if (File.Exists(tmp) && new FileInfo(tmp).Length > 0) break;
                    }
                    if (!File.Exists(tmp)) return null;
                    using var fs = File.OpenRead(tmp);
                    return Bitmap.DecodeToWidth(fs, 360, BitmapInterpolationMode.HighQuality);
                }
                finally { try { File.Delete(tmp); } catch { /* temp file */ } }
            }
        }
        catch { /* no preview: the coloured tile stands in */ }
        return null;
    }
}

/// <summary>A filter chip above the tiles: All, Pictures, Videos…</summary>
public sealed partial class FilterChip : ObservableObject
{
    public string Key { get; }
    private readonly string _name;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _isOn;
    public IRelayCommand ApplyCommand { get; }

    public FilterChip(string key, string name, Action<string> apply)
    {
        Key = key; _name = name; Label = name;
        ApplyCommand = new RelayCommand(() => apply(key));
    }

    public void SetCount(int n) => Label = n > 0 && Key != "all" ? $"{_name}  {n}" : _name;
}

public partial class MainViewModel
{
    public ObservableCollection<RecentTile> Tiles { get; } = new();
    public ObservableCollection<FilterChip> Filters { get; } = new();

    private readonly Dictionary<RecentItem, RecentTile> _tileCache = new(ReferenceEqualityComparer.Instance);
    private string _filter = "all";

    private void InitTiles()
    {
        foreach (var (key, fallback) in new[] { ("all", "All"), ("image", "Pictures"), ("video", "Videos"), ("audio", "Audio"), ("pdf", "PDFs"), ("doc", "Documents"), ("failed", "Failed") })
            Filters.Add(new FilterChip(key, Loc.TryGet("x.f." + key) ?? fallback, SetFilter));
        Recent.CollectionChanged += (_, _) => RebuildTiles();
        RebuildTiles();
    }

    private void SetFilter(string key)
    {
        _filter = key;
        ApplyFilter();
    }

    private void RebuildTiles()
    {
        var keep = new Dictionary<RecentItem, RecentTile>(ReferenceEqualityComparer.Instance);
        Tiles.Clear();
        var i = 0;
        foreach (var r in Recent)
        {
            if (!_tileCache.TryGetValue(r, out var t)) t = new RecentTile(r, i < 40);   // only the newest get a live preview
            keep[r] = t; Tiles.Add(t); i++;
        }
        _tileCache.Clear();
        foreach (var kv in keep) _tileCache[kv.Key] = kv.Value;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        foreach (var t in Tiles) t.Shown = _filter == "all" || t.Kind == _filter;
        foreach (var f in Filters)
        {
            f.IsOn = f.Key == _filter;
            f.SetCount(f.Key == "all" ? Tiles.Count : Tiles.Count(t => t.Kind == f.Key));
        }
    }
}

/// <summary>A glyph and a gradient per kind of file, shared by the preview tiles and the preset cards.</summary>
public static class Look
{
    public static (string Glyph, IBrush Tint) Of(string category)
    {
        var (g, a, b) = category switch
        {
            "Image" => ("▨", "#5E9BFF", "#3A5BD9"),
            "Video" => ("▶", "#B07CFF", "#6C3FD1"),
            "Audio" => ("♪", "#FFB25E", "#E0642E"),
            "Pdf" => ("❏", "#FF7A70", "#D13A3A"),
            "Document" => ("☰", "#6EA8FF", "#3F5FCB"),
            "Spreadsheet" => ("▦", "#5FD68A", "#1F9D55"),
            "Presentation" => ("▤", "#FFA45E", "#D9622B"),
            "Hwp" => ("☰", "#4FD1C5", "#2C8F87"),
            "Text" => ("≡", "#A0A8B5", "#5C6573"),
            "Ebook" => ("▥", "#C99A6B", "#8B5E3C"),
            "Archive" => ("▣", "#E8C25E", "#B8892B"),
            "Vector" => ("✎", "#FF8FB8", "#D13F7A"),
            "Cad" => ("⌗", "#8FB3C9", "#4F7A94"),
            "Font" => ("Aa", "#9FA8FF", "#5662D6"),
            "Subtitle" => ("❝", "#7ED6E8", "#2F9DB5"),
            "Data" => ("{}", "#7DD3A8", "#2E9C73"),
            "Model3D" => ("◈", "#C792EA", "#8244B8"),
            "Geo" => ("◎", "#6CD0A0", "#2C9A6C"),
            "Files" => ("⌕", "#4FD1C5", "#2C8F87"),
            "Ai" => ("✦", "#FF6FB5", "#7A5CFF"),
            _ => ("▸", "#B07CFF", "#6C3FD1"),   // a group
        };
        return (g, new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative), EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
        });
    }
}

public partial class MainViewModel
{
    public ObservableCollection<FilterChip> PresetChips { get; } = new();

    private void SetPresetCategory(string key) { PresetCategory = key; RefreshPresets(); }

    // the library shows one page of tools at a time, with a 1 2 3 … pager underneath instead of a long scroll
    private const int PresetPageSize = 20;
    private int _presetPage;
    private string _presetKey = "";
    [ObservableProperty] private int _presetTotal;
    public ObservableCollection<PageChip> PresetPages { get; } = new();
    public bool HasPager => PresetPages.Count > 3;

    private void GoToPresetPage(int page) { _presetPage = page; RefreshPresets(); }

    private void RefreshPager(int total)
    {
        var pages = Math.Max(1, (total + PresetPageSize - 1) / PresetPageSize);
        if (_presetPage >= pages) { _presetPage = pages - 1; RefreshPresets(); return; }   // e.g. the last page emptied by a filter
        PresetPages.Clear();
        if (pages > 1)
        {
            PresetPages.Add(new PageChip("‹", false, _presetPage > 0, () => GoToPresetPage(_presetPage - 1)));
            for (var i = 0; i < pages; i++) { var n = i; PresetPages.Add(new PageChip((i + 1).ToString(), i == _presetPage, true, () => GoToPresetPage(n))); }
            PresetPages.Add(new PageChip("›", false, _presetPage < pages - 1, () => GoToPresetPage(_presetPage + 1)));
        }
        OnPropertyChanged(nameof(HasPager));
    }

    /// <summary>One chip per kind of file that has presets, with its count, plus All and In donut.</summary>
    private void RefreshPresetChips()
    {
        var all = Converter.Presets.Where(Allowed).ToList();
        if (PresetChips.Count == 0)
        {
            PresetChips.Add(new FilterChip("all", Loc.TryGet("x.f.all") ?? "All", SetPresetCategory));
        }
        var cats = all.GroupBy(p => p.Category).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        foreach (var c in cats.Where(c => !PresetChips.Any(x => x.Key == c)))
            PresetChips.Add(new FilterChip(c, Loc.T("format.category." + c), SetPresetCategory));
        foreach (var chip in PresetChips.Where(x => x.Key != "all" && x.Key != "donut" && !cats.Contains(x.Key)).ToList()) PresetChips.Remove(chip);
        foreach (var chip in PresetChips)
        {
            chip.IsOn = chip.Key == PresetCategory;
            chip.SetCount(chip.Key == "all" ? 0 : chip.Key == "donut" ? all.Count(p => CurrentIds.Contains(p.Id)) : all.Count(p => p.Category == chip.Key));
        }
    }
}

/// <summary>One button of the pager: a page number, or the previous / next arrow.</summary>
public sealed partial class PageChip : ObservableObject
{
    private readonly Action _go;
    public PageChip(string label, bool on, bool enabled, Action go) { Label = label; IsOn = on; IsEnabled = enabled; _go = go; }
    public string Label { get; }
    public bool IsOn { get; }
    public bool IsEnabled { get; }
    [RelayCommand] private void Go() => _go();
}
