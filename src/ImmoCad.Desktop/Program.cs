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

    /// <summary>On macOS the window is asked to render with OpenGL (software as fallback): the 3D view is an OpenGL control and gets no context when Avalonia renders with Metal.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] }).LogToTrace();
}
