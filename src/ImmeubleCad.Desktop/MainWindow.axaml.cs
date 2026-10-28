using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using static ImmeubleCad.Core.Translations;

namespace ImmeubleCad.Desktop;

public partial class MainWindow : Window
{
    private static readonly ILogger Log = App.Log.CreateLogger("MainWindow");
    private MainViewModel Vm => (MainViewModel)DataContext!;
    private bool _closing;
    public IViewport3D View3D { get; private set; } = null!;

    private void CreateViewport()
    {
        // OpenGL when the platform offers it (Silk.NET), the software viewport otherwise, in headless tests, or with IMMEUBLECAD_SOFTWARE=1.
        if (UseOpenGL)
        {
            var gl = new GlViewport3D(); gl.Failed += reason => Avalonia.Threading.Dispatcher.UIThread.Post(() => SwapToSoftware(reason));
            View3D = gl;
        }
        else View3D = new Viewport3D();
        View3DHost.Children.Add(View3D.Control);
    }

    /// <summary>OpenGL when the platform provides it; tests and IMMEUBLECAD_SOFTWARE=1 use the software viewport.</summary>
    public static bool UseOpenGL { get; set; } = Environment.GetEnvironmentVariable("IMMEUBLECAD_SOFTWARE") != "1";

    private void SwapToSoftware(string reason)
    {
        if (View3D is Viewport3D) return;
        Log.LogWarning("OpenGL unavailable ({Reason}); using the software 3D viewport", reason);
        var old = View3D; var sw = new Viewport3D { DataContext = Vm }; sw.Controller.Wireframe = old.Controller.Wireframe; sw.Controller.XRay = old.Controller.XRay;
        View3DHost.Children.Remove(old.Control); View3D = sw; View3DHost.Children.Add(sw.Control); if (Vm.Is3D) sw.Rebuild();
    }

    // Tab (and the segmented control) switches 2D / 3D / wireframe without losing context: the 3D camera starts as a top view matching the plan's framing.
    private void OnWindowKey(object? s, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Tab && Vm.HasDocument && (e.Source is not TextBox)) { Vm.CycleViewCommand.Execute(null); e.Handled = true; }
    }

    private bool HandleTransition(ViewMode from, ViewMode to, Action done)
    {
        var c = View3D.Controller; var look = Plan.LookAtMm; var alt = (Vm.CurrentLevel?.AltitudeMm ?? 0) / 1000.0;
        c.Animate = Vm.Settings.AnimateTransitions;
        if (from == ViewMode.Plan && to is ViewMode.Solid or ViewMode.Wireframe)
        {
            done(); SyncView3D(); View3D.Rebuild();
            c.EnterFromPlan(look.X, look.Y, Plan.Scale, Math.Max(Plan.Bounds.Height, 100), alt); return true;
        }
        if (from is ViewMode.Solid or ViewMode.Wireframe && to == ViewMode.Plan) { c.ReturnToPlan(look.X, look.Y, Plan.Scale, Math.Max(Plan.Bounds.Height, 100), alt, done); return true; }
        return false;   // 3D ↔ wireframe, elevation: immediate
    }

    private void SyncView3D()
    {
        var c = View3D.Controller; c.Wireframe = Vm.Mode == ViewMode.Wireframe; c.XRay = Vm.XRay; c.CutAboveCurrent = Vm.CutAbove; c.Filter = Vm.LevelFilter switch { 0 => ImmeubleCad.Scene.LevelFilter.WholeBuilding, 1 => ImmeubleCad.Scene.LevelFilter.CurrentAndBelow, _ => ImmeubleCad.Scene.LevelFilter.CurrentOnly };
        if (Vm.Is3D) { View3D.Rebuild(); WatchGl(); }
    }

    private bool _watching;
    /// <summary>Some platforms (e.g. macOS) never create a GL context: if none appears shortly after the 3D view is shown, use the software viewport.</summary>
    private void WatchGl()
    {
        if (_watching || View3D is not GlViewport3D gl || gl.Initialized) return; _watching = true;
        var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { t.Stop(); _watching = false; if (View3D is GlViewport3D g && !g.Initialized && Vm.Is3D) SwapToSoftware("no OpenGL context was created"); };
        t.Start();
    }

    public MainWindow()
    {
        InitializeComponent(); CreateViewport();
        DataContextChanged += (_, _) =>
        {
            Vm.Dialogs = new AvaloniaDialogService(this);
            Vm.Changed += () => { Plan.Invalidate(); SyncView3D(); Elev.InvalidateVisual(); SiteMap.InvalidateVisual(); };
            Vm.OnLog += m => Log.LogInformation("{Message}", m);
            Vm.OnError += (what, ex) => Log.LogError(ex, "Failed to {What}", what);
            Vm.RecentFiles.CollectionChanged += (_, _) => BuildMenus(); Vm.LanguageRefreshed += BuildMenus;
            Plan.CursorMoved += p => Vm.Status = $"X {p.X / 1000.0:0.00} m   Y {p.Y / 1000.0:0.00} m";
            Vm.TransitionHandler = HandleTransition;
            KeyDown += OnWindowKey;
            BuildMenus(); Plan.Invalidate();
        };
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.SetAllowDrop(this, true);
    }

    /// <summary>Menus are defined once (MenuSpec) and rendered natively on macOS, in the window elsewhere.</summary>
    private void BuildMenus()
    {
        var specs = MenuFactory.Build(Vm, OpenLogFolder, CopySystemInfo, ShowAbout);
        if (OperatingSystem.IsMacOS()) { NativeMenu.SetMenu(this, MenuFactory.ToNativeMenu(specs)); MenuHost.Content = null; }
        else MenuHost.Content = MenuFactory.ToMenu(specs);
    }

    private void OnDrop(object? s, DragEventArgs e)
    {
        foreach (var f in e.DataTransfer.TryGetFiles() ?? [])
            try { if (f.Path.LocalPath.EndsWith(".immeuble", StringComparison.OrdinalIgnoreCase)) { Vm.OpenFile(f.Path.LocalPath); Log.LogInformation("Opened (drop) {Path}", f.Path.LocalPath); } }
            catch (Exception ex) { Log.LogError(ex, "Failed to open dropped file"); Vm.Status = ex.Message; }
    }

    // Closing: every unsaved document gets its save prompt; Cancel keeps the window open.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_closing || DataContext is not MainViewModel vm || !vm.Docs.Any(d => d.History.IsDirty)) { App.Autosave?.MarkCleanExit(); base.OnClosing(e); return; }
        e.Cancel = true;
        if (await vm.CanQuitAsync()) { _closing = true; App.Autosave?.MarkCleanExit(); Close(); }
    }

    private void OpenLogFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Path.GetDirectoryName(App.Log.LogPath)!, UseShellExecute = true }); }
        catch (Exception ex) { Log.LogError(ex, "Cannot open the log folder"); }
    }

    private async void CopySystemInfo()
    {
        var text = AppLogProvider.SystemInfo() + "\n\n--- log ---\n" + App.Log.Tail(200);
        if (Clipboard != null) await Clipboard.SetTextAsync(text);
        Vm.Status = T("sysinfo.copied");
    }

    private async void ShowAbout() => await new AboutDialog().ShowDialog(this);
}
