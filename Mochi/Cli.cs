using System.Runtime.InteropServices;
using Mochi.Services;
using Microsoft.Win32.SafeHandles;

namespace Mochi;

/// <summary>Headless mode: <c>Mochi --convert &lt;preset-id&gt; [--out dir] files…</c>, <c>--presets</c>, <c>--engines</c>.</summary>
internal static class Cli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id);

    public static int? TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--convert" or "--presets" or "--engines" or "--mkdxf" or "--install" or "--shell" or "--check-update")) return null;
        // The exe is a window program; borrow the parent's console (or the redirected pipe) for the command-line mode.
        AttachConsole(-1);
        foreach (var (id, set) in new (int, Action<TextWriter>)[] { (-11, Console.SetOut), (-12, Console.SetError) })
        {
            var h = GetStdHandle(id);
            if (h != IntPtr.Zero && h != new IntPtr(-1))
                set(new StreamWriter(new FileStream(new SafeFileHandle(h, false), FileAccess.Write), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        }
        App.InitHeadless();

        switch (args[0])
        {
            case "--presets":
                foreach (var p in Converter.Presets) Console.WriteLine($"{p.Id,-12} {p.Name,-28} {p.Category,-12} {p.Extension} {p.Mode}");
                return 0;
            case "--engines":
                foreach (var e in Converter.Engines) Console.WriteLine($"{e.Id,-12} {(e.Ready ? "ready    " : "missing  ")} {e.Name}  {(e.Tool is null ? "" : Tools.Find(e.Tool))}");
                return 0;
            case "--mkdxf" when args.Length >= 2:   // writes a one-line DXF, used to test the CAD engine
                var doc = new ACadSharp.CadDocument();
                doc.Entities.Add(new ACadSharp.Entities.Line { StartPoint = new CSMath.XYZ(0, 0, 0), EndPoint = new CSMath.XYZ(10, 10, 0) });
                using (var w = new ACadSharp.IO.DxfWriter(args[1], doc, false)) w.Write();
                return 0;
            case "--shell" when args.Length >= 2:   // Explorer right-click entry on / off
                ShellIntegration.Set(args[1] == "on"); App.Settings.ContextMenu = args[1] == "on"; App.Settings.Save(); Console.WriteLine("context menu " + args[1]); return 0;
            case "--check-update":
                var rel = Updater.CheckAsync().GetAwaiter().GetResult();
                Console.WriteLine($"current={Updater.Current} found={rel.Version} newer={rel.Newer} asset={rel.AssetUrl} error={rel.Error}");
                return 0;
            case "--install" when args.Length >= 2:   // same code as the Install button on the Engines page
                var eng = Converter.Engines.FirstOrDefault(e => e.Id == args[1]);
                if (eng is null || !Installer.CanInstall(eng)) { Console.Error.WriteLine("Cannot install " + args[1]); return 2; }
                try { Installer.InstallAsync(eng, s => Console.WriteLine(s)).GetAwaiter().GetResult(); Console.WriteLine(eng.Ready ? "READY" : "NOT READY"); return eng.Ready ? 0 : 1; }
                catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
            case "--convert" when args.Length >= 3:
                var p2 = Converter.Find(args[1]);
                if (p2 is null) { Console.Error.WriteLine("Unknown preset " + args[1]); return 2; }
                string? outDir = null;
                var files = new List<string>();
                for (var i = 2; i < args.Length; i++)
                    if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                    else if (args[i] == "--ans" && i + 1 < args.Length)   // answers for dialogs, split by |  (password, page range, clip times…)
                    {
                        var ans = args[++i].Split('|');
                        Mochi.Services.Engines.Prompts.Hook = (_, l, d, _) => Task.FromResult<string[]?>(l.Select((_, k) => k < ans.Length ? ans[k] : d[k]).ToArray());
                    }
                    else files.Add(args[i]);

                var failed = 0;
                try
                {
                    if (p2.IsMerge)
                        foreach (var o in Converter.RunMany(files, p2, outDir)) Console.WriteLine("OK   " + o);
                    else
                        foreach (var f in files)
                        {
                            try { foreach (var o in Converter.Run(f, p2, outDir)) Console.WriteLine("OK   " + o); }
                            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {f}: {ex.Message}"); }
                        }
                }
                catch (Exception ex) { failed++; Console.WriteLine("FAIL " + ex.Message); }
                return failed == 0 ? 0 : 1;
            default:
                Console.Error.WriteLine("Usage: Mochi --convert <preset-id> [--out dir] files…   |   --presets   |   --engines");
                return 2;
        }
    }
}
