using Mochi.ViewModels;

namespace Mochi.Services;

/// <summary>Converts new files that appear in watched folders. Results go to a "converted" subfolder so they are never re-watched.</summary>
public sealed class WatchService
{
    private const string OutName = "converted";
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly MainViewModel _vm;

    public WatchService(MainViewModel vm) => _vm = vm;

    public void Sync()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var e in App.Settings.Watch.Where(e => e.Enabled && Directory.Exists(e.Path)))
        {
            var entry = e;
            var w = new FileSystemWatcher(entry.Path) { EnableRaisingEvents = true, IncludeSubdirectories = false };
            w.Created += (_, a) => _ = Handle(entry, a.FullPath);
            w.Renamed += (_, a) => _ = Handle(entry, a.FullPath);
            _watchers.Add(w);
        }
    }

    private async Task Handle(WatchEntry entry, string path)
    {
        var preset = Converter.Find(entry.PresetId);
        if (preset is null || !Converter.IsSupported(path, preset)) return;
        if (!await WaitReady(path)) return;
        await _vm.ConvertAsync(new[] { path }, preset, Path.Combine(entry.Path, OutName));
    }

    private static async Task<bool> WaitReady(string path)
    {
        for (var i = 0; i < 40; i++)
        {
            try { using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
            catch (FileNotFoundException) { return false; }
            catch (IOException) { await Task.Delay(250); }
        }
        return false;
    }
}
