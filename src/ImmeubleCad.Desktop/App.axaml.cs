using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;

namespace ImmeubleCad.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public static AppLogProvider Log { get; } = new();

    public override void OnFrameworkInitializationCompleted()
    {
        var log = Log.CreateLogger("App");
        Log.HookGlobalHandlers(log);
        log.LogInformation("ImmeubleCad starting: {Os} {Arch} .NET {Fw}", System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture, Environment.Version);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.MainWindow = new MainWindow { DataContext = new MainViewModel() };
        base.OnFrameworkInitializationCompleted();
    }
}
