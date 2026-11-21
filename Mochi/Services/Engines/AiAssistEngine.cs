namespace Mochi.Services.Engines;

/// <summary>The AI tools of the toolbar: they open a window (chat, rename, plan) and return the files it made, if any.</summary>
public sealed class AiAssistEngine : EngineBase
{
    public override string Id => "aiassist";
    public override string Name => "AI assistant";
    public override string Category => "Ai";
    public override string Description => "Summarise a file, ask it questions, explain a picture, rename files by what they contain, or describe a job in words and let Mochi chain the tools. Uses the AI you connect in Settings → AI (Grok, OpenAI, Gemini, or Ollama on this PC).";
    public override string Formats => "PDF, Word, Excel, PowerPoint, web, text and pictures → answers · any files → better names · any files + a sentence → a finished job";

    public override bool CanHandle(string e, Preset p) => p.Mode switch
    {
        "ai:summary" or "ai:ask" => AiFiles.CanRead(e),
        "ai:explain" => AiFiles.IsImage(e) || e == ".pdf",
        "ai:rename" => e != "",
        "ai:command" => true,
        _ => false,
    };

    public override IReadOnlyList<string> Convert(string input, Preset p, string outDir) => ConvertMany(new[] { input }, p, outDir);

    public override IReadOnlyList<string> ConvertMany(IReadOnlyList<string> inputs, Preset p, string outDir)
    {
        var hook = ToolHooks.Ai ?? throw new InvalidOperationException("The AI tools need the app window");
        return hook(p.Mode, inputs).GetAwaiter().GetResult();
    }
}
