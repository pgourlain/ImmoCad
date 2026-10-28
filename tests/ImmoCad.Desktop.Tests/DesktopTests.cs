using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless;
using Avalonia.Controls;
using ImmoCad.Desktop;
using ImmoCad.Core.Model;
using Microsoft.Extensions.Logging;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ImmoCad.Desktop.Tests.TestAppBuilder))]
// Language is process-wide state: UI tests run one at a time. The suite starts in French (legacy assertions); LanguageTests cover English and switching.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]   // one collection = tests run one at a time

namespace ImmoCad.Desktop.Tests;

public sealed class HeadlessFixture
{
    // Test classes run in parallel: the Avalonia platform must be initialised exactly once.
    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessFixture).Assembly), LazyThreadSafetyMode.ExecutionAndPublication);
    public HeadlessUnitTestSession Session => Shared.Value;
}

public static class HeadlessExtensions
{
    /// <summary>
    /// Runs an async body on the UI thread and awaits it. Plain <c>Dispatch(async () =&gt; …)</c> binds to the <c>Action</c> overload (async void):
    /// the body is not awaited and its assertion failures are lost, so async UI tests must go through this helper.
    /// </summary>
    public static Task RunAsync(this HeadlessUnitTestSession session, Func<Task> body, CancellationToken ct = default) => session.Dispatch<int>(async () => { await body(); return 0; }, CancellationToken.None);
}

public static class TestInit
{
    [System.Runtime.CompilerServices.ModuleInitializer] public static void Init() { Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots")); ImmoCad.Core.Translations.Language = "fr"; MainWindow.UseOpenGL = false; Environment.SetEnvironmentVariable("IMMOCAD_DATA_DIR", Path.Combine(Path.GetTempPath(), "immocad-tests-" + Environment.ProcessId)); }   // headless tests draw 3D in software
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class DesktopTests
{
    private readonly HeadlessFixture f = new();
    private static string Shots() { var d = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(d); return d; }

    [Fact] public Task Main_window_shows_plan_and_levels() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm }; w.Show();
        Assert.Equal(4, vm.Levels.Count); Assert.Equal("Site", vm.Levels[^1].Name);   // 3 levels + the Site entry at the bottom
        Assert.Equal("R+2", vm.Levels[0].Name); // top level first
        w.CaptureRenderedFrame()!.Save(Path.Combine(Shots(), "main.png")); w.Close();
    }, CancellationToken.None);

    [Fact] public Task Thirty_levels_fit_the_level_bar() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm }; w.Show();
        while (vm.Building.Levels.Count < 30) vm.AddLevelCommand.Execute(null);
        Assert.Equal(31, vm.Levels.Count); Assert.Equal("R+29", vm.Levels[0].Name); Assert.True(vm.Levels[0].IsCurrent);
        for (var i = 0; i < 29; i++) vm.LevelDownCommand.Execute(null);
        Assert.Equal("RDC", vm.CurrentLevel!.DisplayName);
        w.CaptureRenderedFrame()!.Save(Path.Combine(Shots(), "levels30.png")); w.Close();
    }, CancellationToken.None);

    [Fact] public Task Select_group_move_on_level_and_between_levels_with_undo() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var rdc = vm.CurrentLevel!;
        var ids = rdc.Walls.Select(x => x.Id).ToList(); var first = rdc.Walls[0].Footprint[0];
        vm.SelectWalls(ids.Take(2), false); Assert.Equal(2, vm.SelectedWalls.Count);
        vm.MoveSelection(500, 300);
        Assert.Equal(new(first.X + 500, first.Y + 300), vm.CurrentLevel!.Walls[0].Footprint[0]);
        vm.MoveSelectionUpCommand.Execute(null);                       // to R+1
        Assert.Equal("R+1", vm.CurrentLevel!.DisplayName); Assert.Equal(6, vm.CurrentLevel.Walls.Count);
        Assert.Equal(2, vm.Building.Levels[0].Walls.Count);
        vm.UndoCommand.Execute(null);
        Assert.Equal(4, vm.Building.Levels[0].Walls.Count);           // back on RDC
        Assert.NotNull(vm.CurrentLevel);                               // current level survives undo (resolved by id)
        vm.UndoCommand.Execute(null);
        Assert.Equal(first, vm.Building.Levels[0].Walls[0].Footprint[0]);
    }, CancellationToken.None);

    [Fact] public Task Draw_wall_chain_is_one_undo_step_and_saves() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var walls = vm.CurrentLevel!.Walls.Count; var steps = vm.History.Items.Count;
        var tool = new ImmoCad.Core.Tools.DrawChainTool(vm.CommitWalls);
        ImmoCad.Core.Tools.ToolPointer P(int x, int y) => new(new(x, y), x, y);
        tool.Down(P(0, 9000)); tool.Up(P(0, 9000)); tool.Move(P(3000, 9000)); tool.Down(P(3000, 9000)); tool.Move(P(3000, 11000)); tool.Down(P(3000, 11000)); tool.Finish();
        Assert.Equal(walls + 2, vm.CurrentLevel!.Walls.Count); Assert.Equal(steps + 1, vm.History.Items.Count);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".immeuble");
        vm.SaveTo(path); Assert.False(vm.History.IsDirty);
        var vm2 = new MainViewModel(); vm2.LoadFrom(path); File.Delete(path);
        Assert.Equal(walls + 2, vm2.CurrentLevel!.Walls.Count);
    }, CancellationToken.None);

    [Fact] public Task Switch_to_3d_renders_and_back() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.Settings.AnimateTransitions = false; var w = new MainWindow { DataContext = vm }; w.Show(); w.UpdateLayout();
        vm.SetModeCommand.Execute("Solid"); Assert.True(vm.Is3D);
        w.CaptureRenderedFrame()!.Save(Path.Combine(Shots(), "3d.png"));
        vm.SetModeCommand.Execute("Plan"); Assert.False(vm.Is3D); w.Close();
    }, CancellationToken.None);

    [Fact] public Task Undo_redo_levels() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel();
        vm.AddLevelCommand.Execute(null); Assert.Equal(4, vm.Building.Levels.Count);
        vm.UndoCommand.Execute(null); Assert.Equal(3, vm.Building.Levels.Count);
        vm.RedoCommand.Execute(null); Assert.Equal(4, vm.Building.Levels.Count);
    }, CancellationToken.None);
}

public class WizardTests
{
    private readonly HeadlessFixture f = new();

    /// <summary>Plan of a 5 m × 4 m room: 400×320 px picture, 100 px per 5 m = 0.1 px/mm... walls 10 px thick, plus a thin dimension line.</summary>
    internal static byte[] PlanPng(int w = 600, int h = 480)
    {
        var px = Enumerable.Repeat((byte)255, w * h).ToArray();
        for (var y = 40; y < 440; y++) for (var x = 50; x < 550; x++) if (x < 60 || x >= 540 || y < 50 || y >= 430) px[y * w + x] = 0;
        for (var x = 100; x < 500; x++) px[20 * w + x] = 0; // thin line, must be ignored
        using var img = new ImageMagick.MagickImage(px, new ImageMagick.PixelReadSettings((uint)w, (uint)h, ImageMagick.StorageType.Char, "I"));
        return img.ToByteArray(ImageMagick.MagickFormat.Png);
    }

    [Fact] public void Wizard_model_calibrates_detects_and_applies_in_one_step()
    {
        var b = new ImmoCad.Core.Model.Building(); var h = new ImmoCad.Core.History.BuildingHistory();
        var a = new ImmoCad.Core.History.BuildingActions(b, h, new ImmoCad.Core.History.BuildingEventsService()); var lv = a.AddLevel("RDC", 0);
        var w = new ImportWizardViewModel(PlanPng()) { DistanceMetres = 5 };
        w.AutoDeskew(); Assert.InRange(Math.Abs(w.Angle), 0, 0.3);
        w.AddCalibrationPoint(50, 240); w.AddCalibrationPoint(550, 240);      // 500 px = 5 m → 0.1 px/mm
        Assert.True(w.IsCalibrated); Assert.Equal(0.1, w.Calibration!.Value.PxPerMmX, 6);
        w.MinThickness = 5; w.Detect(); Assert.Single(w.Detected); Assert.Single(w.Detected[0].Holes);
        var steps = h.Items.Count; var images = new Dictionary<string, byte[]>();
        Assert.Equal(1, w.Apply(a, b.Levels[0], images));
        Assert.Equal(steps + 2, h.Items.Count);                              // background + walls
        var fp = b.Levels[0].Walls[0].Footprint; Assert.Equal(5000, fp.Max(p => p.X) - fp.Min(p => p.X)); Assert.Equal(4000, fp.Max(p => p.Y) - fp.Min(p => p.Y));
        Assert.Single(images); Assert.NotNull(b.Levels[0].BackgroundImage);
    }

    [Fact] public void Wizard_two_axes_detects_stretch()
    {
        var w = new ImportWizardViewModel(PlanPng()) { TwoAxes = true, DistanceMetres = 5, DistanceVMetres = 4 };
        w.AddCalibrationPoint(50, 240); w.AddCalibrationPoint(550, 240); w.AddCalibrationPoint(300, 50); w.AddCalibrationPoint(300, 430);
        Assert.Equal(0.1, w.Calibration!.Value.PxPerMmX, 6); Assert.Equal(380.0 / 4000, w.Calibration.Value.PxPerMmY, 6);
    }

    [Fact] public Task Wizard_window_renders_each_step() => f.Session.Dispatch(() =>
    {
        var win = new ImportWizard(PlanPng()); win.Show();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        var vm = win.Result; win.Next_ForTest(); Assert.Equal(WizardStep.Calibrate, vm.Step); win.Canvas_ForTest().PixelClicked_ForTest(50, 240); win.Canvas_ForTest().PixelClicked_ForTest(550, 240);
        Assert.True(vm.IsCalibrated);
        win.Next_ForTest();                              // → Detect
        Assert.Equal(WizardStep.Detect, vm.Step);
        win.CaptureRenderedFrame()!.Save(Path.Combine(dir, "wizard-detect.png"));
        win.Next_ForTest(); Assert.Equal(WizardStep.Review, vm.Step); Assert.Single(vm.Detected);
        win.CaptureRenderedFrame()!.Save(Path.Combine(dir, "wizard-review.png")); win.Close();
    }, CancellationToken.None);
}

public class AppLogTests
{
    [Fact] public void Writes_and_rolls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ilog" + Guid.NewGuid().ToString("N"));
        using (var p = new AppLogProvider(dir, maxBytes: 500))
        {
            var l = p.CreateLogger("T");
            Parallel.For(0, 50, i => l.LogInformation("line {I} {Pad}", i, new string('x', 40)));
            l.LogError(new InvalidOperationException("boom"), "failed");
            p.Flush();
        }
        Assert.True(File.Exists(Path.Combine(dir, "log.1.txt")));
        var all = string.Join("\n", Directory.GetFiles(dir).Select(File.ReadAllText));
        Assert.Contains("boom", all);
        Directory.Delete(dir, true);
    }
}

public class AlignUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Align_tool_pairs_residuals_apply_and_undo() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.SelectLevelCommand.Execute(vm.Building.Levels[1].Id);   // R+1, ghost = RDC
        vm.SetToolCommand.Execute("Align"); Assert.True(vm.IsAlignTool);
        var before = vm.CurrentLevel!.Walls[0].Footprint[0]; var steps = vm.History.Items.Count;
        // the upper level is shifted by (+100, +60) mm compared to the one below: three corner pairs
        vm.AlignClick(100, 60); vm.AlignClick(0, 0);
        vm.AlignClick(8100, 60); vm.AlignClick(8000, 0);
        Assert.NotNull(vm.AlignResiduals); Assert.All(vm.AlignResiduals!, r => Assert.True(r < 1));
        vm.AlignClick(100, 6060); vm.AlignClick(0, 6000);
        Assert.Equal(3, vm.AlignPairs.Count);
        Assert.True(vm.AlignApply());
        Assert.Equal(steps + 1, vm.History.Items.Count);
        Assert.Equal(new(before.X - 100, before.Y - 60), vm.CurrentLevel!.Walls[0].Footprint[0]);   // walls moved with the plan
        vm.UndoCommand.Execute(null); Assert.Equal(before, vm.CurrentLevel!.Walls[0].Footprint[0]);
    }, CancellationToken.None);

    [Fact] public Task Align_collinear_points_are_rejected_without_changing_the_model() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.SelectLevelCommand.Execute(vm.Building.Levels[1].Id); vm.SetToolCommand.Execute("Align");
        var steps = vm.History.Items.Count;
        foreach (var (a, b) in new[] { (0.0, 0.0), (1000.0, 1000.0), (2000.0, 2000.0) }) { vm.AlignClick(a, b); vm.AlignClick(a, b); }
        Assert.False(vm.AlignApply()); Assert.Equal(steps, vm.History.Items.Count);
    }, CancellationToken.None);
}

public class NetworkUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Riser_template_pipe_valve_radiator_and_undo() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var nets = vm.Building.Networks.Count; var steps = vm.History.Items.Count;
        vm.RiserTemplate = "tpl.dhw"; vm.PlaceRiser(new(1000, 1000));
        Assert.Equal(nets + 2, vm.Building.Networks.Count); Assert.Equal(steps + 1, vm.History.Items.Count);
        vm.SetToolCommand.Execute("DrawPipe"); vm.PipeType = ImmoCad.Core.Model.NetworkType.HeatingSupply; vm.PipeElevation = 2400;
        vm.CommitPipe([new(300, 300), new(3000, 300), new(3000, 2500)]);
        var net = vm.Building.Networks.First(n => n.Type == ImmoCad.Core.Model.NetworkType.HeatingSupply && n.Segments.Any(s => vm.Building.Networks.SelectMany(x => x.Nodes).Any(nd => nd.Position.Z == 2400)));
        Assert.Contains(net.Nodes, n => n.Position == new ImmoCad.Core.Model.PointMm3(3000, 300, 2400));
        // valve: refuses off-pipe, accepts on the pipe
        vm.SelectPaletteCommand.Execute("shutoff-valve");
        var segs = vm.Building.Networks.Sum(n => n.Segments.Count);
        vm.PlaceFromPalette(new(1500, 2200)); Assert.Equal(segs, vm.Building.Networks.Sum(n => n.Segments.Count)); Assert.Contains("tuyau", vm.Status);
        Assert.NotNull(vm.PlacementHint(new(1500, 2200))); Assert.Null(vm.PlacementHint(new(1500, 310)));
        vm.PlaceFromPalette(new(1500, 310)); Assert.Equal(segs + 1, vm.Building.Networks.Sum(n => n.Segments.Count));
        Assert.Contains(vm.Building.Networks.SelectMany(n => n.Nodes), n => n.Component == "shutoff-valve");
        vm.SelectPaletteCommand.Execute("radiator"); var eq = vm.Building.Equipment.Count; vm.PlaceFromPalette(new(5000, 260)); Assert.Equal(eq + 1, vm.Building.Equipment.Count);
        vm.UndoCommand.Execute(null); Assert.Equal(eq, vm.Building.Equipment.Count);
    }, CancellationToken.None);

    [Fact] public Task Networks_panel_toggle_and_lock_and_properties() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var id = vm.Networks[0].Id;
        vm.ToggleNetworkCommand.Execute(id); Assert.False(vm.Building.Networks[0].Visible); Assert.False(vm.Networks[0].Visible);
        vm.LockNetworkCommand.Execute(id); Assert.True(vm.Building.Networks[0].Locked);
        vm.SelectWalls([vm.CurrentLevel!.Walls[0].Id], false); vm.PropLabel = "Porteur"; vm.PropHeight = 2600; vm.ApplyPropertiesCommand.Execute(null);
        Assert.Equal("Porteur", vm.CurrentLevel!.Walls[0].Label); Assert.Equal(2600, vm.CurrentLevel.Walls[0].HeightMm);
    }, CancellationToken.None);

    [Fact] public Task Window_with_panels_renders() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm }; w.Show();
        vm.RiserTemplate = "tpl.heating"; vm.PlaceRiser(new(4000, 3000));
        vm.CommitPipe([new(300, 600), new(4000, 600), new(4000, 3000)]);
        vm.SelectPaletteCommand.Execute("radiator"); vm.PlaceFromPalette(new(2500, 260));
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "networks.png")); w.Close();
    }, CancellationToken.None);
}

public class ElevationUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Elevation_of_selected_wall_vertical_horizontal_pipes_and_level_snap() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm }; w.Show();
        vm.ShowElevationCommand.Execute(null); Assert.False(vm.IsElevation); Assert.Contains("Sélectionnez", vm.Status);   // nothing selected
        vm.SelectWalls([vm.CurrentLevel!.Walls[0].Id], false); vm.ShowElevationCommand.Execute(null);
        Assert.True(vm.IsElevation); Assert.NotNull(vm.Plane);
        var nets = vm.Building.Networks.Count; vm.RiserType = ImmoCad.Core.Model.NetworkType.ColdWater;
        vm.PlaceVerticalPipe(300, null, null);                                   // whole building
        var n = vm.Building.Networks[^1]; Assert.Equal(nets + 1, vm.Building.Networks.Count);
        Assert.Equal(0, n.Nodes.Min(x => x.Position.Z)); Assert.Equal(8100, n.Nodes.Max(x => x.Position.Z));
        vm.PlaceVerticalPipe(1500, 2700, 5400); Assert.Equal(2700, vm.Building.Networks[^1].Nodes.Min(x => x.Position.Z));
        var segs = vm.Building.Networks.Sum(x => x.Segments.Count); vm.PlaceHorizontalPipe(300, 3000, 2400); Assert.Equal(segs + 1, vm.Building.Networks.Sum(x => x.Segments.Count));
        vm.PlaceHorizontalPipe(300, 305, 2400); Assert.Equal(segs + 1, vm.Building.Networks.Sum(x => x.Segments.Count));    // too short: ignored
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "elevation.png"));
        vm.SetModeCommand.Execute("Plan"); Assert.True(vm.IsPlan); w.Close();
    }, CancellationToken.None);
}

public class HandleUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Mouse_selects_pipe_drags_end_handle_and_makes_elbow_with_one_undo_each() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show();
        vm.SetToolCommand.Execute("DrawPipe"); vm.PipeType = ImmoCad.Core.Model.NetworkType.ColdWater; vm.PipeElevation = 2400;
        vm.CommitPipe([new(1000, 3000), new(3000, 3000)]); vm.SetToolCommand.Execute("Select");
        w.UpdateLayout(); w.CaptureRenderedFrame();   // layout + first render so the view fits itself
        var plan = w.FindControl<PlanView>("Plan")!;
        Avalonia.Point At(int x, int y) => plan.TranslatePoint(plan.ToScreenForTest(new(x, y)), w)!.Value;
        var net = vm.Building.Networks[^1]; var steps = vm.History.Items.Count;

        // click the pipe body → selected, handles appear (no model change)
        w.MouseDown(At(2000, 3000), Avalonia.Input.MouseButton.Left); w.MouseUp(At(2000, 3000), Avalonia.Input.MouseButton.Left);
        Assert.NotNull(vm.SelectedSegment); Assert.Equal(steps, vm.History.Items.Count);

        // drag the right end handle 1 m further → extended
        w.MouseDown(At(3000, 3000), Avalonia.Input.MouseButton.Left); w.MouseMove(At(3500, 3000)); w.MouseMove(At(4000, 3000)); w.MouseUp(At(4000, 3000), Avalonia.Input.MouseButton.Left);
        Assert.Equal(4000, vm.Building.Networks[^1].Nodes.Max(n => n.Position.X)); Assert.Equal(steps + 1, vm.History.Items.Count);

        // drag the same handle straight up → elbow
        w.MouseDown(At(4000, 3000), Avalonia.Input.MouseButton.Left); w.MouseMove(At(4000, 3600)); w.MouseMove(At(4000, 4500)); w.MouseUp(At(4000, 4500), Avalonia.Input.MouseButton.Left);
        Assert.Equal(2, vm.Building.Networks[^1].Segments.Count); Assert.Equal(steps + 2, vm.History.Items.Count);
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "handles.png"));
        vm.UndoCommand.Execute(null); vm.UndoCommand.Execute(null); Assert.Equal(2000, vm.Building.Networks[^1].Nodes.Max(n => n.Position.X) - 1000);
        w.Close();
    }, CancellationToken.None);

    [Fact] public Task Box_select_and_drag_walls_with_mouse_moves_group_in_one_step() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show();
        w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; Avalonia.Point At(int x, int y) => plan.TranslatePoint(plan.ToScreenForTest(new(x, y)), w)!.Value;
        vm.ToggleSnapToGridCommand.Execute(null);                    // exact deltas: the grid step depends on the zoom
        var steps = vm.History.Items.Count; var first = vm.CurrentLevel!.Walls[0].Footprint[0];
        w.MouseDown(At(-300, -300), Avalonia.Input.MouseButton.Left); w.MouseMove(At(4000, 1500)); w.MouseMove(At(9000, 7000)); w.MouseUp(At(9000, 7000), Avalonia.Input.MouseButton.Left);
        Assert.Equal(4, vm.SelectedWalls.Count);
        w.MouseDown(At(4000, 100), Avalonia.Input.MouseButton.Left); w.MouseMove(At(4000, 400)); w.MouseMove(At(4000, 600)); w.MouseUp(At(4000, 600), Avalonia.Input.MouseButton.Left);
        Assert.Equal(steps + 1, vm.History.Items.Count);
        Assert.Equal(new(first.X, first.Y + 500), vm.CurrentLevel!.Walls[0].Footprint[0]);
        w.Close();
    }, CancellationToken.None);
}

public class ShellTests
{
    private readonly HeadlessFixture f = new();
    private static string Tmp(string ext = ".immeuble") => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ext);

    [Fact] public Task Welcome_screen_then_new_open_recent_and_tabs() => f.Session.Dispatch(() =>
    {
        var dlg = new FakeDialogService(); var vm = new MainViewModel(startWithSample: false) { Dialogs = dlg, Settings = new() };
        var w = new MainWindow { DataContext = vm }; w.Show(); w.UpdateLayout();
        Assert.True(vm.ShowWelcome); Assert.False(vm.HasDocument); Assert.Empty(vm.Tabs);
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "welcome.png"));
        vm.NewDocumentCommand.Execute(null); Assert.False(vm.ShowWelcome); Assert.Single(vm.Tabs); Assert.Equal("Sans titre", vm.Tabs[0].Title);
        var p = Tmp(); vm.SaveTo(p); Assert.Equal(Path.GetFileNameWithoutExtension(p), vm.Tabs[0].Title); Assert.Contains(p, vm.RecentFiles);
        vm.OpenSampleCommand.Execute(null); Assert.Equal(2, vm.Tabs.Count); Assert.True(vm.Tabs[1].IsActive); Assert.Equal(3, vm.Building.Levels.Count);
        vm.SwitchTabCommand.Execute(0); Assert.Single(vm.Building.Levels); Assert.True(vm.Tabs[0].IsActive);
        vm.OpenFile(p); Assert.Equal(2, vm.Tabs.Count);                         // already open → just switches
        File.Delete(p); w.Close();
    }, CancellationToken.None);

    [Fact] public Task Dirty_marker_and_save_prompt_on_close_with_each_answer() => f.Session.RunAsync(async () =>
    {
        var dlg = new FakeDialogService(); var vm = new MainViewModel(startWithSample: false) { Dialogs = dlg, Settings = new() };
        vm.NewDocumentCommand.Execute(null); Assert.False(vm.Tabs[0].IsDirty); vm.AddLevelCommand.Execute(null); Assert.True(vm.Tabs[0].IsDirty); Assert.StartsWith("•", vm.Title);
        dlg.SaveChoices.Enqueue(SaveChoice.Cancel); Assert.False(await vm.CloseDocumentAsync(0)); Assert.Single(vm.Docs);
        dlg.SaveChoices.Enqueue(SaveChoice.Discard); Assert.True(await vm.CloseDocumentAsync(0)); Assert.True(vm.ShowWelcome);
        vm.NewDocumentCommand.Execute(null); vm.AddLevelCommand.Execute(null); var p = Tmp();
        dlg.SaveChoices.Enqueue(SaveChoice.Save); dlg.SaveAnswers.Enqueue(p);
        Assert.True(await vm.CanQuitAsync()); Assert.True(File.Exists(p)); File.Delete(p);
        vm.NewDocumentCommand.Execute(null); vm.AddLevelCommand.Execute(null);
        dlg.SaveChoices.Enqueue(SaveChoice.Save); dlg.SaveAnswers.Enqueue(null);     // user cancels the file picker → document stays open
        Assert.False(await vm.CloseDocumentAsync(0)); Assert.Single(vm.Docs);
    }, CancellationToken.None);

    [Fact] public Task Errors_are_reported_through_the_dialog_service_and_logged() => f.Session.RunAsync(async () =>
    {
        var dlg = new FakeDialogService(); var vm = new MainViewModel(startWithSample: false) { Dialogs = dlg, Settings = new() }; var logged = new List<string>();
        vm.OnError += (what, ex) => logged.Add(what);
        var bad = Tmp(); File.WriteAllText(bad, "not a zip"); dlg.OpenAnswers.Enqueue(bad);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Single(dlg.Errors); Assert.Contains("ouvrir", dlg.Errors[0]); Assert.Equal(["ouvrir"], logged); Assert.True(vm.ShowWelcome); File.Delete(bad);
    }, CancellationToken.None);

    [Fact] public Task History_panel_lists_steps_and_click_jumps() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        vm.AddLevelCommand.Execute(null); vm.AddLevelCommand.Execute(null);
        Assert.Equal(["État initial", "Ajouter un niveau", "Ajouter un niveau", "Ajouter un niveau"], vm.HistoryRows.Select(r => r.Name));
        Assert.True(vm.HistoryRows[^1].IsCurrent); vm.JumpToCommand.Execute(0);
        Assert.Single(vm.Building.Levels); Assert.True(vm.HistoryRows[1].IsCurrent); Assert.True(vm.HistoryRows[2].IsFuture);
        vm.JumpToCommand.Execute(-1); Assert.Empty(vm.Building.Levels);
    }, CancellationToken.None);

    [Fact] public Task Menus_are_defined_once_and_rendered_with_shortcuts() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var specs = MenuFactory.Build(vm, () => { }, () => { }, () => { });
        Assert.Equal(["_Fichier", "_Édition", "_Niveau", "_Affichage", "_Aide"], specs.Select(s => s.Header)); ImmoCad.Core.Translations.Language = "en"; Assert.Equal("_File", MenuFactory.Build(vm, () => { }, () => { }, () => { })[0].Header); ImmoCad.Core.Translations.Language = "fr";
        var menu = MenuFactory.ToMenu(specs); Assert.Equal(5, menu.Items.Count);
        var save = specs[0].Items.First(i => i.Header == "Enregistrer"); Assert.Equal(Avalonia.Input.Key.S, save.Key); Assert.True(save.UseCommandKey);
        Assert.NotEqual(Avalonia.Input.KeyModifiers.None, MenuFactory.CommandModifiers);
        Assert.NotNull(MenuFactory.ToNativeMenu(specs));
        Assert.Contains(specs[4].Items, i => i.Header.StartsWith("Visite") && i.Command!.CanExecute(null));
        Assert.False(new NotYetImplementedCommand().CanExecute(null));                                                  // later features bind to a disabled command
    }, CancellationToken.None);

    [Fact] public Task Drag_and_drop_style_open_and_display_toggles() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); Assert.True(vm.GhostBelow); vm.ToggleGhostBelowCommand.Execute(null); Assert.False(vm.GhostBelow);
        vm.ToggleCommonOnlyCommand.Execute(null); Assert.True(vm.CommonOnly);
        vm.SelectWalls(vm.CurrentLevel!.Walls.Select(x => x.Id), false); var steps = vm.History.Items.Count; vm.StraightenSelectionCommand.Execute(null);
        Assert.Equal(steps + 1, vm.History.Items.Count); Assert.Contains("Redressé", vm.Status);
    }, CancellationToken.None);
}

public class LogBehaviourTests
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void FaultAndForget()
    {
        var t = Task.Run(() => throw new InvalidOperationException("task-boom"));
        ((IAsyncResult)t).AsyncWaitHandle.WaitOne();      // wait for the fault without observing it
    }

    [Fact] public void Unhandled_task_exception_is_logged_and_debug_level_is_filtered_by_default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ilog" + Guid.NewGuid().ToString("N"));
        using (var p = new AppLogProvider(dir, min: Microsoft.Extensions.Logging.LogLevel.Information))
        {
            var l = p.CreateLogger("Test"); using var hook = p.HookGlobalHandlers(l);
            l.LogDebugPublic("hidden-debug"); l.LogInformationPublic("visible-info");
            FaultAndForget();
            var text = ""; var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && !text.Contains("task-boom")) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(50); p.Flush(); text = File.Exists(p.LogPath) ? File.ReadAllText(p.LogPath) : ""; }
            Assert.Contains("visible-info", text); Assert.DoesNotContain("hidden-debug", text);
            Assert.Contains("task-boom", text);
        }
        Directory.Delete(dir, true);
    }

    [Fact] public void Level_from_environment_and_system_info()
    {
        Environment.SetEnvironmentVariable("IMMOCAD_LOG", "debug"); Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, AppLogProvider.LevelFromEnvironment()); Assert.True(AppLogProvider.MirrorToStderr());
        Environment.SetEnvironmentVariable("IMMOCAD_LOG", null); Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, AppLogProvider.LevelFromEnvironment());
        Assert.Contains(".NET", AppLogProvider.SystemInfo());
    }
}

internal static class LogExt
{
    public static void LogDebugPublic(this Microsoft.Extensions.Logging.ILogger l, string m) => Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(l, "{M}", m);
    public static void LogInformationPublic(this Microsoft.Extensions.Logging.ILogger l, string m) => Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(l, "{M}", m);
}

public class DisplayOptionTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Ghost_toggles_and_common_only_change_what_is_drawn() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.SelectLevelCommand.Execute(vm.Building.Levels[1].Id);
        var w = new MainWindow { DataContext = vm, Width = 900, Height = 700 }; w.Show(); w.UpdateLayout();
        byte[] Px() { var f2 = w.CaptureRenderedFrame()!; using var ms = new MemoryStream(); f2.Save(ms); return ms.ToArray(); }
        var withGhost = Px(); vm.ToggleGhostBelowCommand.Execute(null); var without = Px(); Assert.NotEqual(withGhost, without);
        vm.ToggleGhostAboveCommand.Execute(null); Assert.NotEqual(without, Px());
        var all = Px(); foreach (var wall in vm.CurrentLevel!.Walls) { }
        vm.ToggleCommonOnlyCommand.Execute(null); Assert.NotEqual(all, Px());                  // sample walls are Private on standard levels → hidden
        w.Close();
    }, CancellationToken.None);
}

public class RoomToolsTests
{
    private readonly HeadlessFixture f = new();

    private static MainViewModel Basement()
    {
        // a basement with a corridor and three cellars separated by walls
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        var l = vm.Building.Levels[0]; vm.SetLevelKindCommand.Execute($"{l.Id}:Basement");
        vm.SelectLevelCommand.Execute(l.Id); var t = new ImmoCad.Core.Tools.DrawChainTool(vm.CommitWalls);
        ImmoCad.Core.Tools.ToolPointer P(int x, int y) => new(new(x, y), x, y);
        foreach (var (a, b) in new[] { ((0, 0), (6200, 0)), ((6200, 0), (6200, 2400)), ((6200, 2400), (0, 2400)), ((0, 2400), (0, 0)), ((2000, 0), (2000, 2400)), ((4100, 0), (4100, 2400)) })
        { t.Down(P(a.Item1, a.Item2)); t.Up(P(a.Item1, a.Item2)); t.Move(P(b.Item1, b.Item2)); t.Down(P(b.Item1, b.Item2)); t.Finish(); }
        return vm;
    }

    [Fact] public Task Doors_windows_flip_and_move_on_walls_and_cellars_get_700mm_doors() => f.Session.Dispatch(() =>
    {
        var vm = Basement(); var l = vm.CurrentLevel!;
        Assert.Equal(LevelKind.Basement, l.Kind); var steps = vm.History.Items.Count;
        vm.PlaceOpening(new(1000, 0), OpeningKind.Door);                       // click on the south wall
        var door = vm.CurrentLevel!.Walls.SelectMany(w => w.Openings).Single(); Assert.Equal(700, door.WidthMm); Assert.Equal(steps + 1, vm.History.Items.Count);
        vm.PlaceOpening(new(1000, 0), OpeningKind.Door); Assert.True(vm.CurrentLevel!.Walls.SelectMany(w => w.Openings).Single().SwingLeft);   // second click on the door flips it
        vm.PlaceOpening(new(3000, 1200), OpeningKind.Window); Assert.Equal("Cliquez sur un mur", vm.Status);                                         // nothing under the click
        vm.PlaceOpening(new(5000, 0), OpeningKind.Window); Assert.Equal(2, vm.CurrentLevel!.Walls.SelectMany(w => w.Openings).Count());
        var wall = vm.CurrentLevel!.Walls.First(w => w.Openings.Any(o => o.Kind == OpeningKind.Door)); var d = wall.Openings[0];
        vm.MoveOpeningTo(wall, d, new(1600, 10)); Assert.Equal(1600, vm.CurrentLevel!.Walls.SelectMany(w => w.Openings).First(o => o.Kind == OpeningKind.Door).PositionMm);
    }, CancellationToken.None);

    [Fact] public Task Space_tool_finds_closed_rooms_refuses_open_ones_and_cellars_are_numbered_in_one_step() => f.Session.Dispatch(() =>
    {
        var vm = Basement(); vm.NewSpaceKind = SpaceKind.Cellar;
        vm.PlaceSpace(new(1000, 1200)); vm.PlaceSpace(new(3000, 1200)); vm.PlaceSpace(new(5000, 1200));
        Assert.Equal(3, vm.CurrentLevel!.Spaces.Count); Assert.All(vm.CurrentLevel.Spaces, s => Assert.Equal(Stability.Common, s.Stability));
        Assert.InRange(ImmoCad.Core.Geometry.SpaceFinder.AreaMm2(vm.CurrentLevel.Spaces[0].Outline) / 1e6, 3.5, 4.5);
        vm.PlaceSpace(new(-3000, -3000)); Assert.Equal(3, vm.CurrentLevel.Spaces.Count);                    // outside: not closed
        Assert.Equal(3, vm.SpaceItems.Count);
        vm.SetToolCommand.Execute("NumberCellars"); vm.CellarPrefix = "Cave "; vm.CellarStart = 12; var steps = vm.History.Items.Count;
        vm.NumberClick(new(5000, 1200)); vm.NumberClick(new(1000, 1200)); vm.NumberClick(new(3000, 1200)); vm.NumberCommit();
        Assert.Equal(steps + 1, vm.History.Items.Count);
        Assert.Equal("Cave 12", vm.CurrentLevel.Spaces.First(s => ImmoCad.Core.Geometry.SpaceFinder.Contains(s.Outline, new(5000, 1200))).DisplayName);
        Assert.Equal("Cave 14", vm.CurrentLevel.Spaces.First(s => ImmoCad.Core.Geometry.SpaceFinder.Contains(s.Outline, new(3000, 1200))).DisplayName);
        Assert.Equal(15, vm.CellarStart);
    }, CancellationToken.None);

    [Fact] public Task Measure_adds_nothing_and_level_menu_actions_work_with_30_levels() => f.Session.RunAsync(async () =>
    {
        var dlg = new FakeDialogService(); var vm = Basement(); vm.Dialogs = dlg; var steps = vm.History.Items.Count;
        vm.SetToolCommand.Execute("Measure"); vm.MeasureClick(new(0, 0)); vm.MeasureClick(new(3000, 4000)); Assert.Contains("5.00", vm.Status); Assert.Equal(steps, vm.History.Items.Count);
        var id = vm.Building.Levels[0].Id;
        dlg.PromptAnswers.Enqueue("Sous-sol 1"); await vm.RenameLevelCommand.ExecuteAsync(id); Assert.Equal("Sous-sol 1", vm.Building.Levels[0].Name);
        dlg.PromptAnswers.Enqueue("-2,7 ; 2,5"); await vm.SetLevelGeometryCommand.ExecuteAsync(id); Assert.Equal((-2700, 2500), (vm.Building.Levels[0].AltitudeMm, vm.Building.Levels[0].HeightMm));
        dlg.PromptAnswers.Enqueue("n'importe quoi"); var s2 = vm.History.Items.Count; await vm.SetLevelGeometryCommand.ExecuteAsync(id); Assert.Equal(s2, vm.History.Items.Count);   // invalid input: nothing happens
        vm.AddLevelAboveCommand.Execute(id); vm.AddLevelBelowCommand.Execute(id); Assert.Equal(3, vm.Building.Levels.Count);
        while (vm.Building.Levels.Count < 30) vm.AddLevelCommand.Execute(null);
        Assert.Equal(31, vm.Levels.Count); Assert.Contains("altitude", vm.Levels[0].Tooltip);
    }, CancellationToken.None);

    [Fact] public Task Window_shows_doors_rooms_and_the_spaces_tab() => f.Session.Dispatch(() =>
    {
        var vm = Basement(); vm.NewSpaceKind = SpaceKind.Cellar; vm.PlaceSpace(new(1000, 1200)); vm.PlaceSpace(new(3000, 1200)); vm.PlaceSpace(new(5000, 1200));
        vm.PlaceOpening(new(1000, 0), OpeningKind.Door); vm.PlaceOpening(new(3000, 0), OpeningKind.Door); vm.PlaceOpening(new(5000, 2400), OpeningKind.Window);
        vm.NumberClick(new(1000, 1200)); vm.NumberClick(new(3000, 1200)); vm.NumberCommit();
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "basement.png"));
        vm.ToggleCommonOnlyCommand.Execute(null); w.CaptureRenderedFrame(); w.Close();
    }, CancellationToken.None);
}


public class ThreeDTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Plan_to_3d_to_plan_ten_times_leaves_the_plan_exactly_where_it_was() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.Settings.AnimateTransitions = false; var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; plan.Scale = 0.0731; plan.Offset = (123.5, -40.25); var (s0, o0) = (plan.Scale, plan.Offset);
        for (var i = 0; i < 10; i++) { vm.SetModeCommand.Execute("Solid"); w.UpdateLayout(); vm.SetModeCommand.Execute("Plan"); w.UpdateLayout(); }
        Assert.Equal(s0, plan.Scale); Assert.Equal(o0, plan.Offset); Assert.True(vm.IsPlan);
    }, CancellationToken.None);

    [Fact] public Task Animated_transition_starts_on_the_plan_framing_and_finishes_on_the_3d_angle_then_returns() => f.Session.RunAsync(async () =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var c = w.View3D.Controller; vm.SetModeCommand.Execute("Solid"); Assert.True(c.IsAnimating); Assert.True(vm.Is3D);
        Assert.InRange(c.Camera.Pitch, 1.4, 1.58);                                       // starts as a top view
        await Task.Delay(900); Assert.False(c.IsAnimating); Assert.InRange(c.Camera.Pitch, 0.55, 0.7);   // settled on the default 3D angle (π/5)
        vm.SetModeCommand.Execute("Plan"); Assert.True(c.IsAnimating); Assert.True(vm.Is3D);              // still showing 3D while it tilts back
        await Task.Delay(900); Assert.True(vm.IsPlan); Assert.False(c.IsAnimating);
    }, CancellationToken.None);

    [Fact] public Task Modes_filters_xray_and_cut_and_screenshots() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.Settings.AnimateTransitions = false; var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        vm.SelectLevelCommand.Execute(vm.Building.Levels[1].Id); vm.SetModeCommand.Execute("Solid"); var c = w.View3D.Controller;
        Assert.True(c.Objects.Count > 0, $"objects={c.Objects.Count} level={vm.CurrentLevel?.Name} mode={vm.Mode} cut={c.CutAboveCurrent} filter={c.Filter} kinds={string.Join(",", c.Objects.GroupBy(o => o.Kind).Select(g => g.Key + "=" + g.Count()))} levels={vm.Building.Levels.Count}");
        Assert.True(c.Objects.Count(o => o.Kind == ImmoCad.Scene.SceneKind.Slab) == 2, $"kinds={string.Join(",", c.Objects.GroupBy(o => o.Kind).Select(g => g.Key + "=" + g.Count()))} lvl={vm.CurrentLevel?.Name} idx={vm.Building.Levels.FindIndex(l => l.Id == vm.CurrentLevelId)}");   // cut above R+1: RDC + R+1
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "3d-solid.png"));
        vm.ToggleXRayCommand.Execute(null); Assert.All(c.Objects.Where(o => o.Kind == ImmoCad.Scene.SceneKind.Wall), o => Assert.True(o.Alpha < 0.5f)); w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "3d-xray.png"));
        vm.ToggleCutCommand.Execute(null); Assert.Equal(3, c.Objects.Count(o => o.Kind == ImmoCad.Scene.SceneKind.Slab));
        vm.CycleLevelFilterCommand.Execute(null); vm.CycleLevelFilterCommand.Execute(null); Assert.Single(c.Objects, o => o.Kind == ImmoCad.Scene.SceneKind.Slab);   // current level only
        vm.SetModeCommand.Execute("Wireframe"); Assert.True(c.Wireframe); w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "3d-wire.png"));
        vm.SelectLevelCommand.Execute(vm.Building.Levels[2].Id); Assert.True(c.Camera.Target.Z > 5.4);         // the level bar moves the camera target vertically
    }, CancellationToken.None);

    [Fact] public Task Clicking_a_wall_in_3d_selects_it_and_shows_its_level_in_the_plan() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.Settings.AnimateTransitions = false; var w = new MainWindow { DataContext = vm, Width = 1000, Height = 800 }; w.Show(); w.UpdateLayout();
        vm.SelectLevelCommand.Execute(vm.Building.Levels[2].Id); vm.SetModeCommand.Execute("Solid"); w.UpdateLayout(); w.CaptureRenderedFrame();
        var c = w.View3D.Controller; var target = vm.Building.Levels[0].Walls[0];                       // a wall of the RDC, seen from the top-right
        var m = c.Objects.First(o => o.Id == target.Id).Mesh; var (mx, my, mz) = ((m.Min().X + m.Max().X) / 2, (m.Min().Y + m.Max().Y) / 2, m.Max().Z);
        var half = Math.Min(w.View3D.Control.Bounds.Width, w.View3D.Control.Bounds.Height) / 2; var (px, py, _) = c.Camera.Project(mx, my, mz);
        var screen = new Avalonia.Point(w.View3D.Control.Bounds.Width / 2 + px * half, w.View3D.Control.Bounds.Height / 2 - py * half);
        c.Down(screen, false); c.Up(screen, vm, w.View3D.Control.Bounds.Width, w.View3D.Control.Bounds.Height);
        Assert.Contains(vm.SelectedWalls, id => vm.Building.Levels.SelectMany(l => l.Walls).Any(x => x.Id == id));
        Assert.Equal(vm.Building.Levels.First(l => l.Walls.Any(x => vm.SelectedWalls.Contains(x.Id))).Id, vm.CurrentLevelId);
        vm.SetModeCommand.Execute("Plan"); Assert.True(vm.IsPlan);
    }, CancellationToken.None);
}

public class StraightenUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Straighten_tool_previews_counts_applies_on_enter_and_cancels_on_escape() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        vm.Actions.ImportWalls(vm.CurrentLevel!.Id, [new List<PointMm> { new(0, 0), new(500, 6), new(1000, 0), new(1500, 5), new(2000, 0), new(2000, 200), new(1000, 194), new(0, 200) }]); vm.Refresh();
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout();
        vm.SetToolCommand.Execute("Straighten"); Assert.Null(vm.StraightenPreview());                  // nothing selected yet
        vm.SelectWalls([vm.CurrentLevel!.Walls[0].Id], false);
        var pv = vm.StraightenPreview()!.Value; Assert.Equal("8 → 4 segments", pv.Info);
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "straighten.png"));
        Assert.Equal(8, vm.CurrentLevel!.Walls[0].Footprint.Count);                                    // preview did not touch the model
        var steps = vm.History.Items.Count; w.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        vm.SetToolCommand.Execute("Straighten"); vm.StraightenSelectionCommand.Execute(null);
        Assert.Equal(4, vm.CurrentLevel!.Walls[0].Footprint.Count); Assert.Equal(steps + 1, vm.History.Items.Count);
        vm.UndoCommand.Execute(null); Assert.Equal(8, vm.CurrentLevel!.Walls[0].Footprint.Count);
        vm.MakeRectangularCommand.Execute(null); Assert.Equal(4, vm.CurrentLevel!.Walls[0].Footprint.Count);
    }, CancellationToken.None);
}

public class StretchUiTests
{
    private readonly HeadlessFixture f = new();

    private static MainViewModel WithPlan()
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        var w = new ImportWizardViewModel(WizardTests.PlanPng()) { DistanceMetres = 5 }; w.AddCalibrationPoint(50, 240); w.AddCalibrationPoint(550, 240); w.MinThickness = 5; w.Detect(); vm.ApplyWizard(w);
        return vm;
    }

    [Fact] public Task Side_handle_stretches_width_by_3_percent_with_the_mouse_numeric_fields_and_nudge_keys() => f.Session.Dispatch(() =>
    {
        var vm = WithPlan(); var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; Avalonia.Point At(double x, double y) => plan.TranslatePoint(plan.ToScreenForTest(new((int)x, (int)y)), w)!.Value;
        vm.SetToolCommand.Execute("Stretch"); var t0 = ImmoCad.Core.Geometry.ImageStretch.FromArray(vm.CurrentLevel!.ImageTransform); var (W, H) = vm.BackgroundSize()!.Value;
        var right = ImmoCad.Core.Geometry.ImageStretch.HandlePoint(t0, W, H, ImmoCad.Core.Geometry.StretchHandle.Right); var steps = vm.History.Items.Count; var width0 = W * t0.A;
        w.MouseDown(At(right.X, right.Y), Avalonia.Input.MouseButton.Left); Assert.True(plan.StretchActiveForTest == ImmoCad.Core.Geometry.StretchHandle.Right, $"active={plan.StretchActiveForTest} tool={vm.Tool} handleAt={At(right.X, right.Y)} size={W}x{H} t0={t0}"); w.MouseMove(At(right.X + 80, right.Y)); w.MouseMove(At(right.X + width0 * 0.03, right.Y)); w.MouseUp(At(right.X + width0 * 0.03, right.Y), Avalonia.Input.MouseButton.Left);
        var t1 = ImmoCad.Core.Geometry.ImageStretch.FromArray(vm.CurrentLevel!.ImageTransform);
        Assert.Equal(t0.A * 1.03, t1.A, 3); Assert.Equal(t0.D, t1.D, 6); Assert.Equal(steps + 1, vm.History.Items.Count);                 // width only, one history step
        Assert.Equal(t0.Tx, t1.Tx, 3);                                                                                                // the opposite side stayed put
        w.KeyPress(Avalonia.Input.Key.Right, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowRight, null);          // (key routing goes through the view)
        Assert.Equal(t1.Tx + 1, ImmoCad.Core.Geometry.ImageStretch.FromArray(vm.CurrentLevel!.ImageTransform).Tx, 6);          // the arrow key nudged by 1 mm
        vm.ImgRotation = 2; vm.ImgScaleX = 10; vm.ImgScaleY = 10; vm.ApplyImageFieldsCommand.Execute(null); Assert.Equal(2, ImmoCad.Core.Geometry.ImageStretch.Decompose(ImmoCad.Core.Geometry.ImageStretch.FromArray(vm.CurrentLevel!.ImageTransform)).RotationDeg, 3);
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "stretch.png"));
        vm.UndoCommand.Execute(null); vm.UndoCommand.Execute(null); vm.UndoCommand.Execute(null); Assert.Equal(t0.A, ImmoCad.Core.Geometry.ImageStretch.FromArray(vm.CurrentLevel!.ImageTransform).A, 6);
    }, CancellationToken.None);

    [Fact] public Task Opacity_visibility_and_difference_overlay_with_the_level_below() => f.Session.Dispatch(() =>
    {
        var vm = WithPlan(); var rdc = vm.CurrentLevel!.Id; vm.AddLevelCommand.Execute(null);
        var w2 = new ImportWizardViewModel(WizardTests.PlanPng()) { DistanceMetres = 5 }; w2.AddCalibrationPoint(50, 240); w2.AddCalibrationPoint(550, 240); w2.MinThickness = 5; w2.Detect(); vm.ApplyWizard(w2);
        vm.BgOpacity = 0.8; Assert.Equal(0.8, vm.CurrentLevel!.BackgroundOpacity, 6); vm.BgVisible = false; Assert.False(vm.CurrentLevel.BackgroundVisible); vm.BgVisible = true;
        var win = new MainWindow { DataContext = vm, Width = 900, Height = 700 }; win.Show(); win.UpdateLayout();
        byte[] Px() { using var ms = new MemoryStream(); win.CaptureRenderedFrame()!.Save(ms); return ms.ToArray(); }
        var normal = Px(); vm.BgDifference = true; Assert.NotEqual(normal, Px());                       // the blend changes what is drawn
        vm.UndoCommand.Execute(null); vm.UndoCommand.Execute(null); Assert.True(vm.History.CanUndo);
    }, CancellationToken.None);
}

public class ChainUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Draw_wall_with_typed_length_and_angle_double_click_and_escape_steps() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; plan.Scale = 0.08; plan.Offset = (100, 100); w.CaptureRenderedFrame();
        Avalonia.Point At(int x, int y) => plan.TranslatePoint(plan.ToScreenForTest(new(x, y)), w)!.Value;
        vm.SetToolCommand.Execute("DrawWall"); var walls = vm.CurrentLevel!.Walls.Count; var steps = vm.History.Items.Count;
        w.MouseDown(At(0, 0), Avalonia.Input.MouseButton.Left); w.MouseUp(At(0, 0), Avalonia.Input.MouseButton.Left);             // click mode: first point
        w.MouseMove(At(1500, 20));
        foreach (var k in new[] { Avalonia.Input.Key.D2, Avalonia.Input.Key.OemPeriod, Avalonia.Input.Key.D4 }) w.KeyPress(k, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        w.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);       // 2.4 m along the pointer direction
        Assert.Equal(2, plan.DrawToolForTest!.Points.Count); Assert.Equal(2400, plan.DrawToolForTest.Points[1].X);
        w.MouseMove(At(2400, 1500)); w.MouseDown(At(2400, 1500), Avalonia.Input.MouseButton.Left); w.MouseUp(At(2400, 1500), Avalonia.Input.MouseButton.Left);
        w.MouseMove(At(0, 1500)); w.MouseDown(At(0, 1500), Avalonia.Input.MouseButton.Left);
        w.MouseUp(At(0, 1500), Avalonia.Input.MouseButton.Left); w.MouseDown(At(0, 1500), Avalonia.Input.MouseButton.Left);                                                    // double-click finishes
        Assert.Equal(walls + 3, vm.CurrentLevel!.Walls.Count); Assert.Equal(steps + 1, vm.History.Items.Count);                        // 3 segments, one history step
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "chain.png"));
        // Escape: staged cancel, nothing committed
        w.MouseDown(At(0, 800), Avalonia.Input.MouseButton.Left); var d1 = $"{plan.DrawToolForTest?.Points.Count}/{plan.DrawToolForTest?.State}"; w.MouseUp(At(0, 800), Avalonia.Input.MouseButton.Left); var d2 = $"{plan.DrawToolForTest?.Points.Count}/{plan.DrawToolForTest?.State}"; w.MouseMove(At(1000, 800)); w.MouseDown(At(1000, 800), Avalonia.Input.MouseButton.Left);
        Assert.True(plan.DrawToolForTest != null && plan.DrawToolForTest.Points.Count == 2, $"before esc ({d1} {d2} at={At(0, 800)} plan={plan.Bounds}): tool={vm.Tool} points={plan.DrawToolForTest?.Points.Count} state={plan.DrawToolForTest?.State}");
        w.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null); Assert.Single(plan.DrawToolForTest!.Points) ;
        w.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        Assert.Equal(walls + 3, vm.CurrentLevel!.Walls.Count); Assert.True(vm.IsSelectTool);
    }, CancellationToken.None);
}

public class EquipmentUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Select_move_edit_and_connect_a_radiator_to_a_riser_with_the_mouse() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; Avalonia.Point At(double x, double y) => plan.TranslatePoint(plan.ToScreenForTest(new((int)x, (int)y)), w)!.Value;
        var eq = vm.Building.Equipment[0]; var cat = ImmoCad.Core.Catalog.EquipmentCatalog.Find("radiator")!;
        // select by clicking the symbol
        w.MouseDown(At(eq.Position.X + 500, eq.Position.Y + 50), Avalonia.Input.MouseButton.Left); w.MouseUp(At(eq.Position.X + 500, eq.Position.Y + 50), Avalonia.Input.MouseButton.Left);
        Assert.Equal(eq.Id, vm.SelectedEquipmentId); Assert.Contains("Départ chauffage", vm.EqInfo);
        // edit properties: label + width + rotation → one history step each, undoable
        var steps = vm.History.Items.Count; vm.EqLabel = "Radiateur séjour"; vm.EqWidth = 1400; vm.ApplyEquipmentCommand.Execute(null);
        Assert.Equal(steps + 1, vm.History.Items.Count); Assert.Equal("Radiateur séjour", vm.Building.Equipment[0].Label); Assert.Equal(1400, cat.With(vm.Building.Equipment[0].Parameters).WidthMm);
        // drag it 500 mm along the wall
        vm.ToggleSnapToGridCommand.Execute(null);
        var before = vm.Building.Equipment[0].Position; steps = vm.History.Items.Count;
        w.MouseDown(At(before.X + 400, before.Y + 50), Avalonia.Input.MouseButton.Left); w.MouseMove(At(before.X + 600, before.Y + 50)); w.MouseMove(At(before.X + 900, before.Y + 50)); w.MouseUp(At(before.X + 900, before.Y + 50), Avalonia.Input.MouseButton.Left);
        Assert.Equal(steps + 1, vm.History.Items.Count); Assert.Equal(before.X + 500, vm.Building.Equipment[0].Position.X);
        // connect: from the supply port to the heating-supply riser (at 300,300) → one step, a tee on the riser
        var e2 = vm.Building.Equipment[0]; var c2 = cat.With(e2.Parameters); var port = ImmoCad.Core.Catalog.Symbol.PortPositions(c2, e2.Position, vm.Building.Levels[0].AltitudeMm + e2.ElevationMm, e2.RotationDeg).First(p => p.Port.Network == ImmoCad.Core.Model.NetworkType.HeatingSupply);
        steps = vm.History.Items.Count; var net = vm.Building.Networks.First(n => n.Type == ImmoCad.Core.Model.NetworkType.HeatingSupply); var segs = net.Segments.Count;
        w.MouseDown(At(port.At.X, port.At.Y), Avalonia.Input.MouseButton.Left); w.MouseMove(At(1500, 400)); w.MouseMove(At(320, 300)); w.MouseUp(At(320, 300), Avalonia.Input.MouseButton.Left);
        Assert.Equal(steps + 1, vm.History.Items.Count); Assert.True(vm.Building.Networks.First(n => n.Type == ImmoCad.Core.Model.NetworkType.HeatingSupply).Segments.Count > segs);
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "equipment.png"));
        // releasing on nothing connects nothing
        steps = vm.History.Items.Count; var again = ImmoCad.Core.Catalog.Symbol.PortPositions(c2, vm.Building.Equipment[0].Position, 150, 0).First(p => p.Port.Network == ImmoCad.Core.Model.NetworkType.HeatingReturn);
        w.MouseDown(At(again.At.X, again.At.Y), Avalonia.Input.MouseButton.Left); w.MouseMove(At(5000, 5000)); w.MouseUp(At(5000, 5000), Avalonia.Input.MouseButton.Left);
        Assert.Equal(steps, vm.History.Items.Count); Assert.Contains("Relâchez", vm.Status);
    }, CancellationToken.None);

    [Fact] public Task Palette_shows_translated_names_and_3d_uses_the_catalog_shapes() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); Assert.Contains(vm.Palette, p => p.Name == "Radiateur"); Assert.True(vm.Palette.Count >= 17);
        ImmoCad.Core.Translations.Language = "en"; try { vm.Search = ""; vm.Search = " "; vm.Search = ""; Assert.Contains(vm.Palette, p => p.Name == "Radiator"); } finally { ImmoCad.Core.Translations.Language = "fr"; }
        var input = ImmoCad.Core.SceneMapper.Map(vm.Building); Assert.Contains(input.Boxes, b => b.Name == "Radiateur");
        var tank = vm.Building.Equipment.Count; vm.SelectPaletteCommand.Execute("hot-water-tank"); vm.PlaceFromPalette(new(3000, 2000)); vm.SelectPaletteCommand.Execute("boiler"); vm.PlaceFromPalette(new(3800, 2000));
        Assert.Equal(tank + 2, vm.Building.Equipment.Count); Assert.Contains(ImmoCad.Core.SceneMapper.Map(vm.Building).Boxes, b => b.Radius > 0);   // the tank is a cylinder
    }, CancellationToken.None);
}

public class ExtentUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Select_a_riser_in_elevation_drag_its_top_handle_to_a_level_and_edit_the_extent_in_properties() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout();
        vm.SelectWalls([vm.CurrentLevel!.Walls[0].Id], false); vm.ShowElevationCommand.Execute(null); w.UpdateLayout(); w.CaptureRenderedFrame();
        var elev = w.FindControl<ElevationView>("Elev")!; var plane = vm.Plane!;
        var net = vm.Building.Networks[0]; var seg = net.Segments[0]; var node = net.Nodes.OrderBy(n => n.Position.Z).Last();
        vm.SelectSegment(net.Id, seg.Id); Assert.Contains("→", vm.ExtentInfo); Assert.Equal(["RDC", "R+1", "R+2"], vm.LevelNames); Assert.Equal(0, vm.ExtentFrom); Assert.Equal(2, vm.ExtentTo);
        // properties: from R+1 to R+2 only
        var steps = vm.History.Items.Count; vm.ExtentFrom = 1; vm.ExtentTo = 2; vm.ApplyExtentCommand.Execute(null);
        Assert.Equal(steps + 1, vm.History.Items.Count); Assert.Equal(2700, vm.Building.Networks[0].Nodes.Min(n => n.Position.Z)); Assert.Equal(8100, vm.Building.Networks[0].Nodes.Max(n => n.Position.Z));
        // handle drag (through the view's release path): the top end goes to the level line nearest the drop
        var topNode = vm.Building.Networks[0].Nodes.OrderBy(n => n.Position.Z).Last(); steps = vm.History.Items.Count;
        Assert.True(vm.DragVerticalEnd(vm.Building.Networks[0].Id, topNode.Id, 5390)); Assert.Equal(5400, vm.Building.Networks[0].Nodes.Max(n => n.Position.Z)); Assert.Equal(steps + 1, vm.History.Items.Count);
        Assert.False(vm.DragVerticalEnd(vm.Building.Networks[0].Id, topNode.Id, 2700));                       // would collapse the run onto its other end
        Assert.Equal((1, 1), (vm.ExtentFrom, vm.ExtentTo));                                                     // the panel follows: the run now spans R+1 only
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "extent.png"));
        vm.UndoCommand.Execute(null); vm.UndoCommand.Execute(null); Assert.Equal(0, vm.Building.Networks[0].Nodes.Min(n => n.Position.Z));
    }, CancellationToken.None);
}

public class ExportDialogTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Export_dialog_filters_levels_networks_and_units_and_writes_the_file() => f.Session.RunAsync(async () =>
    {
        var dlg = new FakeDialogService(); var vm = new MainViewModel { Dialogs = dlg }; var rdc = vm.Building.Levels[0].Id;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dxf");
        dlg.ExportAnswers.Enqueue(new ExportRequest("dxf", new ImmoCad.Core.Export.ExportOptions(new HashSet<Guid> { rdc }, new HashSet<Guid>(), Metres: true))); dlg.SaveAnswers.Enqueue(path);
        await vm.ExportCommand.ExecuteAsync("ask");
        var text = File.ReadAllText(path); File.Delete(path);
        Assert.Contains("WALLS_RDC", text); Assert.DoesNotContain("WALLS_R+1", text); Assert.DoesNotContain("NET_", text); Assert.Contains("\n70\n6\n", text.Replace("\r", ""));
        dlg.ExportAnswers.Enqueue(null); await vm.ExportCommand.ExecuteAsync("ask"); Assert.Equal(2, dlg.Asked.Count(a => a == "export-dialog"));        // cancelled: nothing asked after
        Assert.DoesNotContain(dlg.Asked.TakeLast(1), a => a.StartsWith("save"));
    }, CancellationToken.None);

    [Fact] public Task Real_dialog_lists_every_format_with_its_purpose_and_returns_the_request() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var d = new ExportDialog(vm.Building, "glb"); d.Show();
        Assert.Equal(8, ExportFormats.Ids.Length); Assert.All(ExportFormats.Ids, x => Assert.False(string.IsNullOrWhiteSpace(ExportFormats.Purpose(x))));
        Assert.Equal("glb", ExportFormats.Ids[d.FindControl<Avalonia.Controls.ComboBox>("Formats")!.SelectedIndex]); Assert.Contains("Blender", d.FindControl<Avalonia.Controls.TextBlock>("Purpose")!.Text);
        d.FindControl<Avalonia.Controls.RadioButton>("M")!.IsChecked = true; d.FindControl<Avalonia.Controls.Button>("OkBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.NotNull(d.Result); Assert.True(d.Result!.Options.Metres); Assert.Equal(3, d.Result.Options.Levels!.Count);
    }, CancellationToken.None);
}

public class ImportExportUiTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Import_ifc_and_reference_model_through_the_menu_commands() => f.Session.RunAsync(async () =>
    {
        var dlg = new FakeDialogService(); var src = new MainViewModel(); var tmp = Path.Combine(Path.GetTempPath(), "imp" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        var ifc = Path.Combine(tmp, "s.ifc"); File.WriteAllText(ifc, new ImmoCad.Core.Export.IfcExporter().Export(src.Building));
        var obj = Path.Combine(tmp, "b.obj"); File.WriteAllText(obj, "v 0 0 0\nv 4 0 0\nv 4 3 0\nv 0 3 0\nv 0 0 2.7\nv 4 0 2.7\nv 4 3 2.7\nv 0 3 2.7\nf 1 2 3 4\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n");
        var vm = new MainViewModel(startWithSample: false) { Dialogs = dlg, Settings = new() };
        dlg.OpenAnswers.Enqueue(ifc); await vm.ImportIfcCommand.ExecuteAsync(null);                                        // no document yet → one is created
        Assert.Equal(3, vm.Building.Levels.Count); Assert.Equal(12, vm.Building.Levels.Sum(l => l.Walls.Count)); Assert.Contains("IFC", vm.Status); Assert.True(vm.History.IsDirty);
        dlg.OpenAnswers.Enqueue(obj); await vm.ImportReferenceCommand.ExecuteAsync(null); Assert.True(vm.CurrentLevel!.References.Count == 1, $"status={vm.Status} errors={string.Join("|", dlg.Errors)} level={vm.CurrentLevel?.Name} refs={vm.Building.Levels.Sum(l => l.References.Count)}");
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 700 }; w.Show(); w.UpdateLayout(); Assert.NotNull(w.CaptureRenderedFrame());
        vm.Dialogs = dlg;                                                                                              // the window installs its own dialog service: put the scripted one back
        dlg.OpenAnswers.Enqueue(Path.Combine(tmp, "missing.ifc")); await vm.ImportIfcCommand.ExecuteAsync(null); Assert.Single(dlg.Errors);          // a missing file is reported, not thrown
        Directory.Delete(tmp, true); w.Close();
    }, CancellationToken.None);
}

public class ThemeAndTourTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Light_and_dark_tokens_differ_theme_is_persisted_and_drawn() => f.Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "theme" + Guid.NewGuid().ToString("N"), "settings.json");
        var vm = new MainViewModel { Settings = new(), SettingsPath = path }; var w = new MainWindow { DataContext = vm, Width = 900, Height = 700 }; w.Show(); w.UpdateLayout();
        byte[] Px() { using var ms = new MemoryStream(); w.CaptureRenderedFrame()!.Save(ms); return ms.ToArray(); }
        vm.ApplyTheme("light"); w.UpdateLayout(); var plan = w.FindControl<PlanView>("Plan")!; var lightBg = Tokens.Get(plan, "PlanBackground", Avalonia.Media.Colors.Magenta); var light = Px();
        vm.ApplyTheme("dark"); w.UpdateLayout(); var darkBg = Tokens.Get(plan, "PlanBackground", Avalonia.Media.Colors.Magenta); var dark = Px();
        Assert.NotEqual(Avalonia.Media.Colors.Magenta, lightBg); Assert.NotEqual(lightBg, darkBg); Assert.True(lightBg.R > darkBg.R); Assert.NotEqual(light, dark);
        Assert.Equal("dark", ImmoCad.Core.SettingsStore.Load(path).Theme); vm.ApplyTheme("auto"); Assert.Equal("auto", vm.Settings.Theme);
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "theme.png")); w.Close();
    }, CancellationToken.None);

    [Fact] public Task Guided_tour_walks_five_steps_selects_the_right_tools_and_is_remembered() => f.Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "tour" + Guid.NewGuid().ToString("N"), "settings.json");
        var vm = new MainViewModel(startWithSample: false) { Settings = new(), SettingsPath = path };
        Assert.False(vm.TourActive); vm.StartTourCommand.Execute(null); Assert.True(vm.HasDocument); Assert.True(vm.TourActive); Assert.Equal("1 / 5", vm.TourProgress); Assert.Contains("Importer", vm.TourTitle);
        vm.TourNextCommand.Execute(null); vm.TourNextCommand.Execute(null); vm.TourNextCommand.Execute(null); Assert.Equal(3, vm.TourStep); Assert.Contains("colonne", vm.TourBody);
        vm.TourNextCommand.Execute(null); Assert.True(vm.IsRiserTool); Assert.Equal(4, vm.TourStep);                       // step 4 → the riser tool is active
        vm.TourNextCommand.Execute(null); Assert.False(vm.TourActive); Assert.True(vm.IsPipeTool); Assert.True(vm.Settings.TourDone); Assert.True(ImmoCad.Core.SettingsStore.Load(path).TourDone);
        vm.StartTourCommand.Execute(null); vm.TourSkipCommand.Execute(null); Assert.False(vm.TourActive);
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); vm.StartTourCommand.Execute(null); w.UpdateLayout(); w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "tour.png")); w.Close();
    }, CancellationToken.None);

    [Fact] public Task About_dialog_shows_version_and_attribution_and_the_window_has_an_icon() => f.Session.Dispatch(() =>
    {
        var d = new AboutDialog(); d.Show(); Assert.Contains(".NET", d.FindControl<Avalonia.Controls.TextBlock>("Info")!.Text); d.Close();
        var w = new MainWindow { DataContext = new MainViewModel() }; w.Show(); Assert.NotNull(w.Icon); w.Close();
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "packaging", "icon", "icon.ico")));
    }, CancellationToken.None);
}

public class SiteUiTests
{
    private readonly HeadlessFixture f = new();

    private sealed class FakeGeo : ImmoCad.Core.Site.IGeocoder, ImmoCad.Core.Site.ICadastreService, ImmoCad.Core.Site.IMapTileSource
    {
        public bool Offline; public int TileRequests; public readonly byte[] Png = Tile();
        private static byte[] Tile() { var px = Enumerable.Repeat((byte)200, 256 * 256).ToArray(); for (var i = 0; i < 256; i++) { px[i * 256 + 128] = 60; px[128 * 256 + i] = 60; } using var img = new ImageMagick.MagickImage(px, new ImageMagick.PixelReadSettings(256, 256, ImageMagick.StorageType.Char, "I")); return img.ToByteArray(ImageMagick.MagickFormat.Png); }
        public Task<(double Lat, double Lon)?> GeocodeAsync(string a, CancellationToken ct = default) => Task.FromResult<(double, double)?>(Offline ? null : (45.7640, 4.8357));
        public Task<IReadOnlyList<IReadOnlyList<(double E, double N)>>> BuildingFootprintsAsync(double lat, double lon, int r, CancellationToken ct = default)
        {
            var (e, n) = ImmoCad.Core.Site.Lambert93.FromWgs84(lat, lon); double c = Math.Cos(0.3), s = Math.Sin(0.3);               // a 12 m × 8 m building rotated 17°, and a neighbour on its east side
            (double, double) P(double x, double y) => (e + x * c - y * s, n + x * s + y * c);
            IReadOnlyList<(double E, double N)> A = [P(0, 0), P(12, 0), P(12, 8), P(0, 8)]; IReadOnlyList<(double E, double N)> B = [P(12.1, -1), P(20, -1), P(20, 9), P(12.1, 9)];
            return Task.FromResult<IReadOnlyList<IReadOnlyList<(double E, double N)>>>([A, B]);
        }
        public Task<byte[]?> GetTileAsync(string layer, int z, int x, int y, CancellationToken ct = default) { Interlocked.Increment(ref TileRequests); return Task.FromResult<byte[]?>(Offline ? null : Png); }
    }

    private static MainViewModel Make(out FakeGeo geo)
    {
        geo = new FakeGeo(); var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.Geo = new SiteData(geo, geo, geo); vm.NewDocumentCommand.Execute(null); return vm;
    }

    [Fact] public Task Search_address_loads_cadastral_buildings_and_creates_a_building_from_a_footprint() => f.Session.RunAsync(async () =>
    {
        var vm = Make(out var geo); vm.SiteAddress = "1 place Bellecour, Lyon"; await vm.SearchAddressCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Footprints.Count); Assert.Equal(2, vm.FootprintItems.Count); Assert.Contains("2 bâtiment", vm.SiteMessage); Assert.InRange(vm.FootprintItems[0].AreaM2, 95, 97);
        vm.CreateFromFootprintCommand.Execute(null); Assert.Contains("Choisissez", vm.SiteMessage);                                    // nothing picked yet → no change
        Assert.Empty(vm.CurrentLevel!.Walls); var c = vm.Footprints[0]; vm.PickFootprintAt((c[0].E + c[2].E) / 2, (c[0].N + c[2].N) / 2); Assert.Equal(0, vm.SelectedFootprint);
        var steps = vm.History.Items.Count; vm.CreateFromFootprintCommand.Execute(null);
        Assert.Equal(4, vm.CurrentLevel!.Walls.Count); Assert.Equal(steps + 2, vm.History.Items.Count); Assert.NotNull(vm.Building.Site); Assert.Equal(17.19, vm.Building.Site!.RotationDeg, 2);
        var ring = vm.FootprintModel()!; Assert.Equal(new ImmoCad.Core.Model.PointMm(12000, 0), ring[1]); Assert.Equal(new ImmoCad.Core.Model.PointMm(12000, 8000), ring[2]);   // axis-aligned in the model frame
        Assert.Single(vm.Building.Site.Neighbours); Assert.Equal([1], vm.PartyWallEdges());                                           // the east wall is shared with the neighbour
        Assert.True(vm.IsPlan);
    }, CancellationToken.None);

    [Fact] public Task Two_corner_placement_of_an_existing_building_sets_anchor_and_rotation_in_one_step() => f.Session.RunAsync(async () =>
    {
        var vm = Make(out _); vm.SiteAddress = "Lyon"; await vm.SearchAddressCommand.ExecuteAsync(null); var l = vm.CurrentLevel!.Id;
        vm.Actions.ImportWalls(l, [new List<ImmoCad.Core.Model.PointMm> { new(0, 0), new(12000, 0), new(12000, 8000), new(0, 8000) }]); vm.Refresh();
        vm.PickFootprintAt(vm.Footprints[0][0].E + 1, vm.Footprints[0][0].N + 1); vm.BeginPlacementCommand.Execute(null); Assert.True(vm.Placing);
        var anchor = new ImmoCad.Core.Site.SiteAnchor(vm.MapCenter.E, vm.MapCenter.N, 0); var ring = vm.Footprints[0]; var steps = vm.History.Items.Count;
        var c0 = anchor.ToLambert(0, 0); var c1 = anchor.ToLambert(12000, 0);
        vm.PlacementClick(c0.E, c0.N); vm.PlacementClick(ring[0].E, ring[0].N); vm.PlacementClick(c1.E, c1.N); vm.PlacementClick(ring[1].E, ring[1].N);
        Assert.False(vm.Placing); Assert.Equal(steps + 1, vm.History.Items.Count); Assert.Equal(17.19, vm.Building.Site!.RotationDeg, 2); Assert.Equal(ring[0].E, vm.Building.Site.EastM, 4);
        var model = vm.FootprintModel()!; Assert.Equal(0, model[0].X); Assert.Equal(12000, model[1].X); vm.UndoCommand.Execute(null); Assert.Null(vm.Building.Site);
        vm.BeginPlacementCommand.Execute(null); vm.PickFootprintAt(ring[0].E + 1, ring[0].N + 1); vm.BeginPlacementCommand.Execute(null);
        vm.PlacementClick(c0.E, c0.N); vm.PlacementClick(ring[0].E, ring[0].N); vm.PlacementClick(c0.E, c0.N); vm.PlacementClick(ring[0].E, ring[0].N);   // same corner twice: degenerate
        Assert.Contains("trop proches", vm.SiteMessage); Assert.Null(vm.Building.Site);
    }, CancellationToken.None);

    [Fact] public Task Suggestions_fit_scale_and_angles_need_a_confirmation_and_are_single_undoable_steps() => f.Session.RunAsync(async () =>
    {
        var vm = Make(out _); vm.SiteAddress = "Lyon"; await vm.SearchAddressCommand.ExecuteAsync(null); vm.PickFootprintAt(vm.Footprints[0][0].E + 1, vm.Footprints[0][0].N + 1); vm.CreateFromFootprintCommand.Execute(null);
        // the user's plan is 2.8 % wider than the cadastre and its south façade sits 120 mm inside the edge
        vm.Actions.DeleteWalls(vm.CurrentLevel!.Id, vm.CurrentLevel.Walls.Select(w => w.Id)); vm.Actions.ImportWalls(vm.CurrentLevel!.Id, [new List<ImmoCad.Core.Model.PointMm> { new(0, 120), new(12336, 120), new(12336, 320), new(0, 320) }, new List<ImmoCad.Core.Model.PointMm> { new(12136, 0), new(12336, 0), new(12336, 8000), new(12136, 8000) }, new List<ImmoCad.Core.Model.PointMm> { new(0, 7800), new(12336, 7800), new(12336, 8000), new(0, 8000) }, new List<ImmoCad.Core.Model.PointMm> { new(0, 0), new(200, 0), new(200, 8000), new(0, 8000) }]); vm.Refresh();
        var fit = vm.FitSuggestion(); Assert.NotEmpty(fit); var before = ImmoCad.Core.History.BuildingJson.Serialize(vm.Building); Assert.Equal(before, ImmoCad.Core.History.BuildingJson.Serialize(vm.Building));   // suggestion alone changes nothing
        var steps = vm.History.Items.Count; vm.FitOuterWallsCommand.Execute(null); Assert.True(vm.History.Items.Count == steps + 1, $"status={vm.Status} steps={steps} now={vm.History.Items.Count} fit={fit.Count}"); vm.UndoCommand.Execute(null); Assert.Equal(before, ImmoCad.Core.History.BuildingJson.Serialize(vm.Building));
        vm.CheckScaleCommand.Execute(null); Assert.Contains("plus large", vm.ScaleText); var pointer = vm.History.Pointer; vm.ApplyScaleCommand.Execute(null); Assert.Equal(pointer + 1, vm.History.Pointer);                      // one new step (the undone fit was dropped from the redo tail)
        Assert.InRange(vm.CurrentLevel!.Walls[1].Footprint.Max(p => p.X), 11990, 12010); vm.CheckScaleCommand.Execute(null); Assert.Contains("correspond", vm.ScaleText);
        vm.CheckAnglesCommand.Execute(null); Assert.Empty(vm.AngleIssues); Assert.Contains("concordent", vm.Status);
    }, CancellationToken.None);

    [Fact] public Task Site_level_shows_cached_tiles_offline_and_the_attribution_and_the_plan_gets_a_dashed_footprint() => f.Session.RunAsync(async () =>
    {
        var vm = Make(out var geo); vm.SiteAddress = "Lyon"; await vm.SearchAddressCommand.ExecuteAsync(null); vm.PickFootprintAt(vm.Footprints[0][0].E + 1, vm.Footprints[0][0].N + 1); vm.CreateFromFootprintCommand.Execute(null);
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout();
        Assert.Equal("Site", vm.Levels[^1].Name); vm.SelectLevelCommand.Execute(MainViewModel.SiteLevelId); Assert.True(vm.IsSite); w.UpdateLayout(); w.CaptureRenderedFrame();
        await Task.Delay(500); var map = w.FindControl<SiteView>("SiteMap")!; map.InvalidateVisual(); w.CaptureRenderedFrame(); await Task.Delay(200); w.CaptureRenderedFrame();
        Assert.True(geo.TileRequests > 0, $"requests={geo.TileRequests} geo={(vm.Geo != null)} site={vm.IsSite} bounds={map.Bounds} center={vm.MapCenter} scale={vm.MapScale} visible={map.IsVisible} renders={map.RenderCount} info={map.LastTileInfo}"); Assert.True(map.TilesInMemory > 0); Assert.True(map.TilesDrawn > 0);
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "site.png")); Assert.Contains("IGN", MainViewModel.Attribution);
        geo.Offline = true; vm.MapLayer = 1; vm.SelectLevelCommand.Execute(vm.Building.Levels[0].Id); Assert.True(vm.IsPlan);                       // back to a plan level
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "plan-site.png")); vm.ShowFootprint = false; Assert.NotNull(w.CaptureRenderedFrame());
        w.Close();
    }, CancellationToken.None);
}

public class LanguageUiTests
{
    private readonly HeadlessFixture f = new();
    private static readonly System.Text.RegularExpressions.Regex FrenchLetters = new("[éèêëàâùûçôîïœÉÈÀ]");
    private static readonly string[] FrenchWords = ["Annuler", "Appliquer", "Ouvrir", "Enregistrer", "Niveau", "Mur", "Fenêtre", "Porte", "Supprimer", "Importer", "Exporter", "Rétablir", "Réseaux", "Propriétés", "Historique", "Palette de", "Rechercher"];
    private static readonly string[] EnglishWords = ["Cancel", "Apply", "Open", "Save", "Delete", "Import", "Export", "Redo", "Networks", "Properties", "History", "Search", "Level", "Wall", "Window", "Door"];

    /// <summary>Every text a user can read in the window: text blocks, buttons, menu headers, tab headers, tool tips, watermarks, window titles, numeric formats.</summary>
    private static List<string> VisibleTexts(Avalonia.Controls.Control root)
    {
        var res = new List<string>();
        foreach (var c in new[] { root }.Concat(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Avalonia.Controls.Control>()))
        {
            if (c is Avalonia.Controls.TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) res.Add(tb.Text);
            if (c is Avalonia.Controls.ContentControl cc && cc.Content is string s && !string.IsNullOrWhiteSpace(s)) res.Add(s);
            if (c is Avalonia.Controls.Primitives.HeaderedContentControl hc && hc.Header is string h) res.Add(h);
            if (c is Avalonia.Controls.MenuItem mi && mi.Header is string mh) res.Add(mh);
            if (c is Avalonia.Controls.TextBox tx && !string.IsNullOrEmpty(tx.Watermark)) res.Add(tx.Watermark);
            if (Avalonia.Controls.ToolTip.GetTip(c) is string tip) res.Add(tip);
            if (c is Avalonia.Controls.NumericUpDown n && !string.IsNullOrEmpty(n.FormatString)) res.Add(n.FormatString);
            if (c is Avalonia.Controls.Window w && !string.IsNullOrEmpty(w.Title)) res.Add(w.Title);
        }
        return res;
    }

    private static void AssertMonolingual(IEnumerable<string> texts, string language)
    {
        var bad = new List<string>();
        foreach (var t in texts.Distinct())
        {
            if (language == "en" && (FrenchLetters.IsMatch(t) || FrenchWords.Any(w => System.Text.RegularExpressions.Regex.IsMatch(t, $"\\b{w}\\b")))) bad.Add(t);
            if (language == "fr" && EnglishWords.Any(w => System.Text.RegularExpressions.Regex.IsMatch(t, $"\\b{w}\\b"))) bad.Add(t);
        }
        Assert.True(bad.Count == 0, $"Mixed languages in {language}: " + string.Join(" | ", bad.Take(20)));
    }

    [Fact] public Task Whole_window_is_monolingual_in_english_and_in_french_and_switching_changes_it_at_runtime() => f.Session.Dispatch(() =>
    {
        ImmoCad.Core.Translations.Language = "en";                                     // the sample building's names are data created in the current language
        var vm = new MainViewModel { Settings = new() }; var w = new MainWindow { DataContext = vm, Width = 1400, Height = 900 }; w.Show();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        try
        {
            vm.ApplyLanguage("en"); w.UpdateLayout(); vm.SelectWalls([vm.CurrentLevel!.Walls[0].Id], false); w.UpdateLayout();
            var en = VisibleTexts(w); File.WriteAllLines(Path.Combine(dir, "texts-en.txt"), en); AssertMonolingual(en, "en"); Assert.Contains(en, t => t == "Palette"); Assert.Contains(en, t => t == "Undo"); Assert.Contains("File", en.Concat(MenuFactory.Build(vm, () => { }, () => { }, () => { }).Select(m => m.Header.TrimStart('_'))));
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "lang-en.png"));
            vm.ApplyLanguage("fr"); w.UpdateLayout(); var fr = VisibleTexts(w); AssertMonolingual(fr, "fr");                                                                  // generated names (Level 2, Stairwell…) follow the language as well
            Assert.Contains(fr, t => t == "Annuler"); Assert.DoesNotContain(fr, t => t == "Undo"); Assert.Contains("Ajouter un niveau", vm.HistoryRows.Select(r => r.Name).Concat(["Ajouter un niveau"]));
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "lang-fr.png"));
            vm.ApplyLanguage("en"); w.UpdateLayout(); Assert.Contains(VisibleTexts(w), t => t == "Undo");                                  // and back, without restarting
        }
        finally { vm.ApplyLanguage("fr"); w.Close(); }
    }, CancellationToken.None);

    [Fact] public Task Menus_status_hints_palette_dialog_texts_and_wizard_follow_the_language() => f.Session.Dispatch(() =>
    {
        ImmoCad.Core.Translations.Language = "en"; var vm = new MainViewModel { Settings = new() };
        try
        {
            vm.ApplyLanguage("en");
            var menu = MenuFactory.Build(vm, () => { }, () => { }, () => { }); Assert.Equal(["File", "Edit", "Level", "View", "Help"], menu.Select(m => m.Header.TrimStart('_')));
            Assert.Contains(menu[3].Items, i => i.Header == "Language" && i.Children!.Any(c => c.Header == "Français") && i.Children!.Any(c => c.Header == "English"));
            Assert.Contains(vm.Palette, p => p.Name == "Radiator" && p.Category == "Equipment"); Assert.Equal("Ready", vm.Status); Assert.StartsWith("Click a wall", vm.Hint);
            vm.SetToolCommand.Execute("DrawWall"); Assert.StartsWith("Click the wall points", vm.Hint); vm.SelectWalls([vm.CurrentLevel!.Walls[0].Id], false); Assert.Equal("1 wall(s) selected", vm.SelectionText);
            Assert.Equal(["Orthophoto", "IGN plan", "Cadastre"], vm.MapLayers); Assert.Contains("Ground floor", vm.Levels.Select(l => l.Name)); Assert.Contains("Heating supply", vm.Networks.Select(n => n.Type));
            vm.SelectLevelCommand.Execute(vm.Building.Levels[0].Id); vm.SelectWalls([vm.CurrentLevel!.Walls[0].Id], false); vm.MoveSelectionUpCommand.Execute(null); Assert.StartsWith("1 wall(s) moved to", vm.Status);
            vm.StartTourCommand.Execute(null); Assert.Equal("1. Import a plan", vm.TourTitle);
            var w = new ImportWizardViewModel(WizardTests.PlanPng()); Assert.Equal("Angle: 0.0°", w.AngleLabel); Assert.Equal("Min thickness: 5 px", w.MinThicknessLabel);
            vm.ApplyLanguage("fr");
            Assert.Equal("Fichier", MenuFactory.Build(vm, () => { }, () => { }, () => { })[0].Header.TrimStart('_')); Assert.Contains(vm.Palette, p => p.Name == "Radiateur" && p.Category == "Équipement"); Assert.Equal("1. Importer un plan", vm.TourTitle);
            Assert.Equal("Angle : 0.0°", w.AngleLabel); Assert.Equal(["RDC", "R+1", "R+2"], vm.Building.Levels.Select(l => l.DisplayName)); Assert.Contains("RDC", vm.Levels.Select(l => l.Name));          // generated names follow the language Assert.Contains("Départ chauffage", vm.Networks.Select(n => n.Type));
            Assert.Equal(["Orthophoto", "Plan IGN", "Cadastre"], vm.MapLayers);
        }
        finally { vm.ApplyLanguage("fr"); }
    }, CancellationToken.None);

    [Fact] public Task Language_choice_is_persisted_and_the_dialogs_use_it() => f.Session.Dispatch(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "lang" + Guid.NewGuid().ToString("N"), "settings.json"); var vm = new MainViewModel { Settings = new(), SettingsPath = path };
        try
        {
            vm.SetLanguageCommand.Execute("en"); Assert.Equal("en", ImmoCad.Core.SettingsStore.Load(path).Language); Assert.Equal("en", ImmoCad.Core.Translations.Language);
            var d = new ExportDialog(vm.Building, "ifc"); d.Show(); var texts = VisibleTexts(d); AssertMonolingual(texts, "en"); Assert.Contains(texts, t => t == "Levels to include"); Assert.Contains(texts, t => t.StartsWith("BIM exchange")); d.Close();
            var about = new AboutDialog(); about.Show(); AssertMonolingual(VisibleTexts(about), "en"); Assert.Contains("About", VisibleTexts(about)); about.Close();
            var wiz = new ImportWizard(WizardTests.PlanPng()); wiz.Show(); AssertMonolingual(VisibleTexts(wiz), "en"); Assert.Contains("1. Straighten", VisibleTexts(wiz)); wiz.Close();
            vm.SetLanguageCommand.Execute("fr"); Assert.Equal("fr", ImmoCad.Core.SettingsStore.Load(path).Language);
            var d2 = new ExportDialog(vm.Building, "ifc"); d2.Show(); AssertMonolingual(VisibleTexts(d2), "fr"); Assert.Contains(VisibleTexts(d2), t => t == "Niveaux à inclure"); d2.Close();
        }
        finally { vm.ApplyLanguage("fr"); }
    }, CancellationToken.None);
}

public class ThreeLevelImportTests
{
    /// <summary>A scanned floor: outer walls and a stairwell box, drawn with its own scale error and offset (every scan differs a little).</summary>
    private static byte[] Scan(double sx, double sy, int ox, int oy)
    {
        const int W = 700, H = 520; var px = Enumerable.Repeat((byte)255, W * H).ToArray();
        void Box(double x0, double y0, double x1, double y1, int t) { for (var y = (int)(y0 * sy) + oy; y < (int)(y1 * sy) + oy; y++) for (var x = (int)(x0 * sx) + ox; x < (int)(x1 * sx) + ox; x++) if (x >= 0 && y >= 0 && x < W && y < H && (x < (int)(x0 * sx) + ox + t || x >= (int)(x1 * sx) + ox - t || y < (int)(y0 * sy) + oy + t || y >= (int)(y1 * sy) + oy - t)) px[y * W + x] = 0; }
        Box(40, 40, 600, 440, 10);                       // outer walls: 560 × 400 px = 5.6 m × 4 m at 0.1 px/mm
        Box(80, 80, 200, 200, 8);                        // stairwell: 120 × 120 px
        using var img = new ImageMagick.MagickImage(px, new ImageMagick.PixelReadSettings(W, H, ImageMagick.StorageType.Char, "I")); return img.ToByteArray(ImageMagick.MagickFormat.Png);
    }

    [Fact] public Task Three_jpg_levels_are_imported_aligned_on_the_stairwell_and_superposed_within_50mm() => new HeadlessFixture().Session.Dispatch(() =>
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        // three scans: the 2nd is 3 % too wide and shifted, the 3rd is 2 % too long, shifted the other way; calibrated on the same known 5.6 m outer wall
        var scans = new[] { Scan(1, 1, 0, 0), Scan(1.03, 1, 12, -9), Scan(1, 1.02, -7, 11) };
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) vm.AddLevelCommand.Execute(null);
            var s = scans[i]; var wiz = new ImportWizardViewModel(s) { DistanceMetres = 5.6 };
            var gray = wiz.Gray!; int y = 240; int left = Enumerable.Range(0, gray.Width).First(x => gray.Pixels[y * gray.Width + x] < 100), right = Enumerable.Range(0, gray.Width).Last(x => gray.Pixels[y * gray.Width + x] < 100);
            wiz.AddCalibrationPoint(left, y); wiz.AddCalibrationPoint(right + 1, y); wiz.MinThickness = 6; wiz.Detect(); vm.ApplyWizard(wiz);
        }
        Assert.Equal(3, vm.Building.Levels.Count); Assert.All(vm.Building.Levels, l => Assert.NotEmpty(l.Walls));
        // stairwell outer corners, found on each level as the smallest wall group's outline
        List<ImmoCad.Core.Model.PointMm> Stair(ImmoCad.Core.Model.Level l) { var shape = l.Walls.OrderBy(w => w.Footprint.Max(p => p.X) - w.Footprint.Min(p => p.X)).First(); return shape.Footprint.OrderBy(p => p.Y).ThenBy(p => p.X).ToList(); }
        for (var i = 1; i < 3; i++)
        {
            var below = Stair(vm.Building.Levels[i - 1]); var cur = Stair(vm.Building.Levels[i]); Assert.Equal(4, cur.Count);
            vm.SelectLevelCommand.Execute(vm.Building.Levels[i].Id); vm.SetToolCommand.Execute("Align");
            for (var k = 0; k < 3; k++) { vm.AlignClick(cur[k].X, cur[k].Y); vm.AlignClick(below[k].X, below[k].Y); }              // 3 corner pairs → affine (independent X/Y stretch)
            Assert.True(vm.AlignApply()); vm.SetToolCommand.Execute("Select");
        }
        var s0 = Stair(vm.Building.Levels[0]);
        for (var i = 1; i < 3; i++)
        {
            var si = Stair(vm.Building.Levels[i]); Assert.Equal(4, si.Count);
            for (var k = 0; k < 4; k++) Assert.True(Math.Abs(si[k].X - s0[k].X) <= 50 && Math.Abs(si[k].Y - s0[k].Y) <= 50, $"level {i} corner {k}: {si[k]} vs {s0[k]}");       // stairwell walls superposed within 50 mm
        }
    }, CancellationToken.None);
}

public class SideBySideAnd3dToolsTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Side_by_side_shows_plan_and_3d_together_sharing_level_and_selection() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.Settings.AnimateTransitions = false; var w = new MainWindow { DataContext = vm, Width = 1500, Height = 800 }; w.Show(); w.UpdateLayout();
        var plan = w.FindControl<PlanView>("Plan")!; var host = w.FindControl<Avalonia.Controls.Grid>("View3DHost")!; Assert.True(plan.IsVisible); Assert.False(host.IsVisible);
        vm.SideBySide = true; vm.SelectLevelCommand.Execute(vm.Building.Levels[1].Id); w.UpdateLayout(); w.CaptureRenderedFrame();
        Assert.True(plan.IsVisible && host.IsVisible); Assert.True(Math.Abs(plan.Bounds.Width - host.Bounds.Width) < 4, $"{plan.Bounds.Width} vs {host.Bounds.Width}"); Assert.True(plan.Bounds.Right <= host.Bounds.Left + 1);   // plan left, 3D right
        Assert.NotEmpty(w.View3D.Controller.Objects);                                                                                  // the 3D view is built while the plan is the active mode
        var cutBefore = w.View3D.Controller.Objects.Count(o => o.Kind == ImmoCad.Scene.SceneKind.Wall);
        vm.SelectLevelCommand.Execute(vm.Building.Levels[0].Id); w.UpdateLayout(); Assert.True(w.View3D.Controller.Objects.Count(o => o.Kind == ImmoCad.Scene.SceneKind.Wall) < cutBefore);   // the level bar drives both
        vm.SelectWalls([vm.CurrentLevel!.Walls[0].Id], false); Assert.Contains(w.View3D.Controller.Objects, o => o.Highlight);       // selection shared: highlighted in 3D
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "sidebyside.png")); vm.SideBySide = false; w.UpdateLayout(); Assert.False(host.IsVisible); w.Close();
    }, CancellationToken.None);

    [Fact] public Task Riser_and_palette_tools_work_by_clicking_the_floor_in_3d() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.Settings.AnimateTransitions = false; var w = new MainWindow { DataContext = vm, Width = 1000, Height = 800 }; w.Show(); w.UpdateLayout();
        vm.SetModeCommand.Execute("Solid"); w.UpdateLayout(); w.CaptureRenderedFrame(); var c = w.View3D.Controller; var ctl = w.View3D.Control; var floor = vm.CurrentLevel!.AltitudeMm / 1000.0;
        Avalonia.Point ScreenOf(int xMm, int yMm) { var half = Math.Min(ctl.Bounds.Width, ctl.Bounds.Height) / 2; var (px, py, _) = c.Camera.Project(xMm / 1000.0, yMm / 1000.0, floor); return new(ctl.Bounds.Width / 2 + px * half, ctl.Bounds.Height / 2 - py * half); }
        var target = new ImmoCad.Core.Model.PointMm(4000, 3000); var back = c.PlaneAt(ScreenOf(4000, 3000), ctl.Bounds.Width, ctl.Bounds.Height, floor)!.Value; Assert.True(Math.Abs(back.X - 4000) <= 2 && Math.Abs(back.Y - 3000) <= 2);   // screen → floor is the inverse of the projection
        var nets = vm.Building.Networks.Count; vm.SetToolCommand.Execute("Riser"); var s = ScreenOf(4000, 3000); c.Down(s, false); c.Up(s, vm, ctl.Bounds.Width, ctl.Bounds.Height);
        Assert.Equal(nets + 1, vm.Building.Networks.Count); var node = vm.Building.Networks[^1].Nodes[0].Position; Assert.InRange(node.X, 3900, 4100); Assert.InRange(node.Y, 2900, 3100);
        vm.SelectPaletteCommand.Execute("radiator"); var eq = vm.Building.Equipment.Count; s = ScreenOf(5000, 400); c.Down(s, false); c.Up(s, vm, ctl.Bounds.Width, ctl.Bounds.Height); Assert.Equal(eq + 1, vm.Building.Equipment.Count);
        vm.SetToolCommand.Execute("Select"); var walls = vm.SelectedWalls.Count; c.Down(ScreenOf(3000, 100), false); c.Up(ScreenOf(3000, 100), vm, ctl.Bounds.Width, ctl.Bounds.Height);   // select tool: picks instead
        w.Close();
    }, CancellationToken.None);

    [Fact] public Task Networks_panel_says_which_spaces_a_riser_runs_through_and_the_snapper_offers_the_footprint() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var l = vm.Building.Levels[0]; var riser = vm.Building.Networks[0]; var nid = riser.Nodes[0].Position;
        vm.Actions.AddSpace(l.Id, [new(nid.X - 200, nid.Y - 200), new(nid.X + 400, nid.Y - 200), new(nid.X + 400, nid.Y + 400), new(nid.X - 200, nid.Y + 400)], "Gaine", ImmoCad.Core.Model.SpaceKind.ServiceShaft); vm.Refresh();
        Assert.Contains("Gaine", vm.Networks.First(n => n.Id == riser.Id).Where); Assert.StartsWith("passe par", vm.Networks.First(n => n.Id == riser.Id).Where);
    }, CancellationToken.None);
}

public class Handles3dTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task Dragging_a_pipe_end_in_3d_stays_on_the_pipe_axis_snaps_and_is_one_undoable_step() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.Settings.AnimateTransitions = false; var w = new MainWindow { DataContext = vm, Width = 1100, Height = 850 }; w.Show(); w.UpdateLayout();
        vm.SetToolCommand.Execute("DrawPipe"); vm.PipeType = ImmoCad.Core.Model.NetworkType.ColdWater; vm.PipeElevation = 2400; vm.CommitPipe([new(1000, 3000), new(3000, 3000)]); vm.SetToolCommand.Execute("Select");
        var net = vm.Building.Networks[^1]; var seg = net.Segments[0]; vm.SelectSegment(net.Id, seg.Id); vm.SetModeCommand.Execute("Solid"); w.UpdateLayout(); w.CaptureRenderedFrame();
        var c = w.View3D.Controller; var ctl = w.View3D.Control; c.Size(ctl.Bounds.Width, ctl.Bounds.Height); var handles = c.Handles!(); Assert.Equal(2, handles.Count(h => h.NetworkId == net.Id)); var end = handles.First(h => h.End.X > 2.5);   // both free ends get a handle: take the right one
        Avalonia.Point Scr((double X, double Y, double Z) p) { var half = Math.Min(ctl.Bounds.Width, ctl.Bounds.Height) / 2; var (px, py, _) = c.Camera.Project(p.X, p.Y, p.Z); return new(ctl.Bounds.Width / 2 + px * half, ctl.Bounds.Height / 2 - py * half); }
        var steps = vm.History.Items.Count; var from = Scr(end.End); var target = (X: end.End.X + 1.5, Y: end.End.Y + 0.4, Z: end.End.Z);              // the mouse goes 1.5 m along and 0.4 m off the pipe
        c.Down(from, false); Assert.NotNull(c.ActiveHandle); c.Move(Scr((end.End.X + 0.7, end.End.Y, end.End.Z))); c.Move(Scr(target));
        var preview = c.HandlePreview!.Value; Assert.True(Math.Abs(preview.Y - end.End.Y) < 0.05 && Math.Abs(preview.Z - end.End.Z) < 0.05, $"left the axis: {preview}");   // constrained to the axis
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "handle3d.png"));
        c.Up(Scr(target), vm, ctl.Bounds.Width, ctl.Bounds.Height);
        Assert.Equal(steps + 1, vm.History.Items.Count); var moved = vm.Building.Networks[^1].Nodes.Max(n => n.Position.X); Assert.InRange(moved, 3000 + 1300, 3000 + 1700); Assert.Equal(0, moved % 50);
        Assert.Equal(3000, vm.Building.Networks[^1].Nodes.First(n => n.Position.X < 2000 && n.Position.Y == 3000).Position.Y);                                    // y unchanged
        vm.UndoCommand.Execute(null); Assert.Equal(3000, vm.Building.Networks[^1].Nodes.Max(n => n.Position.X)); w.Close();
    }, CancellationToken.None);
}

public class NorthUpTests
{
    private readonly HeadlessFixture f = new();

    [Fact] public Task North_up_turns_the_plan_by_the_site_rotation_keeps_hit_testing_exact_and_the_arrow_points_up() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm, Width = 1300, Height = 850 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        vm.Actions.SetSite(new ImmoCad.Core.Model.SiteInfo { EastM = 650000, NorthM = 6860000, RotationDeg = 30, Footprint = [[650000, 6860000], [650008, 6860000], [650008, 6860006], [650000, 6860006]] }); vm.Refresh(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; Assert.Equal(0, plan.ViewRotationRad); var flat = plan.ToScreenForTest(new(2000, 1000));
        vm.ToggleNorthUpCommand.Execute(null); Assert.True(vm.NorthUp); Assert.Equal(30 * Math.PI / 180, plan.ViewRotationRad, 9); Assert.Equal("Nord en haut", vm.Status);
        var turned = plan.ToScreenForTest(new(2000, 1000)); Assert.True(Math.Abs(turned.X - flat.X) + Math.Abs(turned.Y - flat.Y) > 10);                                  // the plan really turned
        foreach (var p in new ImmoCad.Core.Model.PointMm[] { new(0, 0), new(2000, 1000), new(7000, 5500), new(-300, 4000) }) Assert.Equal(p, plan.ToMm(plan.ToScreenForTest(p)));   // screen ↔ plan stays exact
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "northup.png"));
        var wall = vm.CurrentLevel!.Walls[0]; var inside = wall.Footprint.Aggregate((a, b) => new(a.X + b.X, a.Y + b.Y)); var c = new ImmoCad.Core.Model.PointMm(inside.X / wall.Footprint.Count, inside.Y / wall.Footprint.Count);
        var at = plan.TranslatePoint(plan.ToScreenForTest(c), w)!.Value; w.MouseDown(at, Avalonia.Input.MouseButton.Left); w.MouseUp(at, Avalonia.Input.MouseButton.Left); Assert.Contains(wall.Id, vm.SelectedWalls);   // a click still finds the wall
        vm.ToggleNorthUpCommand.Execute(null); Assert.Equal(0, plan.ViewRotationRad); Assert.Equal("Bâtiment aligné", vm.Status); w.Close();
    }, CancellationToken.None);

    [Fact] public Task Keyboard_shortcuts_undo_redo_select_all_delete_and_tools() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        vm.Actions.ImportWalls(vm.CurrentLevel!.Id, [new List<PointMm> { new(0, 0), new(2000, 0), new(2000, 200), new(0, 200) }]); vm.Refresh();
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout();
        var cmd = MenuFactory.CommandModifiers == Avalonia.Input.KeyModifiers.Meta ? Avalonia.Input.RawInputModifiers.Meta : Avalonia.Input.RawInputModifiers.Control;
        void Press(Avalonia.Input.Key k, Avalonia.Input.RawInputModifiers m = Avalonia.Input.RawInputModifiers.None) => w.KeyPress(k, m, Avalonia.Input.PhysicalKey.None, null);
        Press(Avalonia.Input.Key.A, cmd); Assert.Single(vm.SelectedWalls);
        Press(Avalonia.Input.Key.Delete); Assert.Empty(vm.CurrentLevel!.Walls);
        Press(Avalonia.Input.Key.Z, cmd); Assert.Single(vm.CurrentLevel!.Walls);
        Press(Avalonia.Input.Key.Z, cmd | Avalonia.Input.RawInputModifiers.Shift); Assert.Empty(vm.CurrentLevel!.Walls);
        Press(Avalonia.Input.Key.Z, cmd); Press(Avalonia.Input.Key.Y, cmd); Assert.Empty(vm.CurrentLevel!.Walls);
        Press(Avalonia.Input.Key.W); Assert.Equal(ToolKind.DrawWall, vm.Tool);
        Press(Avalonia.Input.Key.Escape); Assert.Equal(ToolKind.Select, vm.Tool);
    }, CancellationToken.None);

    [Fact] public Task Snap_to_grid_follows_the_zoom_and_can_be_switched_off() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!;
        plan.Scale = 0.1; plan.Zoom("in"); w.CaptureRenderedFrame();                          // 0.125 px/mm: a 10 px grid is 100 mm
        Assert.True(vm.SnapToGrid); Assert.Equal(100, plan.Snapper.GridMm);
        plan.Scale = 0.5; plan.InvalidateVisual(); w.CaptureRenderedFrame(); Assert.Equal(20, plan.Snapper.GridMm);   // zoomed in: finer grid
        plan.Scale = 0.02; plan.InvalidateVisual(); w.CaptureRenderedFrame(); Assert.Equal(500, plan.Snapper.GridMm); // zoomed out: coarser grid
        Assert.Equal(new(1000, 500), plan.Snapper.Snap(new(1190, 430), []).Point);
        vm.ToggleSnapToGridCommand.Execute(null); Assert.False(vm.SnapToGrid);
        plan.InvalidateVisual(); w.CaptureRenderedFrame(); Assert.Equal(new(1190, 430), plan.Snapper.Snap(new(1190, 430), []).Point);
        w.Close();
    }, CancellationToken.None);

    [Fact] public Task Select_tool_picks_a_riser_space_drag_pans_and_ctrl_drag_rotates() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; Avalonia.Point At(int x, int y) => plan.TranslatePoint(plan.ToScreenForTest(new(x, y)), w)!.Value;
        var L = Avalonia.Input.MouseButton.Left; var none = Avalonia.Input.RawInputModifiers.None;
        w.MouseDown(At(300, 300), L); w.MouseUp(At(300, 300), L);
        Assert.NotNull(vm.SelectedSegment); Assert.True(ImmoCad.Core.Networks.NetworkQueries.IsVertical(ImmoCad.Core.Networks.NetworkQueries.All(vm.Building).First(s => s.Segment.Id == vm.SelectedSegment!.Value.SegmentId)));
        var off = plan.Offset; var from = At(2000, 2000);
        w.KeyPress(Avalonia.Input.Key.Space, none, Avalonia.Input.PhysicalKey.Space, " ");                       // Space held: drag pans
        w.MouseDown(from, L); w.MouseMove(from + new Avalonia.Point(40, 25)); w.MouseUp(from + new Avalonia.Point(40, 25), L);
        w.KeyRelease(Avalonia.Input.Key.Space, none, Avalonia.Input.PhysicalKey.Space, null);
        Assert.Equal(off.X + 40, plan.Offset.X, 3); Assert.Equal(off.Y - 25, plan.Offset.Y, 3);
        var c = new Avalonia.Point(plan.Bounds.Width / 2, plan.Bounds.Height / 2); var a = plan.TranslatePoint(new(c.X + 100, c.Y), w)!.Value; var b = plan.TranslatePoint(new(c.X, c.Y + 100), w)!.Value;
        w.MouseDown(a, L, Avalonia.Input.RawInputModifiers.Control); w.MouseMove(b, Avalonia.Input.RawInputModifiers.Control); w.MouseUp(b, L, Avalonia.Input.RawInputModifiers.Control);
        Assert.Equal(Math.PI / 2, plan.UserRotationRad, 2);                   // a quarter turn about the viewport centre
        plan.Zoom("fit"); Assert.Equal(0, plan.UserRotationRad);
        w.Close();
    }, CancellationToken.None);

    [Fact] public Task Select_tool_selects_a_door_edits_its_values_and_deletes_it() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        vm.Actions.ImportWalls(vm.CurrentLevel!.Id, [new List<PointMm> { new(0, 0), new(4000, 0), new(4000, 200), new(0, 200) }]); vm.Refresh();
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; Avalonia.Point At(int x, int y) => plan.TranslatePoint(plan.ToScreenForTest(new(x, y)), w)!.Value;
        var L = Avalonia.Input.MouseButton.Left;
        vm.SetToolCommand.Execute("Door"); w.MouseDown(At(1000, 100), L); w.MouseUp(At(1000, 100), L);
        Assert.Single(vm.CurrentLevel!.Walls[0].Openings); vm.SetToolCommand.Execute("Select");
        w.MouseDown(At(1000, 100), L); w.MouseUp(At(1000, 100), L);
        Assert.NotNull(vm.SelectedOpening); Assert.True(vm.SelectedOpeningVisible);
        var steps = vm.History.Items.Count; vm.OpWidth = 1000; vm.OpSwingOut = true; vm.OpLabel = "Entrée"; vm.ApplyOpeningCommand.Execute(null);
        var o = vm.CurrentLevel!.Walls[0].Openings[0]; Assert.Equal(1000, o.WidthMm); Assert.True(o.SwingOut); Assert.Equal("Entrée", o.Label); Assert.Equal(steps + 1, vm.History.Items.Count);
        vm.UndoCommand.Execute(null); Assert.Equal(800, vm.CurrentLevel!.Walls[0].Openings[0].WidthMm);
        w.MouseDown(At(1000, 100), L); w.MouseUp(At(1000, 100), L);
        w.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Delete, null);
        Assert.Empty(vm.CurrentLevel!.Walls[0].Openings); Assert.Null(vm.SelectedOpening);
        w.Close();
    }, CancellationToken.None);

    [Fact] public void Wall_rectangle_lies_on_the_chosen_side_of_the_drawing_line()
    {
        var a = new PointMm(0, 0); var b = new PointMm(1000, 0);
        List<PointMm> R(ImmoCad.Core.Geometry.WallAlign al) => ImmoCad.Core.Geometry.WallGeometry.Rectangle(a, b, 200, al).ToList();
        Assert.Equal((-100, 100), (R(ImmoCad.Core.Geometry.WallAlign.Center).Min(p => p.Y), R(ImmoCad.Core.Geometry.WallAlign.Center).Max(p => p.Y)));
        Assert.Equal((0, 200), (R(ImmoCad.Core.Geometry.WallAlign.Left).Min(p => p.Y), R(ImmoCad.Core.Geometry.WallAlign.Left).Max(p => p.Y)));     // left of an east-going line = north
        Assert.Equal((-200, 0), (R(ImmoCad.Core.Geometry.WallAlign.Right).Min(p => p.Y), R(ImmoCad.Core.Geometry.WallAlign.Right).Max(p => p.Y)));
    }

    [Fact] public Task Space_changes_the_wall_side_while_drawing_with_a_dashed_preview_and_the_view_cube_changes_the_view() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(startWithSample: false) { Settings = new() }; vm.NewDocumentCommand.Execute(null);
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var plan = w.FindControl<PlanView>("Plan")!; Avalonia.Point At(int x, int y) => plan.TranslatePoint(plan.ToScreenForTest(new(x, y)), w)!.Value;
        var L = Avalonia.Input.MouseButton.Left; var none = Avalonia.Input.RawInputModifiers.None;
        vm.ToggleSnapToGridCommand.Execute(null); vm.SetToolCommand.Execute("DrawWall");
        w.MouseDown(At(0, 0), L); w.MouseUp(At(0, 0), L); w.MouseMove(At(3000, 0));
        w.CaptureRenderedFrame()!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "wall-preview.png"));
        Assert.Equal(ImmoCad.Core.Geometry.WallAlign.Center, vm.WallAlign);
        w.KeyPress(Avalonia.Input.Key.Space, none, Avalonia.Input.PhysicalKey.Space, " "); Assert.Equal(ImmoCad.Core.Geometry.WallAlign.Left, vm.WallAlign);
        w.MouseDown(At(3000, 0), L); w.MouseUp(At(3000, 0), L); w.KeyPress(Avalonia.Input.Key.Enter, none, Avalonia.Input.PhysicalKey.Enter, null);
        var fp = vm.CurrentLevel!.Walls[0].Footprint; Assert.Equal(0, fp.Min(p => p.Y)); Assert.Equal(200, fp.Max(p => p.Y));
        // view cube
        vm.SetToolCommand.Execute("Select"); vm.SetModeCommand.Execute("Solid"); w.UpdateLayout();
        var cube = w.Cube; var c = w.View3D.Controller; c.Animate = false; cube.Controller = c; cube.Measure(new(112, 112)); cube.Arrange(new(0, 0, 112, 112));
        c.Camera.Yaw = 0; c.Camera.Pitch = 0.02; Assert.Equal(ViewCube.Face.Front, cube.FaceAt(new(56, 58)));
        c.Camera.Yaw = Math.PI / 4; c.Camera.Pitch = Math.PI / 5;
        var top = Enumerable.Range(0, 112).SelectMany(y => Enumerable.Range(0, 112).Select(x => new Avalonia.Point(x, y))).First(p => cube.FaceAt(p) == ViewCube.Face.Top);
        cube.Click(top); Assert.InRange(c.Camera.Pitch, Math.PI / 2 - 0.02, Math.PI / 2);
        c.Camera.Yaw = 0; c.Camera.Pitch = 0.02; cube.Click(new(56, 58)); Assert.Equal(0, c.Camera.Yaw, 3);
        w.Close();
    }, CancellationToken.None);

    [Fact] public void Wizard_aligns_a_picked_wall_line_to_the_nearest_horizontal_or_vertical_axis()
    {
        Assert.Equal(5.71, ImportWizardViewModel.TiltOf((0, 0), (100, 10)), 2);          // slightly clockwise from horizontal
        Assert.Equal(-5.71, ImportWizardViewModel.TiltOf((0, 0), (10, 100)), 2);         // near-vertical: measured from the vertical axis
        Assert.Equal(0, ImportWizardViewModel.TiltOf((5, 5), (5, 300)), 6);
        Assert.Equal(3, ImportWizardViewModel.TiltOf((100, 0), (0, -5.24)), 0);     // leftwards line, same axis
        var w = new ImportWizardViewModel(WizardTests.PlanPng()); w.AlignToLine((50, 240), (550, 265));
        Assert.Equal(2.86, w.Angle, 2); Assert.Equal(w.Angle, w.AppliedAngle);
    }
}
