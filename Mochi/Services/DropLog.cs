namespace Mochi.Services;

/// <summary>
/// A short trail of what happened to every drag onto the donut or a toolbar (what arrived, which tool was under the pointer, what ran),
/// in %APPDATA%\Mochi\drop.log. When a drag "does nothing", this says where it stopped.
/// </summary>
public static class DropLog
{
    private static readonly string PathFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mochi", "drop.log");
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
                var info = new FileInfo(PathFile);
                if (info.Exists && info.Length > 200_000) File.WriteAllLines(PathFile, File.ReadAllLines(PathFile).TakeLast(300));   // keep it small
                File.AppendAllText(PathFile, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
        }
        catch { /* a log must never get in the way */ }
    }

    public static string Short(IEnumerable<string> files) => string.Join(" | ", files.Take(4).Select(f => System.IO.Path.GetFileName(f.TrimEnd('\\', '/')))) + (files.Count() > 4 ? $" … ({files.Count()})" : "");
}
