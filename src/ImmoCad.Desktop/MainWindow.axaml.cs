using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

public partial class MainWindow : Window
{
    private static readonly ILogger Log = App.Log.CreateLogger("MainWindow");
    private MainViewModel Vm => (MainViewModel)DataContext!;
    private bool _closing;
    public IViewport3D View3D { get; private set; } = null!;
    /// <summary>First-person walk: what is drawn over the view, and the real-time loop behind it.</summary>
    public WalkOverlay? Overlay { get; private set; }
    public WalkDriver? Walker { get; private set; }

    private void CreateViewport()
    {
        // OpenGL when the platform offers it (Silk.NET), the software viewport otherwise, in headless tests, or with IMMOCAD_SOFTWARE=1.
        if (UseOpenGL)
        {
            var gl = new GlViewport3D(); gl.Failed += reason => Avalonia.Threading.Dispatcher.UIThread.Post(() => SwapToSoftware(reason));
            View3D = gl;
        }
        else View3D = new Viewport3D();
        View3DHost.Children.Add(View3D.Control);
        Cube.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right; Cube.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top; Cube.Margin = new Avalonia.Thickness(0, 10, 14, 0); Cube.ZIndex = 10;
        View3DHost.Children.Add(Cube); AttachCube();
    }

    /// <summary>Orientation cube over the 3D view: it redraws whenever the viewport redraws (camera moved).</summary>
    public ViewCube Cube { get; } = new();
    private void AttachCube()
    {
        var c = View3D.Controller; Cube.Controller = c; var inner = c.Invalidate;
        c.Invalidate = () => { inner?.Invoke(); Cube.InvalidateVisual(); };
    }

    /// <summary>OpenGL when the platform provides it; tests and IMMOCAD_SOFTWARE=1 use the software viewport.</summary>
    public static bool UseOpenGL { get; set; } = Environment.GetEnvironmentVariable("IMMOCAD_SOFTWARE") != "1";

    private void SwapToSoftware(string reason)
    {
        if (View3D is Viewport3D) return;
        Log.LogWarning("OpenGL unavailable ({Reason}); using the software 3D viewport", reason);
        var old = View3D; var sw = new Viewport3D { DataContext = Vm }; sw.Controller.Wireframe = old.Controller.Wireframe; sw.Controller.XRay = old.Controller.XRay;
        View3DHost.Children.Remove(old.Control); View3D = sw; View3DHost.Children.Insert(0, sw.Control); AttachCube(); if (Vm.Is3D) sw.Rebuild();
        Vm.WalkAvailable = false; if (Vm.IsWalk) Vm.EndWalkRequest(T("walk.nogl"));            // walking is drawn with OpenGL only
    }

    // Tab (and the segmented control) switches 2D / 3D / wireframe without losing context: the 3D camera starts as a top view matching the plan's framing.
    private void OnWindowKey(object? s, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Handled || e.Source is TextBox || !Vm.HasDocument) return;
        var K = e.Key;
        if (Vm.IsWalk)
        {   // walking: the keys belong to the walk (Esc leaves it); only ⌘/Ctrl combinations (save…) go on to the code below
            if (Walker?.OnKeyDown(e) == true) { e.Handled = true; return; }
        }
        else if (K == Avalonia.Input.Key.G && e.KeyModifiers == Avalonia.Input.KeyModifiers.None) { Vm.WalkCommand.Execute(null); e.Handled = true; return; }
        if (K == Avalonia.Input.Key.Tab && !Vm.IsWalk) { Vm.CycleViewCommand.Execute(null); e.Handled = true; return; }
        var cmd = MenuFactory.CommandModifiers; var mods = e.KeyModifiers;
        if (mods.HasFlag(cmd))
        {   // ⌘ on macOS, Ctrl elsewhere; fallback for when the menu accelerators do not reach the focused control
            var shift = mods.HasFlag(Avalonia.Input.KeyModifiers.Shift);
            switch (K)
            {
                case Avalonia.Input.Key.Z when shift: Vm.RedoCommand.Execute(null); break;
                case Avalonia.Input.Key.Z: Vm.UndoCommand.Execute(null); break;
                case Avalonia.Input.Key.Y: Vm.RedoCommand.Execute(null); break;
                case Avalonia.Input.Key.A: Vm.SelectAllCommand.Execute(null); break;
                case Avalonia.Input.Key.C: Vm.CopyCommand.Execute(null); break;
                case Avalonia.Input.Key.X: Vm.CutCommand.Execute(null); break;
                case Avalonia.Input.Key.V: Vm.PasteCommand.Execute(null); break;
                case Avalonia.Input.Key.D: Vm.DuplicateCommand.Execute(null); break;
                case Avalonia.Input.Key.OemPlus or Avalonia.Input.Key.Add: Vm.ZoomPlanCommand.Execute("in"); break;
                case Avalonia.Input.Key.OemMinus or Avalonia.Input.Key.Subtract: Vm.ZoomPlanCommand.Execute("out"); break;
                case Avalonia.Input.Key.D0 or Avalonia.Input.Key.NumPad0: Vm.ZoomPlanCommand.Execute("fit"); break;
                case Avalonia.Input.Key.S when shift: Vm.SaveAsCommand.Execute(null); break;
                case Avalonia.Input.Key.S: Vm.SaveCommand.Execute(null); break;
                case Avalonia.Input.Key.N: Vm.NewDocumentCommand.Execute(null); break;
                case Avalonia.Input.Key.O: Vm.OpenCommand.Execute(null); break;
                case Avalonia.Input.Key.W: Vm.CloseTabCommand.Execute(-1); break;
                case Avalonia.Input.Key.E: Vm.ExportCommand.Execute("ask"); break;
                case Avalonia.Input.Key.K: Vm.OpenCommandPaletteCommand.Execute(null); break;
                default: return;
            }
            e.Handled = true; return;
        }
        if (mods != Avalonia.Input.KeyModifiers.None) return;
        switch (K)      // single-letter tools (plan view)
        {
            case Avalonia.Input.Key.F9: Vm.ToggleSnapToGridCommand.Execute(null); break;
            case Avalonia.Input.Key.Delete: Vm.DeleteSelectionCommand.Execute(null); break;
            case Avalonia.Input.Key.Escape: Vm.SetToolCommand.Execute("Select"); break;
            case Avalonia.Input.Key.V: Vm.SetToolCommand.Execute("Select"); break;
            case Avalonia.Input.Key.W: Vm.SetToolCommand.Execute("DrawWall"); break;
            case Avalonia.Input.Key.P: Vm.SetToolCommand.Execute("DrawPipe"); break;
            case Avalonia.Input.Key.D: Vm.SetToolCommand.Execute("Door"); break;
            case Avalonia.Input.Key.N: Vm.SetToolCommand.Execute("Window"); break;
            case Avalonia.Input.Key.S: Vm.SetToolCommand.Execute("Space"); break;
            case Avalonia.Input.Key.M: Vm.SetToolCommand.Execute("Measure"); break;
            case Avalonia.Input.Key.F: Vm.SetToolCommand.Execute("MergeRegion"); break;
            case Avalonia.Input.Key.K: Vm.SetToolCommand.Execute("CutWall"); break;
            default: return;
        }
        e.Handled = true;
    }

    private bool HandleTransition(ViewMode from, ViewMode to, Action done)
    {
        var c = View3D.Controller; var look = Plan.LookAtMm; var alt = (Vm.CurrentLevel?.AltitudeMm ?? 0) / 1000.0;
        c.Animate = Vm.Settings.AnimateTransitions;
        if (Vm.SideBySide) return false;                                   // both views are already on screen: no camera animation
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
        var c = View3D.Controller; c.WalkMode = Vm.IsWalk && View3D is GlViewport3D; Cube.IsVisible = !Vm.IsWalk;
        if (Overlay != null) Overlay.IsVisible = Vm.IsWalk;
        if (Vm.IsWalk && View3D is GlViewport3D) Walker?.Start(); else Walker?.Stop();
        c.Wireframe = Vm.Mode == ViewMode.Wireframe; c.XRay = Vm.XRay; c.CutAboveCurrent = Vm.CutAbove; c.Filter = Vm.LevelFilter switch { 0 => ImmoCad.Scene.LevelFilter.WholeBuilding, 1 => ImmoCad.Scene.LevelFilter.CurrentAndBelow, _ => ImmoCad.Scene.LevelFilter.CurrentOnly };
        if (Vm.Show3D) { View3D.Rebuild(); WatchGl(); }
    }

    private bool _watching;
    /// <summary>Some platforms (e.g. macOS) never create a GL context: if none appears shortly after the 3D view is shown, use the software viewport.</summary>
    private void WatchGl()
    {
        if (_watching || View3D is not GlViewport3D gl || gl.Initialized) return; _watching = true;
        var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { t.Stop(); _watching = false; if (View3D is GlViewport3D g && !g.Initialized && (Vm.Is3D || Vm.IsWalk)) SwapToSoftware("no OpenGL context was created"); };
        t.Start();
    }

    public MainWindow()
    {
        InitializeComponent(); CreateViewport();
        DataContextChanged += (_, _) =>
        {
            Vm.Dialogs = new AvaloniaDialogService(this); Vm.AskRiserHeight = Plan.AskRiserHeight;
            if (Overlay == null)
            {
                Overlay = new WalkOverlay(Vm, () => Walker?.Touch()); View3DHost.Children.Add(Overlay);
                Walker = new WalkDriver(Vm, () => View3D.Controller, () => Overlay?.Refresh());
                if (View3D is GlViewport3D gl) gl.Hover += (text, at) => Overlay?.ShowHover(text, at);
                KeyUp += (_, ke) => Walker?.OnKeyUp(ke); Deactivated += (_, _) => Walker?.Clear();
            }
            Vm.PlanLookAt = () => Plan.LookAtMm; Vm.WalkAvailable = View3D is GlViewport3D;
            Vm.Changed += () => { Plan.Invalidate(); Cube2D.InvalidateVisual(); SyncView3D(); Elev.InvalidateVisual(); SiteMap.InvalidateVisual(); };
            Cube2D.Current = () => Vm.ViewPlane switch { ViewPlane.XZ => Vm.PlaneFlip ? ViewCube.Face.Back : ViewCube.Face.Front, ViewPlane.YZ => Vm.PlaneFlip ? ViewCube.Face.Left : ViewCube.Face.Right, _ => ViewCube.Face.Top };
            Cube2D.FaceChosen = f => { switch (f) { case ViewCube.Face.Front: Vm.ChoosePlane(ViewPlane.XZ, false); break; case ViewCube.Face.Back: Vm.ChoosePlane(ViewPlane.XZ, true); break; case ViewCube.Face.Right: Vm.ChoosePlane(ViewPlane.YZ, false); break; case ViewCube.Face.Left: Vm.ChoosePlane(ViewPlane.YZ, true); break; default: Vm.ChoosePlane(ViewPlane.XY, false); break; } };
            Vm.OnLog += m => Log.LogInformation("{Message}", m);
            Vm.OnError += (what, ex) => Log.LogError(ex, "Failed to {What}", what);
            Vm.RecentFiles.CollectionChanged += (_, _) => BuildMenus(); Vm.LanguageRefreshed += BuildMenus;
            Plan.CursorMoved += p => { var (h, v) = Vm.ViewPlane switch { ViewPlane.XZ => ("X", "Z"), ViewPlane.YZ => ("Y", "Z"), _ => ("X", "Y") }; Vm.Status = $"{h} {p.X / 1000.0:0.00} m   {v} {p.Y / 1000.0:0.00} m"; };
            Vm.TransitionHandler = HandleTransition;
            Cube2D.Turned = Plan.TurnQuarter; Cube2D.Spin = () => Vm.ViewPlane == ViewPlane.XY ? Plan.ViewRotationRad : 0; Plan.ViewTurned += Cube2D.InvalidateVisual;
            Vm.PlanZoomRequested += how => Plan.Zoom(how);
            KeyDown += OnWindowKey;
            Vm.MenuCommands = () => MainViewModel.FromMenus(MenuFactory.Build(Vm, OpenLogFolder, CopySystemInfo, ShowAbout));
            Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.CommandPaletteOpen) && Vm.CommandPaletteOpen) Avalonia.Threading.Dispatcher.UIThread.Post(() => CommandBox.Focus()); };
            BuildMenus(); Plan.Invalidate();
        };
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.SetAllowDrop(this, true);
    }

    /// <summary>Menus are defined once (MenuSpec) and rendered natively on macOS, in the window elsewhere.</summary>
    private bool _menusPending;
    private NativeMenu? _nativeMenu;
    private void BuildMenus()
    {
        if (_menusPending) return;
        _menusPending = true;
        // Deferred: rebuilding while a native menu command is still running (save → recent files, language) crashes macOS.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _menusPending = false;
            var specs = MenuFactory.Build(Vm, OpenLogFolder, CopySystemInfo, ShowAbout);
            if (OperatingSystem.IsMacOS())
            {
                var fresh = MenuFactory.ToNativeMenu(specs);
                if (_nativeMenu == null) { _nativeMenu = fresh; NativeMenu.SetMenu(this, _nativeMenu); }
                else { var items = fresh.Items.ToList(); fresh.Items.Clear(); _nativeMenu.Items.Clear(); foreach (var i in items) _nativeMenu.Items.Add(i); }
                MenuHost.Content = null; (Avalonia.Application.Current as App)?.RefreshAppMenu();
            }
            else MenuHost.Content = MenuFactory.ToMenu(specs);
        }, Avalonia.Threading.DispatcherPriority.Background);
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

    // ---- command palette (⌘K): typing filters, ↑↓ choose, Enter runs, Esc or a click outside closes ----
    private void OnCommandKey(object? s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Vm.MoveCommandIndex(+1); break;
            case Key.Up: Vm.MoveCommandIndex(-1); break;
            case Key.Enter: Vm.RunCommandCommand.Execute(null); Plan.Focus(); break;
            case Key.Escape: Vm.CloseCommandPaletteCommand.Execute(null); Plan.Focus(); break;
            default: return;
        }
        e.Handled = true;
    }
    private void OnCommandTapped(object? s, Avalonia.Input.TappedEventArgs e) { if ((s as Control)?.DataContext is CommandEntry c) { Vm.RunCommandCommand.Execute(c); Plan.Focus(); } }
    private void OnPaletteBackdrop(object? s, PointerPressedEventArgs e) => Vm.CloseCommandPaletteCommand.Execute(null);
    private void OnPaletteInside(object? s, PointerPressedEventArgs e) => e.Handled = true;

    private async void ShowAbout() => await new AboutDialog().ShowDialog(this);
}
