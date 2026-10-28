using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImmeubleCad.Core.Tools;
using Microsoft.Extensions.Logging;

namespace ImmeubleCad.Desktop;

public partial class MainWindow : Window
{
    private static readonly ILogger Log = App.Log.CreateLogger("MainWindow");
    private MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Vm.Changed += () => { Plan.InvalidateVisual(); View3D.InvalidateVisual(); };
            Plan.CursorMoved += p => Vm.Status = $"X {p.X / 1000.0:0.00} m  Y {p.Y / 1000.0:0.00} m";
        };
    }

    private void OnToggleWall(object? s, RoutedEventArgs e)
    {
        Plan.Tool = DrawWallBtn.IsChecked == true ? new DrawChainTool(Vm.CommitWalls) : null;
        Vm.Status = Plan.Tool != null ? "Click the next point — Enter to finish — Esc to cancel" : "Ready";
    }

    private async Task<IStorageFile?> Pick(bool save, string title, string pattern)
    {
        var types = new[] { new FilePickerFileType(title) { Patterns = new[] { pattern } } };
        if (save) return await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, FileTypeChoices = types, DefaultExtension = pattern.TrimStart('*') });
        return (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, FileTypeFilter = types })).FirstOrDefault();
    }

    private async void OnOpen(object? s, RoutedEventArgs e) => await Guard("open", async () =>
    { if (await Pick(false, "Building", "*.immeuble") is { } f) Vm.LoadFrom(f.Path.LocalPath); });

    private async void OnSave(object? s, RoutedEventArgs e) => await Guard("save", async () =>
    { if (await Pick(true, "Building", "*.immeuble") is { } f) { Vm.SaveTo(f.Path.LocalPath); Log.LogInformation("Saved {Path}", f.Path.LocalPath); } });

    private async void OnImport(object? s, RoutedEventArgs e) => await Guard("import plan", async () =>
    {
        var f = (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Plan image", FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } })).FirstOrDefault();
        if (f == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = Vm.ImportPlan(f.Path.LocalPath, 0.1); // default 10 px/m… until the calibration wizard exists
        Log.LogInformation("Import plan {Path}: {N} walls in {Ms} ms", f.Path.LocalPath, n, sw.ElapsedMilliseconds);
    });

    private async void OnExport(object? s, RoutedEventArgs e) => await Guard("export", async () =>
    {
        var fmt = (string)((MenuItem)s!).Tag!;
        var ext = fmt switch { "ifc" => "*.ifc", "freecad" => "*.FCMacro", "dxf" => "*.dxf", "obj" => "*.obj", _ => "*.csv" };
        if (await Pick(true, "Export", ext) is not { } f) return;
        if (fmt == "csv") File.WriteAllText(f.Path.LocalPath, Vm.BomCsv()); else Vm.Export(fmt, f.Path.LocalPath);
        Log.LogInformation("Exported {Fmt} to {Path}", fmt, f.Path.LocalPath);
    });

    private async Task Guard(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Log.LogError(ex, "Failed to {What}", what); Vm.Status = $"Error ({what}): {ex.Message}"; }
    }
}
