using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ImmeubleCad.Core;
using Microsoft.Extensions.Logging;

namespace ImmeubleCad.Desktop;

public partial class App : Application
{
    public static AppLogProvider Log { get; } = new();
    public static AutosaveService? Autosave { get; private set; }
    public static string? StartupFile { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var log = Log.CreateLogger("App");
        Log.HookGlobalHandlers(log);
        log.LogInformation("ImmeubleCad starting: version {Version}, built {Built:yyyy-MM-dd}, {Os} {Arch}, .NET {Fw}", typeof(App).Assembly.GetName().Version,
            File.GetLastWriteTimeUtc(typeof(App).Assembly.Location), System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture, Environment.Version);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            var dir = AppLogProvider.DefaultDir();
            var settingsPath = Path.Combine(dir, "settings.json"); var settings = SettingsStore.Load(settingsPath);
            Autosave = new AutosaveService(dir);
            var recovered = Autosave.HasRecovery ? Autosave.Recover() : null;
            Autosave.MarkStarted();

            var vm = new MainViewModel(startWithSample: false) { Settings = settings, SettingsPath = settingsPath };
            foreach (var f in settings.RecentFiles.Where(File.Exists)) vm.RecentFiles.Add(f);
            var win = new MainWindow { DataContext = vm, Width = settings.WindowWidth, Height = settings.WindowHeight };
            win.Opened += (_, _) =>
            {
                try
                {
                    if (StartupFile != null && File.Exists(StartupFile)) vm.OpenFile(StartupFile);
                    else if (recovered != null) { vm.AdoptRecovered(recovered); log.LogWarning("Recovered an autosaved building after an unclean exit"); }
                }
                catch (Exception ex) { log.LogError(ex, "Cannot open the startup document"); }
            };
            win.Closing += (_, _) => { settings.WindowWidth = win.Width; settings.WindowHeight = win.Height; try { SettingsStore.Save(settings, settingsPath); } catch (IOException) { } };
            // autosave the active document every 3 minutes when it has unsaved changes
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
            timer.Tick += (_, _) => { try { if (vm.HasDocument && vm.History.IsDirty) Autosave.Save(vm.Building); } catch (Exception ex) { log.LogError(ex, "Autosave failed"); } };
            timer.Start();
            d.MainWindow = win;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
