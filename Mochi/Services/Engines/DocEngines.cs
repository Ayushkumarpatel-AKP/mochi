using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MimeKit;
using SkiaSharp;

namespace Mochi.Services.Engines;

public sealed record Block(string Kind, string Text, List<List<string>>? Rows = null);

/// <summary>Text documents through a common block model: DOCX, Markdown, HTML, TXT, source code.</summary>
public sealed class DocumentEngine : EngineBase
{
    private static readonly string[] Code =
    {
        ".py", ".js", ".ts", ".jsx", ".tsx", ".java", ".c", ".h", ".cpp", ".hpp", ".cs", ".go", ".rs", ".rb", ".php", ".sh", ".bat",
        ".ps1", ".sql", ".css", ".log", ".ini", ".cfg", ".conf", ".swift", ".kt", ".lua", ".r",
    };

    public override string Id => "document";
    public override string Name => "Documents (built-in)";
    public override string Category => "Document";
    public override string Description => "DOCX, Markdown, HTML and text into each other; source files to UTF-8 text. Scripts are never run.";
    public override string Formats => "DOCX MD HTML TXT → DOCX MD HTML TXT · code → TXT";

    public override bool CanHandle(string e, Preset p)
    {
        if (p.Mode != "") return false;
        var o = p.Extension;
        return e switch
        {
            ".docx" => Is(o, ".txt", ".html", ".md"),
            ".md" or ".markdown" => Is(o, ".html", ".txt", ".docx"),
            ".txt" => Is(o, ".docx", ".html", ".md"),
            ".html" or ".htm" => Is(o, ".md", ".txt", ".docx"),
            _ => Is(e, Code) && o == ".txt",
        };
    }

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var e = Util.Ext(input);
        var dest = Util.Out(input, outDir, p.Extension);

        if (e is ".md" or ".markdown" && p.Extension == ".html")
        {
            var md = Markdig.Markdown.ToHtml(Util.ReadText(input));
            Util.WriteText(dest, Page(Path.GetFileNameWithoutExtension(input), md));
            return new[] { dest };
        }
        if (e is ".html" or ".htm" && p.Extension == ".md")
        {
            Util.WriteText(dest, new ReverseMarkdown.Converter().Convert(Util.ReadText(input)));
            return new[] { dest };
        }

        var blocks = Read(e, input);
        switch (p.Extension)
        {
            case ".txt": Util.WriteText(dest, ToText(blocks)); break;
            case ".md": Util.WriteText(dest, ToMarkdown(blocks)); break;
            case ".html": Util.WriteText(dest, Page(Path.GetFileNameWithoutExtension(input), ToHtmlBody(blocks))); break;
            case ".docx": WriteDocx(dest, blocks); break;
        }
        return new[] { dest };
    }

    public static List<Block> ReadFile(string e, string path) => new DocumentEngine().Read(e, path);

    private List<Block> Read(string e, string path) => e switch
    {
        ".docx" => ReadDocx(path),
        ".md" or ".markdown" => ReadMarkdown(Util.ReadText(path)),
        ".html" or ".htm" => ParseHtml(Util.ReadText(path)),
        _ => Paragraphs(Util.ReadText(path)),
    };

    // ---- readers
    public static List<Block> Paragraphs(string text) =>
        System.Text.RegularExpressions.Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n")
            .Select(s => s.Trim('\n')).Where(s => s.Length > 0).Select(s => new Block("p", s)).ToList();

    private static List<Block> ReadMarkdown(string text)
    {
        var res = new List<Block>(); var para = new StringBuilder();
        void Flush() { if (para.Length > 0) { res.Add(new Block("p", para.ToString().Trim())); para.Clear(); } }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var h = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            var li = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(?:[-*+]|\d+\.)\s+(.*)$");
            if (h.Success) { Flush(); res.Add(new Block("h" + h.Groups[1].Length, h.Groups[2].Value)); }
            else if (li.Success) { Flush(); res.Add(new Block("li", li.Groups[1].Value)); }
            else if (line.Length == 0) Flush();
            else para.Append(para.Length > 0 ? " " : "").Append(line.Trim());
        }
        Flush();
        return res;
    }

    public static List<Block> ParseHtml(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var res = new List<Block>();
        foreach (var el in doc.Body?.QuerySelectorAll("h1,h2,h3,h4,h5,h6,p,li,table,pre") ?? Enumerable.Empty<AngleSharp.Dom.IElement>())
        {
            var tag = el.LocalName;
            if (tag != "table" && el.Closest("table") is not null) continue;
            if (tag == "p" && el.Closest("li") is not null) continue;
            if (tag == "table")
                res.Add(new Block("table", "", el.QuerySelectorAll("tr").Select(r => r.QuerySelectorAll("th,td").Select(c => c.TextContent.Trim()).ToList()).ToList()));
            else
            {
                var t = el.TextContent.Trim();
                if (t.Length > 0) res.Add(new Block(tag == "li" ? "li" : tag == "pre" ? "p" : tag, t));
            }
        }
        return res;
    }

    private static List<Block> ReadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var res = new List<Block>();
        foreach (var el in doc.MainDocumentPart!.Document.Body!.Elements())
        {
            if (el is Paragraph para)
            {
                var text = para.InnerText;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var style = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                var kind = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var lv) ? "h" + Math.Clamp(lv, 1, 6)
                    : style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? "h1"
                    : para.ParagraphProperties?.NumberingProperties is not null ? "li" : "p";
                res.Add(new Block(kind, text));
            }
            else if (el is Table t)
                res.Add(new Block("table", "", t.Elements<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList()));
        }
        return res;
    }

    // ---- writers
    public static string ToText(IEnumerable<Block> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            if (b.Kind == "table") foreach (var r in b.Rows!) sb.AppendLine(string.Join("\t", r));
            else sb.AppendLine(b.Kind == "li" ? "• " + b.Text : b.Text);
            if (b.Kind != "li") sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    public static string ToMarkdown(IEnumerable<Block> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            if (b.Kind.Length == 2 && b.Kind[0] == 'h') sb.AppendLine(new string('#', b.Kind[1] - '0') + " " + b.Text).AppendLine();
            else if (b.Kind == "li") sb.AppendLine("- " + b.Text);
            else if (b.Kind == "table" && b.Rows!.Count > 0)
            {
                sb.AppendLine("| " + string.Join(" | ", b.Rows[0]) + " |");
                sb.AppendLine("|" + string.Concat(b.Rows[0].Select(_ => " --- |")));
                foreach (var r in b.Rows.Skip(1)) sb.AppendLine("| " + string.Join(" | ", r) + " |");
                sb.AppendLine();
            }
            else sb.AppendLine(b.Text).AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    public static string ToHtmlBody(IEnumerable<Block> blocks)
    {
        var sb = new StringBuilder(); var inList = false;
        foreach (var b in blocks)
        {
            if (b.Kind != "li" && inList) { sb.AppendLine("</ul>"); inList = false; }
            var t = WebUtility.HtmlEncode(b.Text);
            if (b.Kind == "li") { if (!inList) { sb.AppendLine("<ul>"); inList = true; } sb.AppendLine($"<li>{t}</li>"); }
            else if (b.Kind == "table")
                sb.AppendLine("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">" + string.Concat(b.Rows!.Select(r => "<tr>" + string.Concat(r.Select(c => $"<td>{WebUtility.HtmlEncode(c)}</td>")) + "</tr>")) + "</table>");
            else sb.AppendLine(b.Kind.Length == 2 && b.Kind[0] == 'h' ? $"<{b.Kind}>{t}</{b.Kind}>" : $"<p>{t.Replace("\n", "<br>")}</p>");
        }
        if (inList) sb.AppendLine("</ul>");
        return sb.ToString();
    }

    public static string Page(string title, string body) =>
        $"<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
        "<style>body{font-family:system-ui,sans-serif;max-width:760px;margin:2rem auto;padding:0 1rem;line-height:1.6}table{border-collapse:collapse}</style>" +
        $"</head><body>\n{body}</body></html>\n";

    public static void WriteDocx(string dest, IEnumerable<Block> blocks)
    {
        using var doc = WordprocessingDocument.Create(dest, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);

        static Paragraph Para(string text, string? size = null, bool bold = false)
        {
            var rp = new RunProperties();
            if (bold) rp.Append(new Bold());
            if (size is not null) rp.Append(new FontSize { Val = size });
            var parts = text.Split('\n');
            var run = new Run(rp);
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) run.Append(new Break());
                run.Append(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            return new Paragraph(run);
        }

        foreach (var b in blocks)
        {
            if (b.Kind == "table")
            {
                var tbl = new Table(new TableProperties(new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
                foreach (var r in b.Rows!)
                {
                    var row = new TableRow();
                    foreach (var c in r) row.Append(new TableCell(Para(c)));
                    tbl.Append(row);
                }
                body.Append(tbl);
            }
            else if (b.Kind.Length == 2 && b.Kind[0] == 'h')
                body.Append(Para(b.Text, (b.Kind[1] - '0') switch { 1 => "40", 2 => "32", 3 => "28", _ => "24" }, true));
            else body.Append(Para(b.Kind == "li" ? "• " + b.Text : b.Text));
        }
        main.Document.Save();
    }
}

public sealed class LibreOfficeEngine : EngineBase
{
    private static readonly string[] In =
    {
        ".doc", ".docx", ".odt", ".rtf", ".ppt", ".pptx", ".odp", ".xls", ".xlsx", ".ods", ".pages", ".numbers", ".key", ".wpd", ".pub",
        ".vsd", ".vsdx", ".txt", ".html", ".htm", ".csv", ".wps", ".dot", ".dotx", ".docm", ".xlsm", ".pptm", ".md",
    };
    private static readonly string[] Out = { ".pdf", ".docx", ".doc", ".odt", ".rtf", ".txt", ".html", ".xlsx", ".xls", ".ods", ".pptx", ".ppt", ".odp", ".csv", ".png" };

    public override string Id => "libreoffice";
    public override string Name => "LibreOffice (older and rare formats)";
    public override string Category => "Document";
    public override string Description => "Word, Excel and PowerPoint to PDF as Office draws them, and the older formats: DOC, PPT, ODT, ODS, WordPerfect, Pages, Keynote, Numbers, Publisher, Visio.";
    public override string Formats => "DOC DOCX ODT RTF XLS XLSX ODS PPT PPTX ODP PAGES KEY NUMBERS … → PDF DOCX ODT XLSX PPTX …";
    public override string? Tool => "soffice";
    public override string? WingetId => "TheDocumentFoundation.LibreOffice";

    private static readonly string[] Slides = { ".ppt", ".pptx", ".odp", ".key", ".pptm" };
    private static readonly string[] Sheets = { ".xls", ".xlsx", ".ods", ".csv", ".numbers", ".xlsm" };

    /// <summary>Slides only become slides, sheets only sheets, documents only documents; PDF, PNG and HTML work from any.</summary>
    public override bool CanHandle(string e, Preset p)
    {
        if (p.Mode != "" || !Is(e, In) || !Is(p.Extension, Out) || e == p.Extension) return false;
        var o = p.Extension;
        if (Is(o, ".pptx", ".ppt", ".odp")) return Is(e, Slides);
        if (Is(o, ".xlsx", ".xls", ".ods", ".csv")) return Is(e, Sheets);
        if (Is(o, ".docx", ".doc", ".odt", ".rtf", ".txt")) return !Is(e, Slides) && !Is(e, Sheets);
        return !(e == ".md" && o == ".png");
    }

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var tmp = Util.TempDir();
        try
        {
            var src = input;
            if (Util.Ext(input) == ".md")   // LibreOffice has no Markdown reader: go through HTML
            {
                src = Path.Combine(tmp, Path.GetFileNameWithoutExtension(input) + ".html");
                Util.WriteText(src, Markdig.Markdown.ToHtml(Util.ReadText(input)));
            }
            var target = p.Extension.TrimStart('.');
            var filter = p.Extension switch { ".txt" => "txt:Text (encoded):UTF8", ".html" => "html:XHTML Writer File:UTF8", _ => target };
            if (Util.Ext(src) is ".xlsx" or ".xls" or ".ods" && p.Extension == ".html") filter = "html:HTML (StarCalc)";
            Proc.Run(Exe, new[]
            {
                "-env:UserInstallation=file:///" + Path.Combine(tmp, "profile").Replace('\\', '/'),
                "--headless", "--convert-to", filter, "--outdir", Path.Combine(tmp, "out"), src,
            });
            var made = Directory.GetFiles(Path.Combine(tmp, "out"), "*" + p.Extension);
            if (made.Length == 0) throw new InvalidOperationException("LibreOffice produced no file");
            var dest = Util.Out(input, outDir, p.Extension);
            File.Move(made[0], dest);
            return new[] { dest };
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
    }
}

public sealed class PandocEngine : EngineBase
{
    private static readonly string[] In = { ".md", ".markdown", ".html", ".htm", ".docx", ".odt", ".rtf", ".epub", ".tex", ".org", ".rst", ".adoc", ".ipynb", ".textile" };
    private static readonly string[] Out = { ".md", ".html", ".docx", ".odt", ".rtf", ".epub", ".tex", ".rst", ".txt", ".org" };

    public override string Id => "pandoc";
    public override string Name => "Pandoc (markup formats)";
    public override string Category => "Document";
    public override string Description => "Markdown and HTML ↔ ODT and RTF, DOCX to Markdown, and LaTeX, Org, reStructuredText, AsciiDoc, Jupyter and EPUB.";
    public override string Formats => "MD HTML DOCX ODT RTF EPUB TEX ORG RST ADOC IPYNB → MD HTML DOCX ODT RTF EPUB TEX RST TXT";
    public override string? Tool => "pandoc";
    public override string? WingetId => "JohnMacFarlane.Pandoc";

    public override bool CanHandle(string e, Preset p) => p.Mode == "" && Is(e, In) && Is(p.Extension, Out) && e != p.Extension;

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, p.Extension);
        var a = new List<string> { input, "-o", dest };
        if (p.Extension == ".txt") a.AddRange(new[] { "-t", "plain" });
        if (Is(p.Extension, ".html", ".tex", ".epub")) a.Add("-s");
        Proc.Run(Exe, a, Path.GetDirectoryName(input));
        return new[] { dest };
    }
}

public sealed class EbookEngine : EngineBase
{
    public override string Id => "ebook";
    public override string Name => "E-books (built-in)";
    public override string Category => "Ebook";
    public override string Description => "EPUB and FB2 to text and HTML, and comic books (CBZ) to PDF. Further steps (e.g. EPUB to PDF) chain through other engines.";
    public override string Formats => "EPUB FB2 → TXT HTML · CBZ → PDF";

    public override bool CanHandle(string e, Preset p) =>
        p.Mode == "" && ((Is(e, ".epub", ".fb2") && Is(p.Extension, ".txt", ".html")) || (e == ".cbz" && p.Extension == ".pdf"));

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var e = Util.Ext(input);
        var dest = Util.Out(input, outDir, p.Extension);
        if (e == ".cbz") return new[] { Comic(input, dest) };

        var blocks = e == ".epub" ? Epub(input) : Fb2(input);
        Util.WriteText(dest, p.Extension == ".txt" ? DocumentEngine.ToText(blocks) : DocumentEngine.Page(Path.GetFileNameWithoutExtension(input), DocumentEngine.ToHtmlBody(blocks)));
        return new[] { dest };
    }

    private static List<Block> Epub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string Read(ZipArchiveEntry en) { using var s = new StreamReader(en.Open(), Encoding.UTF8); return s.ReadToEnd(); }

        var container = XDocument.Parse(Read(zip.GetEntry("META-INF/container.xml") ?? throw new InvalidDataException("Not an EPUB")));
        var opfPath = container.Descendants().First(x => x.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        var opf = XDocument.Parse(Read(zip.GetEntry(opfPath)!));
        var dir = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        var manifest = opf.Descendants().Where(x => x.Name.LocalName == "item").ToDictionary(x => x.Attribute("id")!.Value, x => x.Attribute("href")!.Value);

        var res = new List<Block>();
        foreach (var itemref in opf.Descendants().Where(x => x.Name.LocalName == "itemref"))
        {
            if (!manifest.TryGetValue(itemref.Attribute("idref")!.Value, out var href)) continue;
            var entry = zip.GetEntry(Uri.UnescapeDataString(dir + href));
            if (entry is not null) res.AddRange(DocumentEngine.ParseHtml(Read(entry)));
        }
        return res;
    }

    private static List<Block> Fb2(string path)
    {
        var doc = XDocument.Load(path);
        var res = new List<Block>();
        foreach (var el in doc.Descendants().Where(x => x.Name.LocalName is "title" or "p"))
        {
            var text = string.Concat(el.DescendantNodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (text.Length == 0) continue;
            res.Add(new Block(el.Name.LocalName == "title" ? "h2" : "p", text));
        }
        return res;
    }

    private static string Comic(string path, string dest)
    {
        var tmp = Util.TempDir();
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var pages = new List<string>();
            foreach (var en in zip.Entries.Where(x => Util.Ext(x.Name) is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp").OrderBy(x => x.FullName, new NaturalComparer()))
            {
                var f = Path.Combine(tmp, pages.Count.ToString("D5") + Util.Ext(en.Name));
                en.ExtractToFile(f);
                pages.Add(f);
            }
            if (pages.Count == 0) throw new InvalidDataException("No pictures in this comic");
            return PdfEngine.ImagesToPdf(pages, dest);
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
    }

    private sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? a, string? b) => CompareNatural(a ?? "", b ?? "");
        private static int CompareNatural(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var c = long.Parse(a[si..i]).CompareTo(long.Parse(b[sj..j]));
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}

public sealed class EmailEngine : EngineBase
{
    public override string Id => "email";
    public override string Name => "E-mail (built-in)";
    public override string Category => "Document";
    public override string Description => "E-mails (EML and Outlook MSG) to HTML and text with sender, recipients and date; MSG to EML; the attachments of a message into a ZIP.";
    public override string Formats => "EML MSG → HTML TXT · MSG → EML · attachments → ZIP";

    public override bool CanHandle(string e, Preset p)
    {
        if (!Is(e, ".eml", ".msg")) return false;
        return p.Mode switch
        {
            "" => Is(p.Extension, ".html", ".txt") || (e == ".msg" && p.Extension == ".eml"),
            "attachments" => p.Extension == ".zip",
            _ => false,
        };
    }

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var m = Util.Ext(input) == ".msg" ? FromMsg(input) : MimeMessage.Load(input);
        var dest = Util.Out(input, outDir, p.Extension, p.Mode == "attachments" ? " (attachments)" : "");

        if (p.Mode == "attachments")
        {
            var parts = m.Attachments.OfType<MimePart>().ToList();
            if (parts.Count == 0) throw new InvalidOperationException("This message has no attachments");
            using var z = System.IO.Compression.ZipFile.Open(dest, System.IO.Compression.ZipArchiveMode.Create);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in parts)
            {
                var name = a.FileName ?? "attachment";
                for (var i = 2; !used.Add(name); i++) name = Path.GetFileNameWithoutExtension(a.FileName ?? "attachment") + $" ({i})" + Path.GetExtension(a.FileName ?? "");
                using var s = z.CreateEntry(name).Open();
                a.Content.DecodeTo(s);
            }
            return new[] { dest };
        }
        if (p.Extension == ".eml") { m.WriteTo(dest); return new[] { dest }; }

        var head = new[]
        {
            ("From", m.From.ToString()), ("To", m.To.ToString()), ("Cc", m.Cc.ToString()),
            ("Date", m.Date.ToString("f")), ("Subject", m.Subject ?? ""),
        }.Where(h => h.Item2.Length > 0).ToList();

        if (p.Extension == ".txt")
            Util.WriteText(dest, string.Join("\n", head.Select(h => $"{h.Item1}: {h.Item2}")) + "\n\n" + (m.TextBody ?? System.Text.RegularExpressions.Regex.Replace(m.HtmlBody ?? "", "<[^>]+>", "")));
        else
        {
            var table = "<table>" + string.Concat(head.Select(h => $"<tr><th align=\"left\">{h.Item1}</th><td>{WebUtility.HtmlEncode(h.Item2)}</td></tr>")) + "</table><hr>";
            var body = m.HtmlBody ?? "<pre>" + WebUtility.HtmlEncode(m.TextBody ?? "") + "</pre>";
            Util.WriteText(dest, DocumentEngine.Page(m.Subject ?? "E-mail", table + body));
        }
        return new[] { dest };
    }

    /// <summary>An Outlook .msg rebuilt as a standard MIME message.</summary>
    private static MimeMessage FromMsg(string path)
    {
        using var msg = new MsgReader.Outlook.Storage.Message(path);
        var m = new MimeMessage { Subject = msg.Subject ?? "" };
        if (msg.SentOn is { } sent) m.Date = sent;
        if (!string.IsNullOrWhiteSpace(msg.Sender?.Email) || !string.IsNullOrWhiteSpace(msg.Sender?.DisplayName))
            m.From.Add(new MailboxAddress(msg.Sender!.DisplayName ?? "", Mail(msg.Sender.Email)));
        foreach (var r in msg.Recipients ?? new())
        {
            var a = new MailboxAddress(r.DisplayName ?? "", Mail(r.Email));
            if (r.Type == MsgReader.Outlook.RecipientType.Cc) m.Cc.Add(a);
            else if (r.Type == MsgReader.Outlook.RecipientType.Bcc) m.Bcc.Add(a);
            else m.To.Add(a);
        }
        var body = new BodyBuilder { TextBody = msg.BodyText, HtmlBody = msg.BodyHtml };
        foreach (var o in msg.Attachments ?? new())
            if (o is MsgReader.Outlook.Storage.Attachment att && att.Data is { Length: > 0 })
                body.Attachments.Add(att.FileName ?? "attachment", att.Data);
        m.Body = body.ToMessageBody();
        return m;
    }

    private static string Mail(string? s) => string.IsNullOrWhiteSpace(s) || !s.Contains('@') ? "unknown@invalid" : s;
}

public sealed class HwpEngine : EngineBase
{
    public override string Id => "rhwp";
    public override string Name => "HWP (rhwp)";
    public override string Category => "Hwp";
    public override string Description => "Korean HWP and HWPX documents to PDF, Markdown and text, and HWP to HWPX. Uses the rhwp program that ships with Filee.";
    public override string Formats => "HWP HWPX → PDF MD TXT · HWP → HWPX";
    public override string? Tool => "rhwp";

    public override bool CanHandle(string e, Preset p) =>
        p.Mode == "" && Is(e, ".hwp", ".hwpx") && (Is(p.Extension, ".pdf", ".md", ".txt") || (e == ".hwp" && p.Extension == ".hwpx"));

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        var dest = Util.Out(input, outDir, p.Extension);
        if (p.Extension == ".hwpx")
        {
            Proc.Run(Exe, new[] { "export-hwpx", input, dest });
            return new[] { dest };
        }

        var tmp = Util.TempDir();
        try
        {
            var cmd = p.Extension switch { ".pdf" => "export-pdf", ".md" => "export-markdown", _ => "export-text" };
            var args = new List<string> { cmd, input, "-o", p.Extension == ".pdf" ? Path.Combine(tmp, "out.pdf") : tmp };
            if (p.Extension == ".pdf" && File.Exists(@"C:\Windows\Fonts\malgun.ttf"))   // Batang/Gulim are rarely installed
                args.AddRange(new[] { "--fallback-serif", "Malgun Gothic", "--fallback-mono", "Malgun Gothic" });
            Proc.Run(Exe, args);
            var made = Directory.GetFiles(tmp, "*" + p.Extension, SearchOption.AllDirectories);
            if (made.Length == 0) throw new InvalidOperationException("rhwp produced no file");
            File.Move(made[0], dest);
            return new[] { dest };
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
    }
}
