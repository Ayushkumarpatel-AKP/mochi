using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mochi.Services;

namespace Mochi.ViewModels;

/// <summary>The friendly face of an engine: a plain sentence, a tidy name, an icon, and a details switch.</summary>
public sealed partial class EngineRow
{
    private static readonly Dictionary<string, string> Plain = new()
    {
        ["skia"] = "Opens and saves everyday pictures: PNG, JPG, WebP, BMP and GIF.",
        ["magick"] = "A powerful helper for unusual picture types such as HEIC, AVIF, PSD and TIFF.",
        ["icns"] = "Makes Mac app icons (.icns).",
        ["wic"] = "Reads JPEG XR pictures using Windows.",
        ["imagetools"] = "Rotates, flips, resizes, shrinks and cleans pictures.",
        ["crop"] = "Crops pictures to a shape such as square or 16:9.",
        ["uitools"] = "Opens the drawing, straighten and PDF page editors.",
        ["vector"] = "Turns SVG drawings into pictures, and back.",
        ["trace"] = "Turns a picture into a clean SVG drawing.",
        ["ghostscript"] = "Reads EPS and PostScript drawings.",
        ["ffmpeg"] = "The video and sound expert: MP4, MP3, GIF and many more.",
        ["ffmpeg-plus"] = "One-click video recipes: shrink for WhatsApp, cut a clip, mute…",
        ["motion"] = "Pulls the video out of Motion Photos.",
        ["subtitle"] = "Converts subtitle files (SRT, VTT…) into each other.",
        ["pdf"] = "Makes, merges, splits and shrinks PDF files.",
        ["pdf-extra"] = "Locks, unlocks and converts PDFs.",
        ["nup"] = "Puts several pages on one sheet.",
        ["ofd"] = "Reads Chinese OFD documents.",
        ["document"] = "Word, text and Markdown documents.",
        ["libreoffice"] = "Opens old and unusual office files (needs LibreOffice).",
        ["pandoc"] = "Converts text formats such as Markdown, HTML and EPUB (needs Pandoc).",
        ["ooxml"] = "Reads and writes modern Office files: DOCX, XLSX and PPTX.",
        ["email"] = "Turns e-mail files (EML, MSG) into PDF or text.",
        ["rhwp"] = "Opens Korean HWP documents.",
        ["spreadsheet"] = "Excel, CSV and similar tables.",
        ["ebook"] = "Converts e-books such as EPUB.",
        ["kepub"] = "Makes e-books for Kobo readers.",
        ["calibre"] = "Opens Kindle and rare e-book types (needs Calibre).",
        ["ai"] = "Enlarges pictures, blurs faces, fixes dull photos, reads text and QR codes.",
        ["tesseract"] = "Reads the text inside pictures (OCR).",
        ["whisper"] = "Listens to speech and writes captions.",
        ["translate"] = "Translates text on your own computer (uses Ollama).",
        ["data"] = "JSON, YAML, TOML, XML and CSV data files.",
        ["archive"] = "ZIP, 7z, TAR and other packed files.",
        ["font"] = "Font files: TTF, OTF and WOFF.",
        ["cad"] = "CAD drawings (DXF).",
        ["model3d"] = "3D models such as OBJ, STL and PLY.",
        ["assimp"] = "More 3D types: GLB, glTF, 3MF and FBX.",
        ["geo"] = "Map data: GeoJSON, KML and Shapefile.",
    };

    public string Simple => Plain.TryGetValue(Engine.Id, out var s) ? s : Description.Split('\n')[0];
    public string ShortName => Name.Replace(" (built-in)", "");
    public string Glyph => Look.Of(Engine.Category).Glyph;
    public IBrush Tint => Look.Of(Engine.Category).Tint;
    public string ReadyText => Engine.Tool is null ? Loc.TryGet("x.built_in") ?? "Built in" : Loc.TryGet("x.ready") ?? "Ready";
    public string NeedsText => Loc.TryGet("x.needs_install") ?? "Needs install";
    public string DetailsLabel => IsOpen ? (Loc.TryGet("x.hide") ?? "Hide") + " ▴" : (Loc.TryGet("x.details") ?? "Details") + " ▾";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _shown = true;
    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(DetailsLabel));
    [RelayCommand] private void Toggle() => IsOpen = !IsOpen;
}

/// <summary>A titled section of engines on the Engines page.</summary>
public sealed partial class EngineGroup : ObservableObject
{
    public string Key { get; }
    public string Title { get; }
    public string Tagline { get; }
    public string Glyph { get; }
    public IBrush Tint { get; }
    public ObservableCollection<EngineRow> Rows { get; } = new();
    [ObservableProperty] private bool _shown = true;

    public EngineGroup(string key, string title, string tagline, string lookCategory)
    {
        Key = key; Title = title; Tagline = tagline;
        (Glyph, Tint) = Look.Of(lookCategory);
    }

    public string Count => Rows.Count(r => r.Ready) + " / " + Rows.Count + " " + (Loc.TryGet("x.ready_lc") ?? "ready");
}

public partial class MainViewModel
{
    public ObservableCollection<EngineGroup> EngineGroups { get; } = new();
    public ObservableCollection<FilterChip> EngineChips { get; } = new();
    private string _engineFilter = "all";

    [ObservableProperty] private string _engineSummary = "";
    [ObservableProperty] private string _engineNeeds = "";
    [ObservableProperty] private bool _engineHasNeeds;

    // which section each engine lives in, in the order the sections appear
    private static readonly (string Key, string Title, string Tagline, string Look, string[] Ids)[] EngineSections =
    {
        ("pictures", "Pictures & drawings", "Open, resize and convert photos and artwork.", "Image", new[] { "skia", "imagetools", "crop", "uitools", "magick", "icns", "wic", "vector", "trace", "ghostscript" }),
        ("media", "Video & sound", "Make videos smaller, pull out audio, add captions.", "Video", new[] { "ffmpeg", "ffmpeg-plus", "motion", "subtitle" }),
        ("pdf", "PDF", "Everything for PDF files.", "Pdf", new[] { "pdf", "pdf-extra", "nup", "ofd" }),
        ("office", "Documents & tables", "Word, Excel, text and e-mail files.", "Document", new[] { "document", "ooxml", "spreadsheet", "libreoffice", "pandoc", "email", "rhwp" }),
        ("ebooks", "E-books", "Books for Kindle, Kobo and phones.", "Ebook", new[] { "ebook", "kepub", "calibre" }),
        ("smart", "Smart helpers", "Read text in pictures, write captions, translate.", "Model3D", new[] { "ai", "tesseract", "whisper", "translate" }),
        ("other", "Data, 3D & other files", "Code data, packed files, fonts, 3D models and maps.", "Archive", new[] { "data", "archive", "font", "cad", "model3d", "assimp", "geo" }),
    };

    private void BuildEngineGroups()
    {
        EngineGroups.Clear();
        var left = EngineRows.ToList();
        foreach (var (key, title, tagline, look, ids) in EngineSections)
        {
            var g = new EngineGroup(key, Loc.TryGet("x.eg." + key) ?? title, Loc.TryGet("x.egt." + key) ?? tagline, look);
            foreach (var id in ids)
                if (left.FirstOrDefault(r => r.Engine.Id == id) is { } row) { g.Rows.Add(row); left.Remove(row); }
            if (g.Rows.Count > 0) EngineGroups.Add(g);
        }
        if (left.Count > 0)   // an engine not listed above still shows up
        {
            var g = EngineGroups.FirstOrDefault(x => x.Key == "other") ?? new EngineGroup("other", "Other", "", "Archive");
            foreach (var r in left) g.Rows.Add(r);
            if (!EngineGroups.Contains(g)) EngineGroups.Add(g);
        }

        if (EngineChips.Count == 0)
        {
            EngineChips.Add(new FilterChip("all", Loc.TryGet("x.f.all") ?? "All", SetEngineFilter));
            EngineChips.Add(new FilterChip("needs", Loc.TryGet("x.needs_install") ?? "Needs install", SetEngineFilter));
            foreach (var (key, title, _, _, _) in EngineSections) EngineChips.Add(new FilterChip(key, Loc.TryGet("x.eg." + key) ?? title, SetEngineFilter));
        }
        ApplyEngineFilter();
    }

    private void SetEngineFilter(string key) { _engineFilter = key; ApplyEngineFilter(); }

    private void ApplyEngineFilter()
    {
        foreach (var g in EngineGroups)
        {
            foreach (var r in g.Rows) r.Shown = _engineFilter == "needs" ? !r.Ready : true;
            g.Shown = _engineFilter is "all" or "needs" ? g.Rows.Any(r => r.Shown) : g.Key == _engineFilter;
        }
        var total = EngineRows.Count; var ready = EngineRows.Count(r => r.Ready); var needs = total - ready;
        EngineSummary = Loc.T("x.engines_summary", ready, total);
        EngineHasNeeds = needs > 0;
        EngineNeeds = needs > 0 ? Loc.T("x.engines_needs", needs) : "";
        foreach (var c in EngineChips)
        {
            c.IsOn = c.Key == _engineFilter;
            c.SetCount(c.Key == "all" ? 0 : c.Key == "needs" ? needs : EngineGroups.FirstOrDefault(g => g.Key == c.Key)?.Rows.Count ?? 0);
        }
    }
}
