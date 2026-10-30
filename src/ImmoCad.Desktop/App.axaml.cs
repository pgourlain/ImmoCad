using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ImmoCad.Core;
using Microsoft.Extensions.Logging;

namespace ImmoCad.Desktop;

public partial class App : Application
{
    public static AppLogProvider Log { get; } = new();
    public static AutosaveService? Autosave { get; private set; }
    public static string? StartupFile { get; set; }

    public override void Initialize() { AvaloniaXamlLoader.Load(this); RefreshAppMenu(); }

    /// <summary>The About entry of the macOS application menu follows the language.</summary>
    public void RefreshAppMenu() { if (NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>().FirstOrDefault() is { } about) about.Header = Translations.T("about.app"); }

    private async void OnAbout(object? sender, EventArgs e) { if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }) await new AboutDialog().ShowDialog(w); }

    public override void OnFrameworkInitializationCompleted()
    {
        var log = Log.CreateLogger("App");
        Log.HookGlobalHandlers(log);
        log.LogInformation("ImmoCad starting: version {Version}, built {Built:yyyy-MM-dd}, {Os} {Arch}, .NET {Fw}", typeof(App).Assembly.GetName().Version,
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
            vm.ApplyTheme(settings.Theme);
            // map data: IGN Géoplateforme over HTTP with a disk cache, so a site already seen still shows offline
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("ImmoCad/1.0");
            var cache = new ImmoCad.Geo.DiskCache(Path.Combine(dir, "cache"));
            vm.Geo = new SiteData(new ImmoCad.Geo.IgnGeocoder(http, cache), new ImmoCad.Geo.IgnCadastre(http, cache), new ImmoCad.Geo.IgnTiles(http, cache));
            Translations.Language = settings.Language; RefreshAppMenu();
            var win = new MainWindow { DataContext = vm, Width = settings.WindowWidth, Height = settings.WindowHeight };
            win.Opened += (_, _) =>
            {
                try
                {
                    if (StartupFile != null && File.Exists(StartupFile)) vm.OpenFile(StartupFile);
                    else if (!settings.TourDone && recovered == null) vm.StartTourCommand.Execute(null);   // first start: offer the guided tour once
                    else if (recovered != null) { vm.AdoptRecovered(recovered); log.LogWarning("Recovered an autosaved building after an unclean exit"); }
                }
                catch (Exception ex) { log.LogError(ex, "Cannot open the startup document"); }
            };
            win.Closing += (_, _) => { settings.WindowWidth = win.Width; settings.WindowHeight = win.Height; try { SettingsStore.Save(settings, settingsPath); } catch (IOException) { } };
            // autosave the active document every 3 minutes when it has unsaved changes
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
            timer.Tick += (_, _) => { try { if (vm.HasDocument && vm.History.IsDirty) Autosave.Save(vm.Building); } catch (Exception ex) { log.LogError(ex, "Autosave failed"); } };
            timer.Start();
            if (Environment.GetEnvironmentVariable("IMMOCAD_SMOKE3D") == "1")      // dev aid: open the sample in 3D, wait, quit; the log tells whether OpenGL worked
                win.Opened += async (_, _) => { vm.OpenSampleCommand.Execute(null); vm.SetModeCommand.Execute("Solid"); await Task.Delay(3000); log.LogInformation("Smoke test: 3D shown for 3 s"); Log.Flush(); d.Shutdown(); };
            d.MainWindow = win;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
