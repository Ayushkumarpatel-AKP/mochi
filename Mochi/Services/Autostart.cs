using Microsoft.Win32;

namespace Mochi.Services;

/// <summary>Starts Mochi quietly in the tray when Windows starts, so the donut is always ready without opening the app.</summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Mochi";

    /// <summary>Makes Windows' startup entry match the setting; also retires the older Startup-folder shortcut so Mochi doesn't start twice.</summary>
    public static void Apply(bool on)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;
            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Mochi.lnk")); } catch { /* not there */ }
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (on) k.SetValue(Name, $"\"{exe}\" --background");
            else k.DeleteValue(Name, false);
        }
        catch { /* a locked-down PC simply keeps the old behaviour */ }
    }
}
