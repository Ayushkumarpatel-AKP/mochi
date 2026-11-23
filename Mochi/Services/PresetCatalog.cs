namespace Mochi.Services;

public static class PresetCatalog
{
    private static IReadOnlyList<Preset>? _all;

    /// <summary>Built-in presets followed by the user's own.</summary>
    public static IReadOnlyList<Preset> All => _all ??= Build();

    public static void Reload() => _all = null;

    private static List<Preset> Build()
    {
        var l = new List<Preset>();
        void Add(string id, string name, string cat, string ext, int q = 90, double scale = 1, bool gray = false, string mode = "") =>
            l.Add(new Preset(id, name, cat, ext, q, scale, gray, mode));

        // AI (need a connected AI: Settings → AI)
        Add("ai-summary", "Summarise (AI)", "Ai", "", mode: "ai:summary");
        Add("ai-ask", "Ask this file (AI)", "Ai", "", mode: "ai:ask");
        Add("ai-explain", "Explain image (AI)", "Ai", "", mode: "ai:explain");
        Add("ai-rename", "Smart rename (AI)", "Ai", "", mode: "ai:rename");
        Add("ai-command", "Do it by typing (AI)", "Ai", "", mode: "ai:command");

        // Image
        Add("png", "To PNG", "Image", ".png", 100);
        Add("jpg", "To JPG", "Image", ".jpg", 90);
        Add("webp", "To WEBP", "Image", ".webp", 90);
        Add("jpg-small", "Small JPG", "Image", ".jpg", 60);
        Add("half", "Half size", "Image", ".png", 100, 0.5);
        Add("gray", "Grayscale", "Image", ".png", 100, 1, true);
        Add("bmp", "To BMP", "Image", ".bmp");
        Add("gif", "To GIF", "Image", ".gif");
        Add("tiff", "To TIFF", "Image", ".tiff");
        Add("ico", "To ICO", "Image", ".ico");
        Add("avif", "To AVIF", "Image", ".avif", 70);
        Add("heic", "To HEIC", "Image", ".heic", 80);
        Add("img-pdf", "Image to PDF", "Pdf", ".pdf");
        Add("imgs-pdf", "Images into one PDF", "Pdf", ".pdf", mode: "merge");
        Add("ocr-txt", "Recognize text (OCR)", "Text", ".txt", mode: "ocr");
        Add("ocr-pdf", "Searchable PDF (OCR)", "Pdf", ".pdf", mode: "ocr");
        Add("relief", "3D relief (STL)", "Model3D", ".stl", mode: "relief");

        // PDF
        Add("pdf-png", "PDF to PNG pages", "Image", ".png", 100);
        Add("pdf-jpg", "PDF to JPG pages", "Image", ".jpg", 90);
        Add("pdf-txt", "PDF to text", "Text", ".txt");
        Add("pdf-merge", "Merge PDFs", "Pdf", ".pdf", mode: "merge");
        Add("pdf-split", "Split PDF pages", "Pdf", ".pdf", mode: "split");
        Add("pdf-small", "Smaller PDF", "Pdf", ".pdf", mode: "compress");
        Add("pdf-eps", "PDF to EPS", "Vector", ".eps");

        // Documents
        Add("doc-pdf", "To PDF", "Pdf", ".pdf");
        Add("docx", "To DOCX", "Document", ".docx");
        Add("odt", "To ODT", "Document", ".odt");
        Add("rtf", "To RTF", "Document", ".rtf");
        Add("html", "To HTML", "Document", ".html");
        Add("md", "To Markdown", "Text", ".md");
        Add("txt", "To text", "Text", ".txt");
        Add("epub", "To EPUB", "Ebook", ".epub");
        Add("tex", "To LaTeX", "Text", ".tex");
        Add("pptx", "To PPTX", "Presentation", ".pptx");

        // Spreadsheets and data
        Add("xlsx", "To XLSX", "Spreadsheet", ".xlsx");
        Add("csv", "To CSV", "Spreadsheet", ".csv");
        Add("tsv", "To TSV", "Spreadsheet", ".tsv");
        Add("json", "To JSON", "Data", ".json");
        Add("yaml", "To YAML", "Data", ".yaml");
        Add("toml", "To TOML", "Data", ".toml");
        Add("xml", "To XML", "Data", ".xml");
        Add("jsonl", "To JSON Lines", "Data", ".jsonl");

        // Subtitles
        Add("srt", "To SRT", "Subtitle", ".srt");
        Add("vtt", "To WebVTT", "Subtitle", ".vtt");
        Add("ass", "To ASS", "Subtitle", ".ass");
        Add("lrc", "To LRC", "Subtitle", ".lrc");

        // Archives
        Add("extract", "Extract here", "Archive", "", mode: "extract");
        Add("zip", "To ZIP", "Archive", ".zip", mode: "pack");
        Add("7z", "To 7Z", "Archive", ".7z", mode: "pack");
        Add("targz", "To TAR.GZ", "Archive", ".tar.gz", mode: "pack");

        // Audio and video
        Add("mp3", "To MP3", "Audio", ".mp3");
        Add("m4a", "To M4A", "Audio", ".m4a");
        Add("wav", "To WAV", "Audio", ".wav");
        Add("flac", "To FLAC", "Audio", ".flac");
        Add("ogg", "To OGG", "Audio", ".ogg");
        Add("mp4", "To MP4", "Video", ".mp4");
        Add("webm", "To WEBM", "Video", ".webm");
        Add("mkv", "To MKV", "Video", ".mkv");
        Add("mov", "To MOV", "Video", ".mov");
        Add("vid-gif", "Video to GIF", "Video", ".gif", mode: "gif");
        Add("vid-mp3", "Extract sound (MP3)", "Audio", ".mp3", mode: "audio");
        Add("vid-frame", "Video still (PNG)", "Image", ".png", mode: "frame");
        Add("vid-srt", "Extract subtitles (SRT)", "Subtitle", ".srt", mode: "subs");

        // E-mail, geo, fonts, 3D, CAD, HWP, vector
        Add("eml-html", "E-mail to HTML", "Document", ".html");
        Add("geojson", "To GeoJSON", "Geo", ".geojson");
        Add("kml", "To KML", "Geo", ".kml");
        Add("gpx", "To GPX", "Geo", ".gpx");
        Add("woff", "To WOFF", "Font", ".woff");
        Add("ttf", "To TTF", "Font", ".ttf");
        Add("stl", "To STL", "Model3D", ".stl");
        Add("obj", "To OBJ", "Model3D", ".obj");
        Add("ply", "To PLY", "Model3D", ".ply");
        Add("dxf", "To DXF", "Cad", ".dxf");
        Add("dwg", "To DWG", "Cad", ".dwg");
        Add("hwp-pdf", "HWP to PDF", "Hwp", ".pdf");
        Add("hwp-md", "HWP to Markdown", "Hwp", ".md");
        Add("hwpx", "To HWPX", "Hwp", ".hwpx");

        // Crop, pages per sheet, tools
        Add("crop-1-1", "Crop to 1:1", "Image", "", mode: "crop:1:1");
        Add("crop-16-9", "Crop to 16:9", "Image", "", mode: "crop:16:9");
        Add("crop-4-3", "Crop to 4:3", "Image", "", mode: "crop:4:3");
        Add("crop-9-16", "Crop to 9:16", "Image", "", mode: "crop:9:16");
        Add("crop-21-9", "Crop to 21:9", "Image", "", mode: "crop:21:9");
        Add("crop-auto", "Remove borders / black bars", "Image", "", mode: "crop:auto");
        Add("nup2", "2 pages per sheet", "Pdf", ".pdf", mode: "nup:2x1");
        Add("nup4", "4 pages per sheet", "Pdf", ".pdf", mode: "nup:2x2");
        Add("nup6", "6 pages per sheet", "Pdf", ".pdf", mode: "nup:3x2");
        Add("nup8", "8 pages per sheet", "Pdf", ".pdf", mode: "nup:4x2");
        Add("nup9", "9 pages per sheet", "Pdf", ".pdf", mode: "nup:3x3");
        Add("annotate", "Annotate picture…", "Image", ".png", mode: "annotate");
        Add("removebg", "Remove background…", "Image", ".png", mode: "removebg");
        Add("upscale2", "Enlarge 2× (sharper)", "Image", ".png", mode: "upscale:2");
        Add("upscale4", "Enlarge 4× (sharper)", "Image", ".png", mode: "upscale:4");
        Add("faceblur", "Blur faces", "Image", ".png", mode: "faceblur");
        Add("enhance", "Auto enhance", "Image", ".png", mode: "enhance");
        Add("erase", "Erase object…", "Image", ".png", mode: "erase");
        Add("privacy", "Privacy Guard (hide IDs, faces, numbers)…", "Image", ".png", mode: "privacy");
        Add("winocr", "Read text from picture", "Image", ".txt", mode: "winocr");
        Add("qrread", "Read QR or bar code", "Image", ".txt", mode: "qrread");
        Add("qrmake", "Make QR code from text", "Text", ".png", mode: "qrmake");
        Add("dupes", "Find duplicate photos…", "Image", "", mode: "dupes");
        Add("smartsearch", "Search files by meaning…", "Files", "", mode: "smartsearch");
        Add("organize", "Tidy this folder…", "Files", "", mode: "organize");
        Add("perspective", "Perspective crop…", "Image", ".png", mode: "perspective");
        Add("hide", "Pixelate / blur…", "Image", ".png", mode: "annotate:Pixelate");
        Add("sheet-pdf", "Spreadsheet to PDF (print studio)…", "Spreadsheet", ".pdf", mode: "sheetstudio");
        Add("icns", "To ICNS", "Image", ".icns");
        Add("email-eml", "MSG to EML", "Document", ".eml");
        Add("email-att", "E-mail attachments to ZIP", "Archive", ".zip", mode: "attachments");
        Add("pdfedit", "Edit PDF pages…", "Pdf", ".pdf", mode: "pdfedit");
        Add("rot90", "Rotate PDF 90° right", "Pdf", ".pdf", mode: "rotate:90");
        Add("rot180", "Rotate PDF 180°", "Pdf", ".pdf", mode: "rotate:180");
        Add("rot270", "Rotate PDF 90° left", "Pdf", ".pdf", mode: "rotate:270");
        Add("trace", "Trace to SVG", "Vector", ".svg", mode: "trace");
        Add("captions", "Captions (Whisper → SRT)", "Subtitle", ".srt", mode: "whisper");
        foreach (var (code, name) in new[] { ("en", "English"), ("hi", "Hindi"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("ja", "Japanese"), ("pt", "Portuguese"), ("ru", "Russian"), ("zh", "Chinese"), ("ko", "Korean") })
            Add("tr-" + code, "Translate to " + name, "Text", "", mode: "translate:" + code);
        Add("mobi", "To MOBI (Kindle)", "Ebook", ".mobi");
        Add("azw3", "To AZW3 (Kindle)", "Ebook", ".azw3");
        Add("woff2", "To WOFF2", "Font", ".woff2");
        Add("eot", "To EOT", "Font", ".eot");
        Add("shp", "To Shapefile", "Geo", ".shp");
        Add("cad-svg", "CAD to SVG", "Cad", ".svg");

        // Video, audio and picture presets
        Add("vid-discord", "Video for Discord (under 10 MB)", "Video", ".mp4", mode: "fit:9.5:720");
        Add("vid-wa", "Video for WhatsApp (720p, under 16 MB)", "Video", ".mp4", mode: "fit:15:720");
        Add("vid-mail", "Video for email (under 18 MB)", "Video", ".mp4", mode: "fit:17:720");
        Add("vid-small", "Compress video", "Video", ".mp4", mode: "compress");
        Add("aud-small", "Compress audio", "Audio", ".mp3", mode: "compress");
        Add("vid-mute", "Mute video", "Video", ".mp4", mode: "mute");
        Add("clip", "Cut a clip…", "Video", "", mode: "clip");
        Add("merge-vid", "Merge videos", "Video", ".mp4", mode: "merge-av");
        Add("merge-aud", "Merge audio", "Audio", ".mp3", mode: "merge-av");
        Add("imgs-gif", "Pictures to GIF animation", "Image", ".gif", mode: "gifanim");
        Add("flip-h", "Flip horizontal", "Image", "", mode: "flip:h");
        Add("flip-v", "Flip vertical", "Image", "", mode: "flip:v");
        Add("turn-90", "Rotate 90° right", "Image", "", mode: "irot:90");
        Add("turn-180", "Rotate 180°", "Image", "", mode: "irot:180");
        Add("turn-270", "Rotate 90° left", "Image", "", mode: "irot:270");
        Add("width-800", "Resize to 800 px wide", "Image", "", mode: "width:800");
        Add("width-1080", "Resize to 1080 px wide", "Image", "", mode: "width:1080");
        Add("width-1920", "Resize to 1920 px wide (HD)", "Image", "", mode: "width:1920");
        Add("for-ig", "Photo for Instagram (JPG, 1080 px wide)", "Image", ".jpg", mode: "width:1080");
        Add("under-100", "Picture under 100 KB", "Image", ".jpg", mode: "under:100");
        Add("under-200", "Picture under 200 KB", "Image", ".jpg", mode: "under:200");
        Add("under-500", "Picture under 500 KB", "Image", ".jpg", mode: "under:500");
        Add("under-1000", "Picture under 1 MB", "Image", ".jpg", mode: "under:1000");
        Add("strip", "Remove metadata (EXIF, location)", "Image", "", mode: "strip");
        Add("watermark", "Add text watermark…", "Image", "", mode: "watermark");
        Add("long-img", "Long image (stack pictures)", "Image", ".png", mode: "longimg");
        Add("motion", "Extract Motion Photo video", "Video", ".mp4", mode: "motion");
        // PDF security and ranges, e-books, 3D
        Add("pdf-protect", "Protect PDF with a password…", "Pdf", ".pdf", mode: "protect");
        Add("pdf-unlock", "Remove PDF password…", "Pdf", ".pdf", mode: "unlock");
        Add("pdf-range", "Keep only some pages…", "Pdf", ".pdf", mode: "range");
        Add("kepub", "To KEPUB (Kobo)", "Ebook", ".kepub.epub");
        Add("glb", "To GLB", "Model3D", ".glb");
        Add("gltf", "To glTF", "Model3D", ".gltf");
        Add("3mf", "To 3MF", "Model3D", ".3mf");
        Add("fbx", "To FBX", "Model3D", ".fbx");

        // The user's own
        foreach (var c in App.Settings.CustomPresets)
        {
            var cat = l.FirstOrDefault(x => x.Extension == c.Extension && x.Mode == "")?.Category ?? "Document";
            l.Add(new Preset(c.Id, c.Name, cat, c.Extension, c.Quality, c.Scale, c.Gray));
        }
        return l;
    }
}
