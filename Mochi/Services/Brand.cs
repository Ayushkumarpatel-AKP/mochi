namespace Mochi.Services;

public static class Brand
{
    /// <summary>The name shown in the app. Filee's own strings say "Filee"; Loc swaps it for this.</summary>
    public const string Name = "Mochi";

    /// <summary>One of the mascot stickers in Assets/Mascot (hello, cheer, convert, done, oops, sleep, settings), or null if it can't load.</summary>
    public static Avalonia.Media.Imaging.Bitmap? Mascot(string name)
    {
        try { return new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri($"avares://Mochi/Assets/Mascot/{name}.png"))); }
        catch { return null; }
    }
}
