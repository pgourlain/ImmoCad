using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;

namespace ImmeubleCad.Desktop;

public partial class MainWindow : Window
{
    private static readonly ILogger Log = App.Log.CreateLogger("MainWindow");
    private MainViewModel Vm => (MainViewModel)DataContext!;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Vm.Dialogs = new AvaloniaDialogService(this);
            Vm.Changed += () => { Plan.Invalidate(); View3D.InvalidateVisual(); Elev.InvalidateVisual(); };
            Vm.OnLog += m => Log.LogInformation("{Message}", m);
            Vm.OnError += (what, ex) => Log.LogError(ex, "Failed to {What}", what);
            Vm.RecentFiles.CollectionChanged += (_, _) => BuildMenus();
            Plan.CursorMoved += p => Vm.Status = $"X {p.X / 1000.0:0.00} m   Y {p.Y / 1000.0:0.00} m";
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
        Vm.Status = "Informations système copiées";
    }

    private async void ShowAbout() => await Vm.Dialogs.ShowErrorAsync("À propos d'ImmeubleCad", AppLogProvider.SystemInfo() + "\n\nDonnées cartographiques : © IGN / DGFiP — Licence Ouverte 2.0");
}
