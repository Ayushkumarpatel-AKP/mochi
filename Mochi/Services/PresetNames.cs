namespace Mochi.Services;

/// <summary>Display names for presets in the current language: Filee's own translated names where one matches, "To {0}" otherwise.</summary>
public static class PresetNames
{
    // my preset id → Filee's translation key
    private static readonly Dictionary<string, string> Filee = new()
    {
        ["annotate"] = "preset.annotate", ["gray"] = "preset.grayscale", ["half"] = "preset.half_size", ["jpg-small"] = "preset.compress_image",
        ["crop-1-1"] = "preset.crop_video_1_1", ["crop-16-9"] = "preset.crop_video_16_9", ["crop-4-3"] = "preset.crop_video_4_3",
        ["crop-9-16"] = "preset.crop_video_9_16", ["crop-21-9"] = "preset.crop_video_21_9", ["crop-auto"] = "preset.crop_video_auto",
        ["pdf-merge"] = "preset.merge_pdf", ["pdf-small"] = "preset.compress_pdf", ["imgs-pdf"] = "preset.merge_images",
        ["nup2"] = "preset.pdf_nup_2", ["nup4"] = "preset.pdf_nup_4", ["nup6"] = "preset.pdf_nup_6", ["nup8"] = "preset.pdf_nup_8", ["nup9"] = "preset.pdf_nup_9",
        ["extract"] = "preset.extract", ["vid-mp3"] = "preset.extract_audio", ["vid-srt"] = "preset.extract_subtitles",
        ["vid-frame"] = "preset.extract_frames_png", ["pdfedit"] = "preset.group_pdf_edit", ["captions"] = "preset.caption_video",
        ["vid-discord"] = "preset.for_discord", ["vid-wa"] = "preset.for_whatsapp", ["vid-mail"] = "preset.for_email", ["for-ig"] = "preset.for_instagram",
        ["vid-small"] = "preset.compress_video", ["aud-small"] = "preset.compress_audio", ["clip"] = "preset.clip", ["merge-vid"] = "preset.merge_videos",
        ["merge-aud"] = "preset.merge_audio", ["flip-h"] = "preset.flip_horizontal", ["flip-v"] = "preset.flip_vertical", ["motion"] = "preset.extract_motion",
        ["long-img"] = "preset.long_image", ["hide"] = "preset.hide",
    };

    public static string Of(Preset p)
    {
        if (p.Id.StartsWith("c-")) return p.Name;   // the user's own names are theirs
        if (Filee.TryGetValue(p.Id, out var key) && Loc.TryGet(key) is { } t) return t;
        if (p.Name.StartsWith("To ", StringComparison.Ordinal)) return Loc.T("x.to", p.Name[3..]);
        return p.Name;
    }
}
