using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Mochi.Services;

public enum PrivacyKind { Aadhaar, Pan, Phone, Email, Card, Birth, Bank, Id, Number, Text, Face, Code, Area, Manual }

/// <summary>One thing Privacy Guard wants to cover: where it is (picture pixels), what it is, and whether it will be covered.</summary>
public sealed class Finding
{
    public PrivacyKind Kind;
    public string Label = "";
    public SKRect Rect;
    public bool On = true;
    /// <summary>False when Mochi is only guessing (a number that fails its checksum, a name line): it is still covered unless the user unticks it.</summary>
    public bool Sure = true;
}

/// <summary>
/// Privacy Guard: finds what should not be shared in a picture or a scanned page (ID numbers, phone numbers, faces, QR codes, names and
/// addresses) and covers it for good. Everything runs on this PC. No detector is perfect, so the design is: look several ways,
/// cover when in doubt, look again at the result, and let the user see and fix it.
/// </summary>
public static class Privacy
{
    private sealed record Word(string Text, SKRect R);
    private sealed class Line { public List<Word> Words = new(); public string Text = ""; public List<(int S, int E)> Spans = new(); public SKRect Box; }

    public sealed record Scan(List<Finding> Findings, List<string> Notes);

    // ================================================================ checksums

    private static readonly int[,] VerD =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 }, { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 }, { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 },
        { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 }, { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 }, { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 }, { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };
    private static readonly int[,] VerP =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 }, { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 },
        { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 }, { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    /// <summary>The check digit that ends every real Aadhaar number (and VID).</summary>
    public static bool Verhoeff(string digits)
    {
        var c = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (d is < 0 or > 9) return false;
            c = VerD[c, VerP[i % 8, d]];
        }
        return c == 0;
    }

    public static bool Luhn(string digits)
    {
        var sum = 0; var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (d is < 0 or > 9) return false;
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d; alt = !alt;
        }
        return sum % 10 == 0;
    }

    // ================================================================ reading the text (with boxes)

    /// <summary>Every text recogniser installed in Windows, at the normal size and (for small pictures) twice as big, so thin print is not missed.</summary>
    private static List<Line> ReadLines(SKBitmap bmp)
    {
        var engines = new List<Windows.Media.Ocr.OcrEngine>();
        foreach (var lang in Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages)
            if (Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang) is { } e) engines.Add(e);
        if (engines.Count == 0 && Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is { } u) engines.Add(u);
        if (engines.Count == 0) throw new NotSupportedException("Windows has no text-recognition language installed. Add one in Settings → Time & language → Language.");

        var max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        var scales = new List<double>();
        var big = Math.Max(bmp.Width, bmp.Height);
        scales.Add(big > max ? max / (double)big : 1.0);
        if (big * 2 <= max && big < 1800) scales.Add(2.0);
        if (big * 3 <= max && big < 1000) scales.Add(3.0);   // small pictures: thin print needs to be bigger still

        var lines = new List<Line>();
        foreach (var scale in scales)
        {
            var w = Math.Max(1, (int)(bmp.Width * scale)); var h = Math.Max(1, (int)(bmp.Height * scale));
            using var flat = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var c = new SKCanvas(flat))
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
            {
                c.Clear(SKColors.White);
                c.DrawBitmap(bmp, new SKRect(0, 0, w, h), paint);
            }
            var bytes = new byte[w * h * 4];
            Marshal.Copy(flat.GetPixels(), bytes, 0, bytes.Length);
            using var sb = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes),
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);
            foreach (var engine in engines)
            {
                Windows.Media.Ocr.OcrResult res;
                try { res = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult(); } catch { continue; }
                foreach (var l in res.Lines)
                {
                    var line = new Line();
                    var sbd = new StringBuilder();
                    foreach (var wd in l.Words)
                    {
                        var r = wd.BoundingRect;
                        var rect = Unskew(r, res.TextAngle, w, h, scale);
                        if (sbd.Length > 0) sbd.Append(' ');
                        line.Spans.Add((sbd.Length, sbd.Length + wd.Text.Length));
                        sbd.Append(wd.Text);
                        line.Words.Add(new Word(wd.Text, rect));
                    }
                    if (line.Words.Count == 0) continue;
                    line.Text = sbd.ToString();
                    line.Box = Union(line.Words.Select(x => x.R));
                    lines.Add(line);
                }
            }
        }
        return lines;
    }

    /// <summary>
    /// Windows reports word boxes on a straightened copy of a tilted picture (TextAngle degrees about the middle). Turning the corners back
    /// puts each box on the real words, so a photo taken at a slant is covered where the text really is.
    /// </summary>
    private static SKRect Unskew(Windows.Foundation.Rect r, double? angle, int w, int h, double scale)
    {
        var xs = new[] { r.X, r.X + r.Width }; var ys = new[] { r.Y, r.Y + r.Height };
        var a = (angle ?? 0) * Math.PI / 180;
        if (Math.Abs(a) < 0.0008) return new SKRect((float)(r.X / scale), (float)(r.Y / scale), (float)((r.X + r.Width) / scale), (float)((r.Y + r.Height) / scale));
        double cx = w / 2.0, cy = h / 2.0, cos = Math.Cos(a), sin = Math.Sin(a);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var x in xs)
            foreach (var y in ys)
            {
                var dx = x - cx; var dy = y - cy;
                var nx = cx + dx * cos - dy * sin; var ny = cy + dx * sin + dy * cos;
                minX = Math.Min(minX, nx); maxX = Math.Max(maxX, nx); minY = Math.Min(minY, ny); maxY = Math.Max(maxY, ny);
            }
        return new SKRect((float)(minX / scale), (float)(minY / scale), (float)(maxX / scale), (float)(maxY / scale));
    }

    private static SKRect Union(IEnumerable<SKRect> rs)
    {
        var any = false; var u = SKRect.Empty;
        foreach (var r in rs) { u = any ? SKRect.Union(u, r) : r; any = true; }
        return u;
    }

    private static SKRect Pad(SKRect r, float x, float y) { r.Inflate(x, y); return r; }

    private static float Overlap(SKRect a, SKRect b)
    {
        var i = SKRect.Intersect(a, b);
        if (i.Width <= 0 || i.Height <= 0) return 0;
        return i.Width * i.Height / Math.Max(1f, Math.Min(a.Width * a.Height, b.Width * b.Height));
    }

    // ================================================================ what to look for in the text

    /// <summary>A word read as digits, allowing for the letters a recogniser mixes up with them (O for 0, I for 1…); null when it is not a number.</summary>
    private static string? DigitForm(string t)
    {
        var s = t.Trim().Trim(',', ';', ':', '.', '|', '(', ')', '[', ']', '*');
        if (s.Length == 0) return null;
        var sb = new StringBuilder(); var real = 0;
        foreach (var ch in s)
        {
            if (char.IsDigit(ch)) { sb.Append((char)('0' + (int)char.GetNumericValue(ch))); real++; }
            else if (ch == '-') continue;
            else
            {
                var m = ch switch { 'O' or 'o' or 'D' or 'Q' => '0', 'I' or 'l' or '|' or 'i' => '1', 'S' or 's' => '5', 'B' => '8', 'Z' => '2', 'G' => '6', _ => '?' };
                if (m == '?') return null;
                sb.Append(m);
            }
        }
        var r = sb.ToString();
        return real >= Math.Max(1, r.Length / 2) ? r : null;
    }

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+\s?@\s?[A-Za-z0-9\-]+(?:\s?\.\s?[A-Za-z0-9\-]+)*\s?\.\s?[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Dob = new(@"(?<![\d/])(0?[1-9]|[12]\d|3[01])\s?[/\-.]\s?(0?[1-9]|1[0-2])\s?[/\-.]\s?(19|20)\d{2}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex BirthWord = new(@"(?i)(?:yob|year\s+of\s+birth|d\.?o\.?b\.?|date\s+of\s+birth|जन्म[^\d]{0,14})\W{0,4}(\d{4}|\d{1,2}\s?[/\-.]\s?\d{1,2}\s?[/\-.]\s?\d{2,4})", RegexOptions.Compiled);
    private static readonly Regex GstIn = new(@"^\d{2}[A-Z]{5}\d{4}[A-Z][1-9A-Z]Z[0-9A-Z]$", RegexOptions.Compiled);
    private static readonly Regex Ifsc = new(@"^[A-Z]{4}[0O][A-Z0-9]{6}$", RegexOptions.Compiled);
    private static readonly Regex Passport = new(@"^[A-PR-WY][1-9]\d{6}[0-9]$", RegexOptions.Compiled);
    private static readonly Regex Voter = new(@"^[A-Z]{3}\d{7}$", RegexOptions.Compiled);
    private static readonly Regex NameLine = new(@"(?i)^\W*(name|father'?s?\s*name|mother'?s?\s*name|husband'?s?\s*name|नाम|पिता|पति)\b", RegexOptions.Compiled);
    private static readonly Regex AddressLine = new(@"(?i)^\W*(address|s\s*/\s*o|d\s*/\s*o|w\s*/\s*o|c\s*/\s*o|पता)\b", RegexOptions.Compiled);

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>"AB1DE2345F" read as a PAN even when the recogniser swapped 0/O, 1/I, 5/S or 8/B.</summary>
    private static bool LooksLikePan(string raw, out bool exact)
    {
        exact = false;
        var s = Letters(raw).ToUpperInvariant();
        if (s.Length != 10) return false;
        exact = Regex.IsMatch(s, "^[A-Z]{5}[0-9]{4}[A-Z]$");
        if (exact) return true;
        var fixedChars = s.Select((ch, i) =>
            i is < 5 or 9 ? ch switch { '0' => 'O', '1' => 'I', '5' => 'S', '8' => 'B', '2' => 'Z', '6' => 'G', _ => ch }
                          : ch switch { 'O' => '0', 'I' or 'L' => '1', 'S' => '5', 'B' => '8', 'Z' => '2', 'G' => '6', _ => ch }).ToArray();
        return Regex.IsMatch(new string(fixedChars), "^[A-Z]{5}[0-9]{4}[A-Z]$") && s.Count(char.IsDigit) >= 2;
    }

    /// <summary>Four letters, a zero, six letters or digits, allowing for 0/O, 1/I and 5/S mixed up in the first five places.</summary>
    private static bool LooksLikeIfsc(string s)
    {
        if (s.Length != 11) return false;
        for (var i = 0; i < 4; i++) if (!(char.IsLetter(s[i]) || s[i] is '0' or '1' or '5' or '8')) return false;
        return s[4] is '0' or 'O' && s.Skip(5).All(char.IsLetterOrDigit) && s.Take(4).Count(char.IsLetter) >= 3;
    }

    /// <summary>Joins pieces of one printed line that the recogniser returned apart (a number in its own column, a word after a gap).</summary>
    private static List<Line> MergeBands(List<Line> lines)
    {
        var list = lines.OrderBy(l => l.Box.Left).ToList();
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < list.Count && !merged; i++)
                for (var j = 0; j < list.Count && !merged; j++)
                {
                    if (i == j) continue;
                    var a = list[i]; var b = list[j];
                    var overlap = Math.Min(a.Box.Bottom, b.Box.Bottom) - Math.Max(a.Box.Top, b.Box.Top);
                    var gap = b.Box.Left - a.Box.Right;
                    if (overlap < 0.6f * Math.Min(a.Box.Height, b.Box.Height) || gap < -2 || gap > 2.5f * Math.Max(a.Box.Height, b.Box.Height)) continue;
                    var m = new Line();
                    foreach (var w in a.Words.Concat(b.Words)) m.Words.Add(w);
                    var sb = new StringBuilder();
                    foreach (var w in m.Words) { if (sb.Length > 0) sb.Append(' '); m.Spans.Add((sb.Length, sb.Length + w.Text.Length)); sb.Append(w.Text); }
                    m.Text = sb.ToString(); m.Box = Union(m.Words.Select(w => w.R));
                    list[Math.Min(i, j)] = m; list.RemoveAt(Math.Max(i, j));
                    merged = true;
                }
        }
        return list;
    }

    private static readonly (string[] Words, PrivacyKind Kind, string Label)[] FieldLabels =
    {
        (new[] { "email", "emailid", "mail", "emial" }, PrivacyKind.Email, "Email (after its label)"),
        (new[] { "mobile", "mob", "phone", "contact", "tel", "telephone", "whatsapp" }, PrivacyKind.Phone, "Phone (after its label)"),
        (new[] { "ac", "acct", "account", "accno" }, PrivacyKind.Bank, "Account number (after its label)"),
        (new[] { "aadhaar", "aadhar", "adhar", "uid", "vid" }, PrivacyKind.Aadhaar, "Aadhaar (after its label)"),
        (new[] { "pan" }, PrivacyKind.Pan, "PAN (after its label)"),
        (new[] { "card", "cardno" }, PrivacyKind.Card, "Card number (after its label)"),
        (new[] { "dob", "birth", "yob" }, PrivacyKind.Birth, "Date of birth (after its label)"),
        (new[] { "ifsc", "ifs" }, PrivacyKind.Bank, "IFSC (after its label)"),
        (new[] { "passport", "epic", "voter" }, PrivacyKind.Id, "ID number (after its label)"),
    };
    private static readonly HashSet<string> LabelFiller = new() { "no", "num", "number", "id", "code", "of", "date", "year", "card", "mail", "to" };

    private static string Norm(string w) => new string(w.ToLowerInvariant().Where(char.IsLetter).ToArray());

    private static bool Near(string a, string b)
    {
        if (a == b) return true;
        if (a.Length < 4 || b.Length < 4 || Math.Abs(a.Length - b.Length) > 1) return false;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length] <= 1;
    }

    /// <summary>
    /// A value that could not be read (small print, an underscore, a smudge) still sits after its label. So the rest of the line after
    /// "Email", "Mobile", "A/c No", "PAN", "DOB" and the like is covered as a guess, whatever the recogniser made of it.
    /// </summary>
    private static void FindByLabel(Line l, List<Finding> found, Action<PrivacyKind, string, SKRect, bool> add)
    {
        for (var i = 0; i < l.Words.Count; i++)
        {
            var n = Norm(l.Words[i].Text);
            if (n.Length < 2) continue;
            foreach (var (words, kind, label) in FieldLabels)
            {
                if (!words.Any(w => Near(n, w))) continue;
                var k = kind; var text = label;
                if (n == "account" && i > 0 && Norm(l.Words[i - 1].Text) == "permanent") { k = PrivacyKind.Pan; text = "PAN (after its label)"; }
                else if (n == "pan" && i + 1 < l.Words.Count && !(l.Words[i].Text.Trim(':', '.', ' ').Length <= 4)) continue;
                var j = i + 1;
                while (j < l.Words.Count && (LabelFiller.Contains(Norm(l.Words[j].Text)) || Norm(l.Words[j].Text).Length == 0 && l.Words[j].Text.Length <= 2) && j < i + 4) j++;
                if (j >= l.Words.Count) break;
                var rest = l.Words.Skip(j).ToList();
                var r = Union(rest.Select(w => w.R));
                if (r.Width < 10 || r.Height < 4) break;
                if (found.Any(f => Overlap(f.Rect, r) > 0.7f && f.Rect.Width >= r.Width * 0.7f)) break;   // already covered by something read exactly
                add(k, text, r, false);
                break;
            }
        }
    }

    private static void FindText(List<Line> lines, List<Finding> found, List<string> notes)
    {
        void Add(PrivacyKind k, string label, SKRect r, bool sure = true)
        {
            r = Pad(r, 4, 3);
            // already covered by a finding that is at least as big
            foreach (var f in found) if (f.Kind == k && Overlap(r, f.Rect) > 0.6f && f.Rect.Width * f.Rect.Height >= r.Width * r.Height * 0.8f) return;
            found.RemoveAll(f => f.Kind == k && Overlap(f.Rect, r) > 0.8f && f.Rect.Width * f.Rect.Height < r.Width * r.Height);   // a bigger box of the same kind replaces a smaller one
            found.Add(new Finding { Kind = k, Label = label, Rect = r, Sure = sure });
        }

        SKRect Span(Line l, int a, int b)
        {
            var rs = new List<SKRect>();
            for (var i = 0; i < l.Words.Count; i++) if (l.Spans[i].S < b && l.Spans[i].E > a) rs.Add(l.Words[i].R);
            return rs.Count == 0 ? l.Box : Union(rs);
        }

        var ordered = lines.Concat(MergeBands(lines)).OrderBy(l => l.Box.Top).ToList();
        for (var li = 0; li < ordered.Count; li++)
        {
            var l = ordered[li];

            // numbers: runs of digit-like words (4-4-4 Aadhaar, 10-digit phones, long account numbers, cards)
            var forms = l.Words.Select(w => DigitForm(w.Text)).ToList();
            for (var i = 0; i < forms.Count; i++)
            {
                if (forms[i] is null) continue;
                (int Len, PrivacyKind K, string Label, bool Sure)? best = null; var cat = "";
                for (var j = i; j < forms.Count && j < i + 5 && forms[j] is not null; j++)
                {
                    cat += forms[j];
                    var n = cat.Length;
                    (PrivacyKind, string, bool)? hit = null;
                    if (n == 12 && Verhoeff(cat) && cat[0] is >= '2' and <= '9') hit = (PrivacyKind.Aadhaar, "Aadhaar number", true);
                    else if (n == 16 && Verhoeff(cat) && j > i) hit = (PrivacyKind.Aadhaar, "Aadhaar virtual ID", true);
                    else if (n is >= 13 and <= 19 && Luhn(cat) && j > i) hit = (PrivacyKind.Card, "Card number", true);
                    else if (n == 10 && cat[0] is >= '6' and <= '9') hit = (PrivacyKind.Phone, "Phone number", true);
                    else if (n == 12 && cat.StartsWith("91") && cat[2] is >= '6' and <= '9') hit = (PrivacyKind.Phone, "Phone number", true);
                    else if (n == 11 && cat[0] == '0' && cat[1] is >= '6' and <= '9') hit = (PrivacyKind.Phone, "Phone number", true);
                    else if (n == 12 && j > i) hit = (PrivacyKind.Aadhaar, "Possible Aadhaar number", false);
                    else if (n == 16 && j > i) hit = (PrivacyKind.Card, "Possible card or ID number", false);
                    else if (n >= 9 && n <= 18) hit = (PrivacyKind.Bank, "Long number (account or ID)", false);
                    if (hit is { } h) best = (j, h.Item1, h.Item2, h.Item3);
                    if (n > 19) break;
                }
                if (best is { } b)
                {
                    Add(b.K, b.Label, Union(l.Words.Skip(i).Take(b.Len - i + 1).Select(w => w.R)), b.Sure);
                    i = b.Len;
                }
            }

            // numbers glued to a label ("No:1234 5678 9012"): look inside the whole line
            foreach (Match m in Regex.Matches(l.Text, @"(?<![\d])(?:\d[ \-]?){9,19}(?!\d)"))
            {
                var d = new string(m.Value.Where(char.IsDigit).ToArray());
                var (k, label, sure) = d.Length == 12 && Verhoeff(d) ? (PrivacyKind.Aadhaar, "Aadhaar number", true)
                    : d.Length == 10 && d[0] is >= '6' and <= '9' ? (PrivacyKind.Phone, "Phone number", true)
                    : d.Length == 12 ? (PrivacyKind.Aadhaar, "Possible Aadhaar number", false)
                    : (PrivacyKind.Bank, "Long number (account or ID)", false);
                Add(k, label, Span(l, m.Index, m.Index + m.Length), sure);
            }

            // letters and digits: PAN, IFSC, passport, voter ID, GST number (a recogniser often splits them: "SNWEAI 913E")
            for (var i = 0; i < l.Words.Count; i++)
            {
                for (var len = 1; len <= 3 && i + len <= l.Words.Count; len++)
                {
                    var words = l.Words.Skip(i).Take(len).ToList();
                    var raw = Letters(string.Concat(words.Select(w => w.Text))).ToUpperInvariant();
                    PrivacyKind? kind = null; string label = ""; var sure = true;
                    if (LooksLikePan(string.Concat(words.Select(w => w.Text)), out var exact)) { kind = PrivacyKind.Pan; label = "PAN number"; sure = exact && len == 1; }
                    else if (raw.Length == 11 && LooksLikeIfsc(raw)) { kind = PrivacyKind.Bank; label = "Bank IFSC code"; }
                    else if (raw.Length == 15 && GstIn.IsMatch(raw)) { kind = PrivacyKind.Id; label = "GST number"; }
                    else if (len == 1 && Passport.IsMatch(raw)) { kind = PrivacyKind.Id; label = "Passport number"; sure = false; }
                    else if (len == 1 && Voter.IsMatch(raw)) { kind = PrivacyKind.Id; label = "Voter ID number"; sure = false; }
                    if (kind is null) continue;
                    Add(kind.Value, label, Union(words.Select(w => w.R)), sure);
                    i += len - 1;
                    break;
                }
            }

            foreach (Match m in Email.Matches(l.Text)) Add(PrivacyKind.Email, "Email address", Span(l, m.Index, m.Index + m.Length));
            FindByLabel(l, found, (k, t, r, sure) => Add(k, t, r, sure));
            foreach (Match m in Dob.Matches(l.Text)) Add(PrivacyKind.Birth, "Date of birth", Span(l, m.Index, m.Index + m.Length));
            foreach (Match m in BirthWord.Matches(l.Text)) Add(PrivacyKind.Birth, "Date of birth", Span(l, m.Groups[1].Index, m.Groups[1].Index + m.Groups[1].Length));

            // a name or an address: the whole line (and the next two for an address) is covered, because the words that follow are the personal part
            if (NameLine.IsMatch(l.Text)) Add(PrivacyKind.Text, "Name", l.Box, false);
            else if (AddressLine.IsMatch(l.Text))
            {
                Add(PrivacyKind.Text, "Address", l.Box, false);
                for (var k = 1; k <= 2 && li + k < ordered.Count; k++)
                    if (ordered[li + k].Box.Top - ordered[li + k - 1].Box.Bottom < l.Box.Height * 1.2f) Add(PrivacyKind.Text, "Address", ordered[li + k].Box, false);
            }
        }

        // a document whose number could not be read is still covered where the number always is
        var all = string.Join(" ", ordered.Select(l => l.Text)).ToLowerInvariant();
        if (!found.Any(f => f.Kind == PrivacyKind.Pan) && (all.Contains("permanent account number") || all.Contains("income tax department") || all.Contains("आयकर")))
        {
            var label = ordered.FirstOrDefault(l => l.Text.Contains("Permanent", StringComparison.OrdinalIgnoreCase) || l.Text.Contains("Account", StringComparison.OrdinalIgnoreCase));
            if (label is not null)
            {
                var next = ordered.Where(l => l.Box.Top >= label.Box.Bottom - 2).OrderBy(l => l.Box.Top).FirstOrDefault();
                if (next is not null) { Add(PrivacyKind.Pan, "PAN number (line under the label)", next.Box, false); notes.Add("This looks like a PAN card but the number could not be read exactly. The line under “Permanent Account Number” is covered to be safe."); }
            }
        }
        if (all.Contains("aadhaar") || all.Contains("uidai") || all.Contains("आधार") || all.Contains("unique identification"))
        {
            notes.Add("This looks like an Aadhaar card. Faces, the number and the QR code are covered. Check the QR code area on the right and cover any part Mochi missed.");
            if (!found.Any(f => f.Kind == PrivacyKind.Aadhaar)) notes.Add("The Aadhaar number could not be read. Draw a box over it (hold the left mouse button and drag).");
        }
    }

    // ================================================================ faces and codes

    private static (byte[] Px, int W, int H) Straight(SKBitmap bmp)
    {
        using var flat = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var c = new SKCanvas(flat)) { c.Clear(SKColors.White); c.DrawBitmap(bmp, 0, 0); }
        var bytes = new byte[flat.Width * flat.Height * 4];
        Marshal.Copy(flat.GetPixels(), bytes, 0, bytes.Length);
        return (bytes, flat.Width, flat.Height);
    }

    /// <summary>
    /// Faces, found with YuNet at several sizes: the whole picture, then overlapping parts of it (2×2, 3×3, 4×4), so small faces such as the
    /// photo on an ID card are seen as well as a big portrait. Parts are checked a second time at a lower bar than the whole picture.
    /// </summary>
    private static List<SKRect> FindFaces(SKBitmap bmp)
    {
        var (px, w, h) = Straight(bmp);
        var cand = new List<SKRect>();
        void Run(int x0, int y0, int tw, int th)
        {
            byte[] part;
            if (tw == w && th == h) part = px;
            else
            {
                part = new byte[tw * th * 4];
                for (var y = 0; y < th; y++) Buffer.BlockCopy(px, ((y0 + y) * w + x0) * 4, part, y * tw * 4, tw * 4);
            }
            foreach (var (_, r) in AiTools.FindFacesYunet(part, tw, th, 0.5f))
                cand.Add(new SKRect(r.Left + x0, r.Top + y0, r.Right + x0, r.Bottom + y0));
        }
        Run(0, 0, w, h);
        foreach (var grid in new[] { 2, 3, 4 })
        {
            var tw = (int)(w / (grid - (grid - 1) * 0.3)); var th = (int)(h / (grid - (grid - 1) * 0.3));
            if (Math.Min(tw, th) < 120) continue;
            for (var gy = 0; gy < grid; gy++)
                for (var gx = 0; gx < grid; gx++)
                    Run(Math.Min(w - tw, (int)(gx * tw * 0.7)), Math.Min(h - th, (int)(gy * th * 0.7)), tw, th);
        }
        var keep = new List<SKRect>();
        foreach (var r in cand.OrderByDescending(c => c.Width * c.Height))
        {
            if (r.Width < 8 || r.Height < 8 || r.Width > w * 0.9f || r.Height > h * 0.9f) continue;
            if (keep.Any(k => Overlap(k, r) > 0.45f)) continue;
            keep.Add(r);
        }
        return keep;
    }

    private static List<Finding> FindCodes(SKBitmap bmp)
    {
        var res = new List<Finding>();
        try
        {
            using var flat = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(flat)) { c.Clear(SKColors.White); c.DrawBitmap(bmp, 0, 0); }
            var reader = new ZXing.SkiaSharp.BarcodeReader { AutoRotate = true, Options = { TryHarder = true, TryInverted = true } };
            foreach (var r in reader.DecodeMultiple(flat) ?? Array.Empty<ZXing.Result>())
            {
                var pts = r.ResultPoints?.Where(p => p is not null).ToList();
                if (pts is null || pts.Count == 0) continue;
                var box = new SKRect(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
                var side = Math.Max(box.Width, box.Height);
                if (pts.Count <= 2) box.Inflate(side * 0.08f, Math.Max(side * 0.18f, 14));
                else box.Inflate(side * 0.2f, side * 0.2f);
                res.Add(new Finding { Kind = PrivacyKind.Code, Label = r.BarcodeFormat == ZXing.BarcodeFormat.QR_CODE ? "QR code" : "Bar code", Rect = box });
            }
        }
        catch { /* a picture ZXing cannot read has no code it could read either */ }
        return res;
    }

    /// <summary>What the recogniser read, line by line (for testing).</summary>
    public static List<string> DebugText(SKBitmap bmp) => ReadLines(bmp).Select(l => $"[{l.Box.Left:0},{l.Box.Top:0}] {l.Text}").ToList();

    // ================================================================ the whole scan

    public static Scan Detect(SKBitmap bmp)
    {
        var found = new List<Finding>(); var notes = new List<string>();
        List<Line> lines;
        try { lines = ReadLines(bmp); }
        catch (Exception ex) { lines = new(); notes.Add("Text could not be read: " + ex.Message + " Only faces and codes were looked for."); }
        FindText(lines, found, notes);
        if (lines.Count > 0)
        {
            var heights = lines.SelectMany(l => l.Words).Select(w => w.R.Height).OrderBy(x => x).ToList();
            if (heights[heights.Count / 2] < 11) notes.Add("The writing in this picture is very small or blurry, so Mochi may have missed some of it. Zoom in, look carefully, and drag a box over anything that should be covered.");
        }
        else if (!notes.Any(n => n.StartsWith("Text could not"))) notes.Add("Mochi could not read any writing in this picture. If it has words or numbers, drag a box over them yourself.");
        foreach (var c in FindCodes(bmp)) if (!found.Any(f => f.Kind == PrivacyKind.Code && Overlap(f.Rect, c.Rect) > 0.5f)) found.Add(c);
        try
        {
            foreach (var r in FindFaces(bmp))
            {
                var box = r; box.Inflate(r.Width * 0.3f, r.Height * 0.4f);
                found.Add(new Finding { Kind = PrivacyKind.Face, Label = "Face", Rect = box, Sure = true });
            }
        }
        catch (Exception ex) { notes.Add("Faces could not be checked (" + ex.Message + "). Look at the picture and cover any face yourself."); }
        // text read inside a face box is not a second finding
        found.RemoveAll(f => f.Kind is not (PrivacyKind.Face or PrivacyKind.Code) && found.Any(o => o.Kind == PrivacyKind.Face && Overlap(f.Rect, o.Rect) > 0.7f));
        return new Scan(found.OrderBy(f => f.Rect.Top).ThenBy(f => f.Rect.Left).ToList(), notes);
    }

    /// <summary>Covers every ticked finding with solid black (no blur to undo, nothing underneath).</summary>
    public static SKBitmap Redact(SKBitmap src, IEnumerable<Finding> findings)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var c = new SKCanvas(dst);
        c.Clear(SKColors.White);
        c.DrawBitmap(src, 0, 0);
        using var black = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        foreach (var f in findings.Where(f => f.On))
        {
            var r = f.Rect; r.Inflate(2, 2);
            c.DrawRect(r, black);
        }
        return dst;
    }

    /// <summary>
    /// Covers the ticked findings, then reads the result again the way a stranger would. Anything that is still readable is covered too
    /// (up to two more rounds). Returns the safe picture and how many extra things the second look caught.
    /// </summary>
    public static (SKBitmap Safe, int Extra, List<Finding> More) CoverAndCheck(SKBitmap src, List<Finding> findings)
    {
        var all = findings.ToList();
        var extra = 0; var more = new List<Finding>();
        var safe = Redact(src, all);
        for (var round = 0; round < 2; round++)
        {
            var again = new List<Finding>(); var n = new List<string>();
            try { FindText(ReadLines(safe), again, n); } catch { break; }
            foreach (var c in FindCodes(safe)) again.Add(c);
            again.RemoveAll(a => a.Kind is PrivacyKind.Text or PrivacyKind.Area || (!a.Sure && a.Kind == PrivacyKind.Bank));   // guesses are not worth a second round
            again.RemoveAll(a => all.Any(o => o.On && Overlap(a.Rect, o.Rect) > 0.9f));
            if (again.Count == 0) break;
            extra += again.Count; more.AddRange(again); all.AddRange(again);
            safe.Dispose();
            safe = Redact(src, all);
        }
        return (safe, extra, more);
    }

    // ================================================================ files

    /// <summary>True when the file carries hidden photo or document details (camera, date, location, author).</summary>
    public static bool HasHiddenInfo(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[(int)Math.Min(fs.Length, 262144)];
            var n = fs.Read(buf, 0, buf.Length);
            var s = Encoding.Latin1.GetString(buf, 0, n);
            return s.Contains("Exif\0\0") || s.Contains("<x:xmpmeta") || s.Contains("/Author") || s.Contains("/Creator") || s.Contains("eXIf");
        }
        catch { return false; }
    }

    /// <summary>The pages of a picture or a PDF, drawn flat on white (a PDF page becomes a picture: nothing hidden stays behind in it).</summary>
    public static List<SKBitmap> LoadPages(string path)
    {
        var pages = new List<SKBitmap>();
        if (Util.Ext(path) == ".pdf")
        {
            using var fs = File.OpenRead(path);
            foreach (var bmp in PDFtoImage.Conversion.ToImages(fs, options: new PDFtoImage.RenderOptions(Dpi: 170)))
                using (bmp) pages.Add(Flat(bmp));
            return pages;
        }
        var (px, w, h) = Matte.Load(path, 6000);
        using var raw = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(px, 0, raw.GetPixels(), px.Length);
        pages.Add(Flat(raw));
        return pages;
    }

    private static SKBitmap Flat(SKBitmap b)
    {
        var f = new SKBitmap(b.Width, b.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var c = new SKCanvas(f);
        c.Clear(SKColors.White);
        c.DrawBitmap(b, 0, 0);
        return f;
    }

    /// <summary>Saves the safe pages next to the original: one picture, or a flat PDF with no text layer and no details of its own.</summary>
    public static string Save(string source, IReadOnlyList<SKBitmap> safePages)
    {
        var pdf = Util.Ext(source) == ".pdf";
        if (pdf)
        {
            var dest = Util.Out(source, Path.GetDirectoryName(source)!, ".pdf", " (safe)");
            using var fs = File.Create(dest);
            using var doc = SKDocument.CreatePdf(fs);
            foreach (var p in safePages)
            {
                var wpt = p.Width * 72f / 170f; var hpt = p.Height * 72f / 170f;
                using var canvas = doc.BeginPage(wpt, hpt);
                using var img = SKImage.FromBitmap(p);
                canvas.DrawImage(img, new SKRect(0, 0, wpt, hpt));
                doc.EndPage();
            }
            doc.Close();
            return dest;
        }
        var ext = Util.Ext(source) is ".jpg" or ".jpeg" ? ".jpg" : ".png";
        var outPath = Util.Out(source, Path.GetDirectoryName(source)!, ext, " (safe)");
        AiTools.Save(safePages[0], outPath, 92);   // re-encoding the pixels leaves every hidden detail behind
        return outPath;
    }
}
