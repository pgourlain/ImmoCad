using Avalonia;

namespace ImmeubleCad.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--mcp")) return Mcp.McpHost.RunAsync(args).GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
