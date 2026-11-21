using Avalonia;
using Mochi.Services;

namespace Mochi;

internal static class Program
{
    /// <summary>Optional start-up flags: <c>--page engines</c> opens that page, <c>--donut a.png b.png</c> opens the donut for those files, <c>--annotate|--pdfedit|--perspective file</c> a tool.</summary>
    public static string? StartPage { get; private set; }
    public static List<string> StartDonut { get; } = new();
    public static (string Tool, string File)? StartTool { get; private set; }

    /// <summary><c>[--prompt "…"] --ai ai:summary a.pdf b.pdf</c> opens an AI tool (ai:summary, ai:ask, ai:explain, ai:rename, ai:command) on those files.</summary>
    public static (string Mode, List<string> Files)? StartAi { get; private set; }

    /// <summary>Started with <c>--background</c> (Windows start-up): tray only, no window.</summary>
    public static bool Background;

    [STAThread]
    public static int Main(string[] args)
    {
        if (Cli.TryRun(args) is { } code) return code;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--background") Background = true;
            else if (args[i] == "--page" && i + 1 < args.Length) StartPage = args[++i];
            else if (args[i] == "--tool" && i + 1 < args.Length) Services.Engines.ToolHooks.InitialTool = args[++i];
            else if (args[i] is "--annotate" or "--pdfedit" or "--perspective" or "--removebg" or "--erase" or "--dupes" or "--smartsearch" or "--organize" or "--sheet" && i + 1 < args.Length) StartTool = (args[i][2..], args[++i]);
            else if (args[i] == "--prompt" && i + 1 < args.Length) Views.AiCommandWindow.PendingPrompt = args[++i];   // with --ai ai:command: the request to plan
            else if (args[i] == "--ai" && i + 2 < args.Length) { StartAi = (args[i + 1], args.Skip(i + 2).ToList()); break; }
            else if (args[i] == "--donut") StartDonut.AddRange(args.Skip(i + 1));
        }

        // one Mochi at a time: a second launch hands its files to the first and leaves
        if (!SingleInstance.TryBecomeFirst())
        {
            SingleInstance.Send(StartDonut.Count > 0 ? "donut\t" + string.Join("\t", StartDonut) : "show");
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
