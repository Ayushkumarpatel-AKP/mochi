using Mochi.Services.Engines;

namespace Mochi.Services;

/// <summary>A conversion the user can pick: a target extension plus options. Mode picks special behaviour (merge, split, ocr…).</summary>
public sealed record Preset(string Id, string Name, string Category, string Extension,
    int Quality = 90, double Scale = 1, bool Gray = false, string Mode = "")
{
    /// <summary>These presets take all picked files at once and make one result (merge PDFs, join videos, long image…).</summary>
    public bool IsMerge => Mode is "merge" or "merge-av" or "gifanim" or "longimg" || Mode.StartsWith("ai:");

    /// <summary>The name in the current language.</summary>
    public string DisplayName => PresetNames.Of(this);
}

public interface IEngine
{
    string Id { get; }
    string Name { get; }
    string Category { get; }
    string Description { get; }
    string Formats { get; }
    string? Tool { get; }
    string? WingetId { get; }
    bool Ready { get; }
    bool CanHandle(string inExt, Preset p);
    IReadOnlyList<string> Convert(string input, Preset p, string outDir);
    IReadOnlyList<string> ConvertMany(IReadOnlyList<string> inputs, Preset p, string outDir);
}

public abstract class EngineBase : IEngine
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Category { get; }
    public abstract string Description { get; }
    public virtual string Formats => "";
    public virtual string? Tool => null;
    public virtual string? WingetId => null;
    public virtual bool Ready => Tool is null || Tools.Find(Tool) is not null;
    public abstract bool CanHandle(string inExt, Preset p);
    public abstract IReadOnlyList<string> Convert(string input, Preset p, string outDir);
    public virtual IReadOnlyList<string> ConvertMany(IReadOnlyList<string> inputs, Preset p, string outDir) =>
        throw new NotSupportedException("This engine cannot combine files.");

    protected static bool Is(string e, params string[] set) => set.Contains(e);
    protected string Exe => Tools.Find(Tool!) ?? throw new FileNotFoundException(Name + " is not installed");
}

public static class Converter
{
    private static readonly IEngine[] All =
    {
        new AiAssistEngine(), new PdfEngine(), new SubtitleEngine(), new SheetEngine(), new DataEngine(), new GeoEngine(),
        new FontEngine(), new Model3dEngine(), new CadEngine(), new ArchiveEngine(), new EmailEngine(),
        new EbookEngine(), new DocumentEngine(), new ImageEngine(), new VectorEngine(), new GhostscriptEngine(),
        new MagickEngine(), new FfmpegEngine(), new PandocEngine(), new LibreOfficeEngine(), new TesseractEngine(),
        new HwpEngine(), new UiToolsEngine(), new AiEngine(), new CropEngine(), new NupEngine(), new WhisperEngine(), new TranslateEngine(),
        new CalibreEngine(), new TraceEngine(), new IcnsEngine(), new WicEngine(), new OfdEngine(),
        new FfmpegPlusEngine(), new ImageToolsEngine(), new MotionPhotoEngine(), new PdfExtraEngine(), new OoxmlEngine(),
        new KepubEngine(), new AssimpEngine(),
    };

    private static readonly string[] Mids = { ".docx", ".html", ".pdf", ".png", ".txt", ".csv", ".odt", ".svg" };

    public static IReadOnlyList<Preset> Presets => PresetCatalog.All;
    public static Preset? Find(string id) => PresetCatalog.All.FirstOrDefault(p => p.Id == id);

    /// <summary>Engines in the user's chosen priority order; ones not yet ranked keep their built-in order.</summary>
    public static IReadOnlyList<IEngine> Engines
    {
        get
        {
            var order = App.Settings.EngineOrder;
            return All.Select((e, i) => (e, i))
                .OrderBy(x => { var k = order.IndexOf(x.e.Id); return k < 0 ? 1000 + x.i : k; })
                .Select(x => x.e).ToList();
        }
    }

    public static void SaveOrder(IEnumerable<IEngine> ordered)
    {
        App.Settings.EngineOrder = ordered.Select(e => e.Id).ToList();
        App.Settings.Save();
    }

    private sealed record Step(IEngine Engine, Preset Preset);

    private static IEngine? First(string ext, Preset p) => Engines.FirstOrDefault(e => e.Ready && e.CanHandle(ext, p));

    /// <summary>Direct engine, or two engines joined through a common middle format (e.g. EPUB → HTML → PDF).</summary>
    private static List<Step>? Plan(string ext, Preset target)
    {
        var direct = First(ext, target);
        if (direct is not null) return new() { new Step(direct, target) };
        if (target.Mode != "") return null;
        foreach (var mid in Mids)
        {
            if (mid == ext || mid == target.Extension) continue;
            var m = new Preset("mid" + mid, "", "", mid);
            var a = First(ext, m);
            var b = a is null ? null : First(mid, target);
            if (a is not null && b is not null) return new() { new Step(a, m), new Step(b, target) };
        }
        return null;
    }

    public static bool IsSupported(string path, Preset p) => Plan(Directory.Exists(path) ? "" : Util.Ext(path), p) is not null;

    public static IReadOnlyList<string> Run(string input, Preset p, string? outDir = null)
    {
        var ext = Directory.Exists(input) ? "" : Util.Ext(input);
        var plan = Plan(ext, p) ?? throw new NotSupportedException($"No engine can turn {(ext == "" ? "this folder" : ext)} into {p.Name}");
        var dest = outDir ?? Chosen() ?? Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? ".";
        if (plan.Count == 1) return plan[0].Engine.Convert(input, p, dest);

        var tmp = Util.TempDir();
        try
        {
            var mid = plan[0].Engine.Convert(input, plan[0].Preset, tmp);
            var outs = new List<string>();
            foreach (var m in mid) outs.AddRange(plan[1].Engine.Convert(m, p, dest));
            return outs;
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup is best-effort */ } }
    }

    public static IReadOnlyList<string> RunMany(IReadOnlyList<string> inputs, Preset p, string? outDir = null)
    {
        var ext = Util.Ext(inputs[0]);
        var eng = First(ext, p) ?? throw new NotSupportedException($"No engine can combine {ext} files");
        return eng.ConvertMany(inputs, p, outDir ?? Chosen() ?? Path.GetDirectoryName(inputs[0])!);
    }

    /// <summary>The folder the user chose for results, or null for "next to the original".</summary>
    private static string? Chosen()
    {
        var f = App.Settings.OutputFolder;
        if (string.IsNullOrWhiteSpace(f)) return null;
        try { Directory.CreateDirectory(f); return f; } catch { return null; }
    }

    /// <summary>Presets that can convert the picked files, the ones that fit the most files first (a mixed batch still gets suggestions).</summary>
    public static List<Preset> Suggest(IEnumerable<string> files, int max = 8)
    {
        // what a preset can do depends only on a file's type, so each type is asked once, however many files there are
        var kinds = files.GroupBy(f => Directory.Exists(f) ? "" : Util.Ext(f)).Select(g => (Probe: "x" + g.Key, Count: g.Count())).ToList();
        return Presets.Where(p => p.Mode is "" or "ocr" or "annotate" or "pdfedit")
            .Select(p => (Preset: p, Fit: kinds.Where(k => IsSupported(k.Probe, p)).Sum(k => k.Count)))
            .Where(x => x.Fit > 0)
            .OrderByDescending(x => x.Fit)
            .Select(x => x.Preset)
            .DistinctBy(p => p.Extension + "|" + p.Mode + "|" + p.Scale + p.Gray + p.Quality)   // "To PDF" and "HWP to PDF" are the same job
            .Take(max).ToList();
    }
}
