namespace Mochi.Services;

/// <summary>Keeps the search index of the chosen folders fresh in the background, at low priority, so a search never has to wait.</summary>
public static class SmartIndexService
{
    private static Timer? _timer;

    /// <summary>The folders to search: the user's own list, starting with Documents, Desktop and Downloads.</summary>
    public static List<string> Folders()
    {
        var s = App.Settings;
        if (!s.SearchFoldersSeeded)
        {
            foreach (var f in new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
                if (Directory.Exists(f) && !s.SearchFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) s.SearchFolders.Add(f);
            s.SearchFoldersSeeded = true; s.Save();
        }
        return s.SearchFolders.Where(Directory.Exists).ToList();
    }

    public static void Start()
    {
        _timer?.Dispose();
        if (!App.Settings.BackgroundIndex) return;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));   // the first run waits until the app has settled
    }

    public static void Stop() { _timer?.Dispose(); _timer = null; }

    private static async void Tick()
    {
        try { foreach (var f in Folders()) await IndexStore.For(f).RefreshAsync(); }
        catch { /* try again next time */ }
    }
}
