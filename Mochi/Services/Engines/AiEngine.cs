using SkiaSharp;

namespace Mochi.Services.Engines;

/// <summary>Offline "smart" picture helpers: enlarge, blur faces, auto-enhance, read text (Windows OCR), read and make QR codes. No install: the models travel inside Mochi.</summary>
public sealed class AiEngine : EngineBase
{
    private static readonly string[] Img = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public override string Id => "ai";
    public override string Name => "Smart picture helpers (built-in)";
    public override string Category => "Image";
    public override string Description => "Makes pictures bigger and sharper, blurs faces, fixes dull photos with one click, reads the text and QR codes in a picture, and makes QR codes. Everything runs on your computer.";
    public override string Formats => "PNG JPG WEBP BMP GIF → enlarged, face-blurred or enhanced picture · picture → TXT (text, QR) · TXT → QR code PNG";

    public override bool CanHandle(string e, Preset p) => p.Mode switch
    {
        var m when m.StartsWith("upscale:") || m is "faceblur" or "enhance" or "winocr" or "qrread" => Is(e, Img),
        "qrmake" => e == ".txt",
        _ => false,
    };

    private static bool HasAlpha(byte[] px) { for (var i = 3; i < px.Length; i += 4) if (px[i] < 255) return true; return false; }

    // JPG and WEBP stay as they are, unless the picture is see-through: that needs PNG
    private static string SameKind(string input) => Util.Ext(input) is ".jpg" or ".jpeg" ? ".jpg" : Util.Ext(input) == ".webp" ? ".webp" : ".png";

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir)
    {
        if (p.Mode == "qrmake")
        {
            var text = File.ReadAllText(input).Trim();
            if (text.Length == 0) throw new InvalidDataException("The text file is empty.");
            if (text.Length > 2000) throw new InvalidDataException("That is too much text for one QR code (the limit is about 2000 characters).");
            using var qr = AiTools.MakeQr(text);
            var qrDest = Util.Out(input, outDir, ".png", " (QR)");
            AiTools.Save(qr, qrDest);
            return new[] { qrDest };
        }

        var (px, w, h) = Matte.Load(input, 0);
        switch (p.Mode)
        {
            case var m when m.StartsWith("upscale:"):
            {
                var scale = m == "upscale:4" ? 4 : 2;
                var (o, ow, oh) = AiTools.Enlarge(px, w, h, scale);
                var dest = Util.Out(input, outDir, HasAlpha(o) ? ".png" : SameKind(input), $" ({scale}x)");
                AiTools.SaveImage(o, ow, oh, dest, 95);
                return new[] { dest };
            }
            case "faceblur":
            {
                var faces = AiTools.FindFaces(px, w, h);
                if (faces.Count == 0) throw new InvalidDataException("No faces found in this picture.");
                using var blurred = AiTools.BlurRegions(px, w, h, faces);
                var dest = Util.Out(input, outDir, HasAlpha(px) ? ".png" : SameKind(input), " (faces blurred)");
                AiTools.Save(blurred, dest, 95);
                return new[] { dest };
            }
            case "enhance":
            {
                AiTools.Enhance(px, w, h);
                var dest = Util.Out(input, outDir, HasAlpha(px) ? ".png" : SameKind(input), " (enhanced)");
                AiTools.SaveImage(px, w, h, dest, 95);
                return new[] { dest };
            }
            case "winocr":
            {
                var text = AiTools.ReadText(px, w, h);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("No text found in this picture.");
                var dest = Util.Out(input, outDir, ".txt", " (text)");
                File.WriteAllText(dest, text, new System.Text.UTF8Encoding(true));
                return new[] { dest };
            }
            case "qrread":
            {
                var codes = AiTools.ReadCodes(px, w, h);
                if (codes.Count == 0) throw new InvalidDataException("No QR code or bar code found in this picture.");
                var dest = Util.Out(input, outDir, ".txt", " (code)");
                File.WriteAllText(dest, string.Join(Environment.NewLine, codes), new System.Text.UTF8Encoding(true));
                return new[] { dest };
            }
        }
        throw new NotSupportedException(p.Mode);
    }
}
