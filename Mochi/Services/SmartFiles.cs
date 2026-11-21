using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mochi.Services;

/// <summary>Turns text into 384 numbers that capture its meaning (all-MiniLM-L6-v2, quantised, bundled, offline).</summary>
public static class Embedder
{
    private static InferenceSession? _session;
    private static Dictionary<string, int>? _vocab;
    private static readonly object Gate = new();
    public const int Dim = 384;

    private static byte[] Asset(string name)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://Mochi/Assets/models/" + name));
            using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }
        catch (InvalidOperationException) when (Environment.GetEnvironmentVariable("MOCHI_MODELS") is { } dir)   // command-line runs have no asset loader
        {
            return File.ReadAllBytes(Path.Combine(dir, name));
        }
    }

    private static void Load()
    {
        lock (Gate)
        {
            if (_session is not null) return;
            _vocab = new Dictionary<string, int>();
            var i = 0;
            foreach (var line in Encoding.UTF8.GetString(Asset("minilm-vocab.txt")).Split('\n')) _vocab[line.TrimEnd('\r')] = i++;
            _session = new InferenceSession(Asset("minilm.onnx"), new SessionOptions { IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2), GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        }
    }

    // ---- WordPiece, the way BERT (uncased) reads text
    private static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch0 in text.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch0);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            var ch = char.ToLowerInvariant(ch0);
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } }
            else if (char.IsPunctuation(ch) || char.IsSymbol(ch) || (ch >= 0x4E00 && ch <= 0x9FFF))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return ch.ToString();
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static List<int> Tokens(string text, int max)
    {
        var ids = new List<int> { _vocab!["[CLS]"] };
        foreach (var w in Words(text))
        {
            if (ids.Count >= max - 1) break;
            if (w.Length > 100) { ids.Add(_vocab["[UNK]"]); continue; }
            var pieces = new List<int>(); var start = 0; var bad = false;
            while (start < w.Length)
            {
                var end = w.Length; int found = -1;
                while (start < end)
                {
                    var piece = (start > 0 ? "##" : "") + w[start..end];
                    if (_vocab.TryGetValue(piece, out var id)) { found = id; break; }
                    end--;
                }
                if (found < 0) { bad = true; break; }
                pieces.Add(found); start = end;
            }
            if (bad) ids.Add(_vocab["[UNK]"]); else ids.AddRange(pieces);
        }
        if (ids.Count > max - 1) ids.RemoveRange(max - 1, ids.Count - (max - 1));
        ids.Add(_vocab["[SEP]"]);
        return ids;
    }

    /// <summary>
    /// One unit-length vector per text, texts cut at <paramref name="maxTokens"/> word pieces. Texts are batched by length, so a short
    /// text is not padded to the length of a long one (that alone made the first version several times slower).
    /// </summary>
    public static float[][] Embed(IReadOnlyList<string> texts, int maxTokens = 128, int batch = 32)
    {
        Load();
        var toks = texts.Select(t => Tokens(t, maxTokens)).ToList();
        var order = Enumerable.Range(0, texts.Count).OrderBy(i => toks[i].Count).ToList();
        var result = new float[texts.Count][];
        for (var b = 0; b < order.Count; b += batch)
        {
            var idx = order.Skip(b).Take(batch).ToList(); var n = idx.Count;
            var len = idx.Max(i => toks[i].Count);
            var ids = new DenseTensor<long>(new[] { n, len }); var mask = new DenseTensor<long>(new[] { n, len }); var types = new DenseTensor<long>(new[] { n, len });
            for (var r = 0; r < n; r++)
                for (var j = 0; j < toks[idx[r]].Count; j++) { ids[r, j] = toks[idx[r]][j]; mask[r, j] = 1; }
            float[] hidden;
            lock (Gate)
            {
                using var res = _session!.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask), NamedOnnxValue.CreateFromTensor("token_type_ids", types),
                });
                hidden = res.First().AsTensor<float>().ToArray();
            }
            for (var r = 0; r < n; r++)
            {
                var v = new float[Dim]; var count = toks[idx[r]].Count;
                for (var j = 0; j < count; j++)
                    for (var d = 0; d < Dim; d++) v[d] += hidden[(r * len + j) * Dim + d];
                double norm = 0;
                for (var d = 0; d < Dim; d++) { v[d] /= Math.Max(1, count); norm += v[d] * v[d]; }
                var inv = (float)(1 / Math.Max(1e-9, Math.Sqrt(norm)));
                for (var d = 0; d < Dim; d++) v[d] *= inv;
                result[idx[r]] = v;
            }
        }
        return result;
    }

    public static float Dot(float[] a, float[] b) { float s = 0; for (var i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }
}

/// <summary>Reads the words out of a file, if it has any: text, PDF, Word, Excel, PowerPoint, web pages, subtitles…</summary>
public static class TextExtractor
{
    private static readonly HashSet<string> Plain = new()
    {
        ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".toml", ".log", ".srt", ".vtt", ".ass", ".lrc", ".ini", ".tex", ".rst",
        ".py", ".js", ".ts", ".cs", ".java", ".c", ".cpp", ".h", ".go", ".rs", ".php", ".rb", ".sql", ".sh", ".bat", ".ps1", ".css",
    };

    public static bool HasText(string ext) => Plain.Contains(ext) || ext is ".html" or ".htm" or ".rtf" or ".pdf" or ".docx" or ".pptx" or ".xlsx";

    /// <param name="deep">Read much further into long PDFs and big sheets (for the AI tools); the search index keeps the quick default.</param>
    public static string? Read(string path, int maxChars = 6000, bool deep = false)
    {
        try
        {
            var ext = Util.Ext(path);
            var len = new FileInfo(path).Length;
            if (len == 0 || len > 40L * 1024 * 1024) return null;
            string? text = null;
            if (Plain.Contains(ext)) text = ReadHead(path, deep ? maxChars + 2000 : maxChars * 2);
            else if (ext is ".html" or ".htm") text = Regex.Replace(Regex.Replace(ReadHead(path, maxChars * 6), "(?is)<(script|style).*?</\\1>", " "), "<[^>]+>", " ");
            else if (ext == ".rtf") text = Regex.Replace(Regex.Replace(ReadHead(path, maxChars * 4), @"\\'[0-9a-f]{2}|\\[a-z]+-?\d* ?|[{}]", " "), @"\s+", " ");
            else if (ext == ".pdf") text = Pdf(path, maxChars, deep ? 120 : 4);
            else if (ext == ".docx") text = Docx(path);
            else if (ext == ".pptx") text = Pptx(path);
            else if (ext == ".xlsx") text = Xlsx(path, deep);
            if (text is null) return null;
            text = Regex.Replace(WebUtility(text), @"\s+", " ").Trim();
            return text.Length < 3 ? null : text.Length > maxChars ? text[..maxChars] : text;
        }
        catch { return null; }   // locked, damaged or protected: it is still found by its name
    }

    private static string WebUtility(string s) => System.Net.WebUtility.HtmlDecode(s);

    private static string ReadHead(string path, int chars)
    {
        using var sr = new StreamReader(path, Encoding.UTF8, true);
        var buf = new char[chars]; var n = sr.Read(buf, 0, chars);
        return new string(buf, 0, n);
    }

    private static string Pdf(string path, int maxChars, int pages)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages().Take(pages)) { sb.Append(page.Text).Append(' '); if (sb.Length > maxChars * 2) break; }
        return sb.ToString();
    }

    private static string Docx(string path)
    {
        using var d = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
        return d.MainDocumentPart?.Document?.Body?.InnerText ?? "";
    }

    private static string Pptx(string path)
    {
        using var d = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);
        var sb = new StringBuilder();
        foreach (var s in d.PresentationPart?.SlideParts ?? Enumerable.Empty<DocumentFormat.OpenXml.Packaging.SlidePart>()) sb.Append(s.Slide.InnerText).Append(' ');
        return sb.ToString();
    }

    private static string Xlsx(string path, bool deep)
    {
        using var wb = new ClosedXML.Excel.XLWorkbook(path);
        var sb = new StringBuilder();
        foreach (var ws in wb.Worksheets.Take(deep ? 10 : 3))
        {
            sb.Append(ws.Name).Append(' ');
            foreach (var c in ws.CellsUsed().Take(deep ? 8000 : 400)) sb.Append(c.GetString()).Append(' ');
        }
        return sb.ToString();
    }
}

public sealed class Chunk
{
    public string Text { get; set; }
    public float[]? Vec { get; set; }
    public Chunk(string text, float[]? vec) { Text = text; Vec = vec; }
}

/// <summary>One file in the index. It is searchable by name and words as soon as it is listed; the meaning vectors arrive a little later.</summary>
public sealed class IndexedFile
{
    public string Path { get; }
    public long Size { get; }
    public long Ticks { get; }
    public string Kind { get; }
    public string Head { get; set; } = "";          // the first lines of text, for word search and snippets
    public bool Deep { get; set; }                  // longer text already embedded
    public List<Chunk> Chunks { get; } = new();
    public string NameText { get; }
    private string? _hay;
    public string Hay => _hay ??= (NameText + " " + Head).ToLowerInvariant();
    public void ResetHay() => _hay = null;

    public IndexedFile(string path, long size, long ticks, string kind)
    {
        Path = path; Size = size; Ticks = ticks; Kind = kind; NameText = FileIndex.NameWords(path);
    }
}

public sealed record Hit(IndexedFile File, double Score, string Snippet);

/// <summary>File lists and name helpers shared by the index and the tidy-up tool.</summary>
public static class FileIndex
{
    private static readonly string[] SkipDirs = { "node_modules", ".git", "bin", "obj", "__pycache__", "$RECYCLE.BIN", "System Volume Information", ".venv", "venv", "AppData", "Windows", "Program Files", "Program Files (x86)", ".gradle", ".nuget", "site-packages", "Library" };
    private static readonly HashSet<string> NotWorthIndexing = new() { ".exe", ".msi", ".dll", ".sys", ".bin", ".dat", ".tmp", ".lnk", ".iso", ".cab", ".apk", ".dmg", ".pkg", ".class", ".pyc", ".o", ".obj", ".pdb", ".log" };

    public static IEnumerable<string> Enumerate(string folder)
    {
        var stack = new Stack<string>(); stack.Push(folder);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<string> files = Array.Empty<string>(), subs = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(d); subs = Directory.EnumerateDirectories(d); } catch { /* no access */ }
            foreach (var f in files)
            {
                var name = System.IO.Path.GetFileName(f);
                if (name.StartsWith("~$") || name.StartsWith('.') || NotWorthIndexing.Contains(Util.Ext(f))) continue;
                yield return f;
            }
            foreach (var s in subs)
            {
                var n = System.IO.Path.GetFileName(s);
                if (SkipDirs.Contains(n, StringComparer.OrdinalIgnoreCase) || n.StartsWith('.')) continue;
                stack.Push(s);
            }
        }
    }

    public static string KindOf(string ext) => ext switch
    {
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".svg" => "Image",
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v" => "Video",
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac" => "Audio",
        ".pdf" => "Pdf",
        ".xlsx" or ".xls" or ".csv" => "Spreadsheet",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "Archive",
        _ => "Document",
    };

    /// <summary>The words of the file name, so "IMG_Trip_Goa_2024.jpg" is found by "goa trip".</summary>
    public static string NameWords(string path) =>
        Regex.Replace(Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(path), @"([a-z])([A-Z])", "$1 $2"), @"[_\-.\s]+", " ").Trim();
}

/// <summary>
/// The search index of one folder, built in the background and kept on disk.
/// Order of work: list the files (searchable by name at once) → read their first lines (searchable by words) →
/// one short meaning vector each → longer text in the background. Search works at every stage.
/// </summary>
public sealed class IndexStore
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IndexStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    public static IndexStore For(string folder) => Stores.GetOrAdd(System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'), f => new IndexStore(f));

    public string Folder { get; }
    public IndexedFile[] Files { get; private set; } = Array.Empty<IndexedFile>();
    public int Total { get; private set; }
    public int Ready { get; private set; }     // files with a meaning vector
    public int Deepened { get; private set; }
    public bool Busy { get; private set; }
    public string Phase { get; private set; } = "";
    public event Action? Changed;

    private readonly object _gate = new();
    private Task? _run;
    private bool _loaded;

    private IndexStore(string folder) { Folder = folder; }

    private string CachePath
    {
        get
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi", "index");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Folder.ToLowerInvariant())))[..16] + ".v3");
        }
    }

    /// <summary>Starts (or joins) an incremental refresh on a low-priority thread.</summary>
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) return _run;
            var tcs = new TaskCompletionSource();
            var t = new Thread(() =>
            {
                try { Run(); } catch { /* the index is a convenience: a failure leaves what is there */ }
                finally { Busy = false; Phase = ""; Changed?.Invoke(); tcs.TrySetResult(); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "mochi-index" };
            Busy = true; t.Start();
            return _run = tcs.Task;
        }
    }

    private void Run()
    {
        var cached = _loaded ? Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) : Load();
        _loaded = true;

        // 1. the file list: names are searchable right away
        Phase = "list"; Changed?.Invoke();
        var current = new List<IndexedFile>(); var fresh = new List<IndexedFile>();
        foreach (var p in FileIndex.Enumerate(Folder))
        {
            FileInfo fi;
            try { fi = new FileInfo(p); } catch { continue; }
            if (cached.TryGetValue(p, out var old) && old.Size == fi.Length && old.Ticks == fi.LastWriteTimeUtc.Ticks) { current.Add(old); continue; }
            var f = new IndexedFile(p, fi.Length, fi.LastWriteTimeUtc.Ticks, FileIndex.KindOf(Util.Ext(p)));
            current.Add(f); fresh.Add(f);
        }
        Files = current.ToArray(); Total = Files.Length; Ready = Files.Count(f => f.Chunks.Any(c => c.Vec is not null)); Deepened = Files.Count(f => f.Deep);
        Changed?.Invoke();

        // 2. the first lines of every new file, several at a time (recent documents first)
        if (fresh.Count > 0)
        {
            Phase = "read"; Changed?.Invoke();
            var order = fresh.OrderByDescending(f => TextExtractor.HasText(Util.Ext(f.Path))).ThenByDescending(f => f.Ticks).ToList();
            Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, f =>
            {
                var ext = Util.Ext(f.Path);
                var head = f.Size <= 20L * 1024 * 1024 && TextExtractor.HasText(ext) ? TextExtractor.Read(f.Path, 1500) : null;
                f.Head = head ?? "";
                f.ResetHay();
                var quick = f.NameText + (head is null ? "" : ". " + (head.Length > 320 ? head[..320] : head));
                lock (f) f.Chunks.Add(new Chunk(quick, null));
            });
            Changed?.Invoke();

            // 3. one short vector each: 64 word pieces is about 8 times faster than the old 256
            Phase = "meaning"; Changed?.Invoke();
            foreach (var group in order.Chunk(192))
            {
                var texts = group.Select(f => f.Chunks[0].Text).ToList();
                var vecs = Embedder.Embed(texts, 64, 32);
                for (var i = 0; i < group.Length; i++) group[i].Chunks[0].Vec = vecs[i];
                Ready = Files.Count(f => f.Chunks.Any(c => c.Vec is not null));
                Changed?.Invoke();
            }
            Save();
        }
        else if (cached.Count != current.Count) Save();   // only deletions

        // 4. the rest of long documents, quietly: more of the text, 128 word pieces per piece
        var deep = Files.Where(f => !f.Deep && f.Head.Length >= 320 && f.Size <= 20L * 1024 * 1024).OrderByDescending(f => f.Ticks).ToList();
        if (deep.Count > 0)
        {
            Phase = "deep"; Changed?.Invoke();
            var done = 0;
            foreach (var group in deep.Chunk(64))
            {
                var pieces = new List<(IndexedFile File, string Text)>();
                Parallel.ForEach(group, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, f =>
                {
                    var full = TextExtractor.Read(f.Path, 4500);
                    if (full is null || full.Length <= 320) { f.Deep = true; return; }
                    var at = 320;
                    for (var k = 0; k < 3 && at < full.Length; k++, at += 520)
                        lock (pieces) pieces.Add((f, full.Substring(at, Math.Min(520, full.Length - at))));
                    f.Head = full.Length > 3000 ? full[..3000] : full; f.ResetHay();
                });
                if (pieces.Count > 0)
                {
                    var vecs = Embedder.Embed(pieces.Select(p => p.Text).ToList(), 128, 32);
                    for (var i = 0; i < pieces.Count; i++) lock (pieces[i].File) pieces[i].File.Chunks.Add(new Chunk(pieces[i].Text, vecs[i]));
                }
                foreach (var f in group) f.Deep = true;
                done += group.Length; Deepened = Files.Count(f => f.Deep);
                if (done % 256 == 0) { Save(); }
                Changed?.Invoke();
            }
            Save();
        }
    }

    // ---------------------------------------------------------------- search

    /// <summary>Works at any stage: words and names count from the start, meaning counts for the files that already have vectors.</summary>
    public List<Hit> Search(string query, float[]? queryVec, int max = 40)
    {
        var words = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value).Distinct().ToList();
        var hits = new List<Hit>();
        foreach (var f in Files)
        {
            double best = -1; Chunk? bestChunk = null;
            if (queryVec is not null)
                lock (f)
                    foreach (var c in f.Chunks)
                    {
                        if (c.Vec is null) continue;
                        var s = Embedder.Dot(queryVec, c.Vec);
                        if (s > best) { best = s; bestChunk = c; }
                    }
            var hay = f.Hay;
            var lexical = words.Count == 0 ? 0 : words.Count(w => hay.Contains(w)) / (double)words.Count;
            var nameLow = f.NameText.ToLowerInvariant();
            var nameHit = words.Count == 0 ? 0 : words.Count(w => nameLow.Contains(w)) / (double)words.Count;
            double score;
            if (bestChunk is not null)
            {
                var nameOnly = f.Head.Length == 0;   // a picture or a video: only its name to go on
                score = (nameOnly ? best * 0.65 : best) + 0.25 * lexical + 0.15 * nameHit;
            }
            else
            {
                if (lexical == 0) continue;         // no vector yet and no word in common
                score = 0.20 + 0.55 * lexical + 0.25 * nameHit;
            }
            hits.Add(new Hit(f, score, Snippet(bestChunk?.Text ?? f.Head, words)));
        }
        return hits.OrderByDescending(h => h.Score).Take(max).ToList();
    }

    private static string Snippet(string text, List<string> words)
    {
        if (text.Length == 0) return "";
        var low = text.ToLowerInvariant(); var at = -1;
        foreach (var w in words) { var i = low.IndexOf(w, StringComparison.Ordinal); if (i >= 0 && (at < 0 || i < at)) at = i; }
        var start = Math.Max(0, (at < 0 ? 0 : at) - 40);
        var s = text.Substring(start, Math.Min(220, text.Length - start)).Trim();
        return (start > 0 ? "…" : "") + s + (start + 220 < text.Length ? "…" : "");
    }

    /// <summary>Searches several folders at once.</summary>
    public static List<Hit> SearchAll(IEnumerable<IndexStore> stores, string query, int max = 40)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var q = Embedder.Embed(new[] { query }, 64, 1)[0];
        return stores.SelectMany(s => s.Search(query, q, max)).OrderByDescending(h => h.Score).Take(max).ToList();
    }

    // ---------------------------------------------------------------- the saved index

    private Dictionary<string, IndexedFile> Load()
    {
        var dict = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(CachePath)) return dict;
            using var br = new BinaryReader(File.OpenRead(CachePath));
            if (br.ReadInt32() != 3) return dict;
            var n = br.ReadInt32();
            for (var i = 0; i < n; i++)
            {
                var f = new IndexedFile(br.ReadString(), br.ReadInt64(), br.ReadInt64(), br.ReadString()) { Head = br.ReadString(), Deep = br.ReadBoolean() };
                var cc = br.ReadInt32();
                for (var c = 0; c < cc; c++)
                {
                    var text = br.ReadString(); float[]? v = null;
                    if (br.ReadBoolean()) { v = new float[Embedder.Dim]; for (var d = 0; d < v.Length; d++) v[d] = br.ReadSingle(); }
                    f.Chunks.Add(new Chunk(text, v));
                }
                dict[f.Path] = f;
            }
        }
        catch { dict.Clear(); }
        // show what the saved index knows before the refresh has even looked at the disk
        Files = dict.Values.ToArray(); Total = Files.Length; Ready = Files.Count(f => f.Chunks.Any(c => c.Vec is not null)); Deepened = Files.Count(f => f.Deep);
        return dict;
    }

    private void Save()
    {
        try
        {
            var snapshot = Files;
            var tmp = CachePath + ".tmp";
            using (var bw = new BinaryWriter(File.Create(tmp)))
            {
                bw.Write(3); bw.Write(snapshot.Length);
                foreach (var f in snapshot)
                {
                    bw.Write(f.Path); bw.Write(f.Size); bw.Write(f.Ticks); bw.Write(f.Kind); bw.Write(f.Head.Length > 1500 ? f.Head[..1500] : f.Head); bw.Write(f.Deep);
                    List<Chunk> cs; lock (f) cs = f.Chunks.ToList();
                    bw.Write(cs.Count);
                    foreach (var c in cs)
                    {
                        bw.Write(c.Text.Length > 700 ? c.Text[..700] : c.Text);
                        bw.Write(c.Vec is not null);
                        if (c.Vec is not null) foreach (var x in c.Vec) bw.Write(x);
                    }
                }
            }
            File.Move(tmp, CachePath, true);
        }
        catch { /* only a speed-up */ }
    }
}

/// <summary>One planned move: a file, and the sub-folder of the messy folder it should go to.</summary>
public sealed record PlannedMove(string Source, string Target, string Group);

public enum OrganizeMode { ByType, ByTypeAndMonth, ByTopic }

/// <summary>Tidies the loose files of a folder into sub-folders: by kind, by kind and month, or documents by what they are about.</summary>
public static class Organizer
{
    private static readonly Dictionary<string, string> Kinds = BuildKinds();

    private static Dictionary<string, string> BuildKinds()
    {
        var d = new Dictionary<string, string>();
        void Add(string folder, string exts) { foreach (var e in exts.Split(' ')) d["." + e] = folder; }
        Add("Pictures", "png jpg jpeg webp gif bmp tif tiff heic heif avif ico raw cr2 nef dng");
        Add("Videos", "mp4 mov mkv avi webm m4v wmv flv mpg mpeg 3gp");
        Add("Music & Audio", "mp3 wav flac m4a ogg aac wma opus aiff mid");
        Add("PDFs", "pdf");
        Add("Documents", "doc docx odt rtf txt md pages tex");
        Add("Spreadsheets", "xls xlsx ods csv tsv numbers");
        Add("Presentations", "ppt pptx odp key");
        Add("Archives", "zip rar 7z tar gz bz2 xz iso");
        Add("Apps & Installers", "exe msi apk dmg pkg deb appx msix");
        Add("Code & Data", "py js ts cs java c cpp h go rs php rb sql sh bat ps1 json xml yaml yml html htm css ipynb");
        Add("Fonts", "ttf otf woff woff2");
        Add("Design & 3D", "psd ai svg fig xd sketch stl obj fbx glb gltf blend dwg dxf");
        Add("Subtitles", "srt vtt ass lrc");
        Add("E-books", "epub mobi azw3 kepub");
        return d;
    }

    /// <summary>How alike two documents must be to share a topic folder (cosine of their meaning vectors).</summary>
    public static float TopicThreshold { get; set; } = 0.28f;

    public static string KindFolder(string path) => Kinds.TryGetValue(Util.Ext(path), out var k) ? k : "Other files";

    private static bool Skip(string name, FileAttributes attr)
    {
        var ext = Util.Ext(name);
        return attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System) || name.StartsWith('.') || name.StartsWith("~$")
               || ext is ".lnk" or ".ini" or ".tmp" or ".part" or ".crdownload" or ".download" or ".partial" or ".url";
    }

    public static List<PlannedMove> Plan(string folder, OrganizeMode mode, IProgress<string>? status = null)
    {
        var files = Directory.EnumerateFiles(folder).Where(f => { try { return !Skip(Path.GetFileName(f), File.GetAttributes(f)); } catch { return false; } }).ToList();
        var moves = new List<PlannedMove>();
        var topicFiles = new List<string>();
        foreach (var f in files)
        {
            var kind = KindFolder(f);
            if (mode == OrganizeMode.ByTopic && kind is "PDFs" or "Documents" or "Spreadsheets" or "Presentations") { topicFiles.Add(f); continue; }
            var group = mode == OrganizeMode.ByTypeAndMonth ? Path.Combine(kind, File.GetLastWriteTime(f).ToString("yyyy-MM", CultureInfo.InvariantCulture)) : kind;
            moves.Add(new PlannedMove(f, Path.Combine(folder, group, Path.GetFileName(f)), group));
        }
        if (topicFiles.Count > 0) moves.AddRange(Topics(folder, topicFiles, status));
        return moves.Where(m => !string.Equals(Path.GetDirectoryName(m.Target), folder, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static readonly HashSet<string> Stop = new(("the and for with that this from have your you are was were not but can will all any one our their his her has had been " +
        "they them what when where which who how why more than also into over such only other about these those there then its out off per via")
        .Split(' '));

    /// <summary>Documents grouped by meaning: k-means on their vectors, each group named after its most telling words.</summary>
    private static List<PlannedMove> Topics(string folder, List<string> docs, IProgress<string>? status)
    {
        var moves = new List<PlannedMove>();
        if (docs.Count < 6)   // too few to find topics
        {
            foreach (var f in docs) moves.Add(new PlannedMove(f, Path.Combine(folder, KindFolder(f), Path.GetFileName(f)), KindFolder(f)));
            return moves;
        }
        status?.Report("Reading " + docs.Count + " documents…");
        var texts = docs.Select(f => (TextExtractor.Read(f, 3000) ?? "")).ToList();
        var vecs = Embedder.Embed(docs.Select((f, i) => FileIndex.NameWords(f) + ". " + (texts[i].Length > 600 ? texts[i][..600] : texts[i])).ToList(), 128, 32);
        var label = Group(vecs, TopicThreshold);
        var k = label.Max() + 1;

        var words = texts.Select((t, i) => Regex.Matches((FileIndex.NameWords(docs[i]) + " " + t).ToLowerInvariant(), @"[a-z]{4,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).Distinct().ToList()).ToList();
        var df = new Dictionary<string, int>();
        foreach (var ws in words) foreach (var w in ws) df[w] = df.GetValueOrDefault(w) + 1;
        var topic = 0;
        for (var c = 0; c < k; c++)
        {
            var members = Enumerable.Range(0, docs.Count).Where(i => label[i] == c).ToList();
            if (members.Count == 0) continue;
            if (members.Count < 2)   // alone: it stays with its kind
            {
                var f = docs[members[0]]; var kf = KindFolder(f);
                moves.Add(new PlannedMove(f, Path.Combine(folder, kf, Path.GetFileName(f)), kf));
                continue;
            }
            var local = new Dictionary<string, int>();
            foreach (var i in members) foreach (var w in words[i]) local[w] = local.GetValueOrDefault(w) + 1;
            var top = local.Where(kv => kv.Value >= Math.Max(2, (members.Count + 1) / 2)).OrderByDescending(kv => kv.Value * Math.Log((double)docs.Count / df[kv.Key] + 1)).Take(2).Select(kv => kv.Key).ToList();
            var name = top.Count > 0 ? "Documents - " + string.Join(' ', top) : "Documents - topic " + (++topic);
            foreach (var i in members) moves.Add(new PlannedMove(docs[i], Path.Combine(folder, name, Path.GetFileName(docs[i])), name));
        }
        return moves;
    }

    /// <summary>Average-linkage grouping: keep joining the two most alike groups while they are alike enough (small folders), k-means beyond that.</summary>
    private static int[] Group(float[][] v, float threshold)
    {
        var n = v.Length;
        if (n > 400) return KMeans(v, Math.Clamp(n / 12, 4, 12));
        var clusters = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        while (clusters.Count > 1)
        {
            float best = float.MinValue; int bi = 0, bj = 1;
            for (var i = 0; i < clusters.Count; i++)
                for (var j = i + 1; j < clusters.Count; j++)
                {
                    float sum = 0;
                    foreach (var a in clusters[i]) foreach (var b in clusters[j]) sum += Embedder.Dot(v[a], v[b]);
                    var avg = sum / (clusters[i].Count * clusters[j].Count);
                    if (avg > best) { best = avg; bi = i; bj = j; }
                }
            if (best < threshold) break;
            clusters[bi].AddRange(clusters[bj]); clusters.RemoveAt(bj);
        }
        var label = new int[n];
        for (var c = 0; c < clusters.Count; c++) foreach (var i in clusters[c]) label[i] = c;
        return label;
    }

    private static int[] KMeans(float[][] v, int k)
    {
        var rnd = new Random(11); var n = v.Length;
        var centers = new List<float[]> { (float[])v[rnd.Next(n)].Clone() };
        while (centers.Count < k)   // k-means++: the next centre is likely far from the ones so far
        {
            var d = v.Select(x => centers.Max(c => Embedder.Dot(x, c))).Select(s => Math.Max(1e-6, 1 - s)).ToArray();
            var pick = rnd.NextDouble() * d.Sum(); var acc = 0.0; var idx = n - 1;
            for (var i = 0; i < n; i++) { acc += d[i]; if (acc >= pick) { idx = i; break; } }
            centers.Add((float[])v[idx].Clone());
        }
        var label = new int[n];
        for (var iter = 0; iter < 20; iter++)
        {
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                var best = 0; var bs = float.MinValue;
                for (var c = 0; c < k; c++) { var s = Embedder.Dot(v[i], centers[c]); if (s > bs) { bs = s; best = c; } }
                if (label[i] != best) { label[i] = best; changed = true; }
            }
            if (!changed) break;
            for (var c = 0; c < k; c++)
            {
                var sum = new float[Embedder.Dim]; var cnt = 0;
                for (var i = 0; i < n; i++) if (label[i] == c) { cnt++; for (var d = 0; d < sum.Length; d++) sum[d] += v[i][d]; }
                if (cnt == 0) continue;
                double norm = 0; foreach (var x in sum) norm += x * x;
                var inv = (float)(1 / Math.Max(1e-9, Math.Sqrt(norm)));
                for (var d = 0; d < sum.Length; d++) sum[d] *= inv;
                centers[c] = sum;
            }
        }
        return label;
    }

    // ---- doing it, and undoing it
    private static string UndoPath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "organize-undo.json");
        }
    }

    public sealed record Done(string From, string To);

    public static int Apply(IEnumerable<PlannedMove> moves, string folder)
    {
        var done = new List<Done>();
        foreach (var m in moves)
        {
            try
            {
                if (!File.Exists(m.Source)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(m.Target)!);
                var to = Util.Unique(m.Target);
                File.Move(m.Source, to);
                done.Add(new Done(m.Source, to));
            }
            catch { /* in use or protected: stays where it is */ }
        }
        if (done.Count > 0) File.WriteAllText(UndoPath, System.Text.Json.JsonSerializer.Serialize(new { Folder = folder, Moves = done }));
        return done.Count;
    }

    public static bool CanUndo(string folder)
    {
        try { return File.Exists(UndoPath) && System.Text.Json.JsonDocument.Parse(File.ReadAllText(UndoPath)).RootElement.GetProperty("Folder").GetString()?.Equals(folder, StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    /// <summary>Puts every moved file back where it was, then removes the sub-folders that are empty again.</summary>
    public static int Undo(string folder)
    {
        if (!CanUndo(folder)) return 0;
        var root = System.Text.Json.JsonDocument.Parse(File.ReadAllText(UndoPath)).RootElement;
        var back = 0; var dirs = new HashSet<string>();
        foreach (var m in root.GetProperty("Moves").EnumerateArray())
        {
            var from = m.GetProperty("From").GetString()!; var to = m.GetProperty("To").GetString()!;
            try
            {
                if (!File.Exists(to)) continue;
                File.Move(to, Util.Unique(from)); back++;
                dirs.Add(Path.GetDirectoryName(to)!);
            }
            catch { /* moved away since */ }
        }
        foreach (var d in dirs.OrderByDescending(x => x.Length))
        {
            try
            {
                var p = d;
                while (p.Length > folder.Length && Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any()) { Directory.Delete(p); p = Path.GetDirectoryName(p)!; }
            }
            catch { /* keep it */ }
        }
        File.Delete(UndoPath);
        return back;
    }
}
