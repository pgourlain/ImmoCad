using Avalonia;

namespace ImmoCad.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--mcp")) return Mcp.McpHost.RunAsync(args).GetAwaiter().GetResult();
        App.StartupFile = args.FirstOrDefault(a => a.EndsWith(".immeuble", StringComparison.OrdinalIgnoreCase));
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
