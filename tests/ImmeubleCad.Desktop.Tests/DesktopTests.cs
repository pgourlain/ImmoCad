using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless;
using Avalonia.Controls;
using ImmeubleCad.Desktop;
using ImmeubleCad.Core.Model;
using Microsoft.Extensions.Logging;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ImmeubleCad.Desktop.Tests.TestAppBuilder))]

namespace ImmeubleCad.Desktop.Tests;

public sealed class HeadlessFixture
{
    // Test classes run in parallel: the Avalonia platform must be initialised exactly once.
    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessFixture).Assembly), LazyThreadSafetyMode.ExecutionAndPublication);
    public HeadlessUnitTestSession Session => Shared.Value;
}

public static class TestInit
{
    [System.Runtime.CompilerServices.ModuleInitializer] public static void Init() { MainWindow.UseOpenGL = false; Environment.SetEnvironmentVariable("IMMEUBLECAD_DATA_DIR", Path.Combine(Path.GetTempPath(), "immeublecad-tests-" + Environment.ProcessId)); }   // headless tests draw 3D in software
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
        Assert.Equal(3, vm.Levels.Count);
        Assert.Equal("R+2", vm.Levels[0].Name); // top level first
        w.CaptureRenderedFrame()!.Save(Path.Combine(Shots(), "main.png")); w.Close();
    }, CancellationToken.None);

    [Fact] public Task Thirty_levels_fit_the_level_bar() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm }; w.Show();
        while (vm.Building.Levels.Count < 30) vm.AddLevelCommand.Execute(null);
        Assert.Equal(30, vm.Levels.Count); Assert.Equal("R+29", vm.Levels[0].Name); Assert.True(vm.Levels[0].IsCurrent);
        for (var i = 0; i < 29; i++) vm.LevelDownCommand.Execute(null);
        Assert.Equal("RDC", vm.CurrentLevel!.Name);
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
        Assert.Equal("R+1", vm.CurrentLevel!.Name); Assert.Equal(6, vm.CurrentLevel.Walls.Count);
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
        var tool = new ImmeubleCad.Core.Tools.DrawChainTool(vm.CommitWalls);
        ImmeubleCad.Core.Tools.ToolPointer P(int x, int y) => new(new(x, y), x, y);
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
        vm.AddLevelCommand.Execute(null); Assert.Equal(4, vm.Levels.Count);
        vm.UndoCommand.Execute(null); Assert.Equal(3, vm.Levels.Count);
        vm.RedoCommand.Execute(null); Assert.Equal(4, vm.Levels.Count);
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
        var b = new ImmeubleCad.Core.Model.Building(); var h = new ImmeubleCad.Core.History.BuildingHistory();
        var a = new ImmeubleCad.Core.History.BuildingActions(b, h, new ImmeubleCad.Core.History.BuildingEventsService()); var lv = a.AddLevel("RDC", 0);
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
        vm.RiserTemplate = "EF + ECS"; vm.PlaceRiser(new(1000, 1000));
        Assert.Equal(nets + 2, vm.Building.Networks.Count); Assert.Equal(steps + 1, vm.History.Items.Count);
        vm.SetToolCommand.Execute("DrawPipe"); vm.PipeType = ImmeubleCad.Core.Model.NetworkType.HeatingSupply; vm.PipeElevation = 2400;
        vm.CommitPipe([new(300, 300), new(3000, 300), new(3000, 2500)]);
        var net = vm.Building.Networks.First(n => n.Type == ImmeubleCad.Core.Model.NetworkType.HeatingSupply && n.Segments.Any(s => vm.Building.Networks.SelectMany(x => x.Nodes).Any(nd => nd.Position.Z == 2400)));
        Assert.Contains(net.Nodes, n => n.Position == new ImmeubleCad.Core.Model.PointMm3(3000, 300, 2400));
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
        vm.RiserTemplate = "Chauffage aller + retour"; vm.PlaceRiser(new(4000, 3000));
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
        var nets = vm.Building.Networks.Count; vm.RiserType = ImmeubleCad.Core.Model.NetworkType.ColdWater;
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
        vm.SetToolCommand.Execute("DrawPipe"); vm.PipeType = ImmeubleCad.Core.Model.NetworkType.ColdWater; vm.PipeElevation = 2400;
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

    [Fact] public Task Dirty_marker_and_save_prompt_on_close_with_each_answer() => f.Session.Dispatch(async () =>
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

    [Fact] public Task Errors_are_reported_through_the_dialog_service_and_logged() => f.Session.Dispatch(async () =>
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
        Assert.Equal(["État initial", "Add level", "Add level", "Add level"], vm.HistoryRows.Select(r => r.Name));
        Assert.True(vm.HistoryRows[^1].IsCurrent); vm.JumpToCommand.Execute(0);
        Assert.Single(vm.Building.Levels); Assert.True(vm.HistoryRows[1].IsCurrent); Assert.True(vm.HistoryRows[2].IsFuture);
        vm.JumpToCommand.Execute(-1); Assert.Empty(vm.Building.Levels);
    }, CancellationToken.None);

    [Fact] public Task Menus_are_defined_once_and_rendered_with_shortcuts() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var specs = MenuFactory.Build(vm, () => { }, () => { }, () => { });
        Assert.Equal(["_Fichier", "_Édition", "_Niveau", "_Affichage", "_Aide"], specs.Select(s => s.Header)); ImmeubleCad.Core.Translations.Language = "en"; Assert.Equal("_File", MenuFactory.Build(vm, () => { }, () => { }, () => { })[0].Header); ImmeubleCad.Core.Translations.Language = "fr";
        var menu = MenuFactory.ToMenu(specs); Assert.Equal(5, menu.Items.Count);
        var save = specs[0].Items.First(i => i.Header == "Enregistrer"); Assert.Equal(Avalonia.Input.Key.S, save.Key); Assert.True(save.UseCommandKey);
        Assert.NotEqual(Avalonia.Input.KeyModifiers.None, MenuFactory.CommandModifiers);
        Assert.NotNull(MenuFactory.ToNativeMenu(specs));
        Assert.Contains(specs[4].Items, i => i.Header.StartsWith("Visite") && !i.Command!.CanExecute(null));            // later features: disabled NotYetImplemented command
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
        Environment.SetEnvironmentVariable("IMMEUBLECAD_LOG", "debug"); Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, AppLogProvider.LevelFromEnvironment()); Assert.True(AppLogProvider.MirrorToStderr());
        Environment.SetEnvironmentVariable("IMMEUBLECAD_LOG", null); Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, AppLogProvider.LevelFromEnvironment());
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
        vm.SelectLevelCommand.Execute(l.Id); var t = new ImmeubleCad.Core.Tools.DrawChainTool(vm.CommitWalls);
        ImmeubleCad.Core.Tools.ToolPointer P(int x, int y) => new(new(x, y), x, y);
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
        Assert.InRange(ImmeubleCad.Core.Geometry.SpaceFinder.AreaMm2(vm.CurrentLevel.Spaces[0].Outline) / 1e6, 3.5, 4.5);
        vm.PlaceSpace(new(-3000, -3000)); Assert.Equal(3, vm.CurrentLevel.Spaces.Count);                    // outside: not closed
        Assert.Equal(3, vm.SpaceItems.Count);
        vm.SetToolCommand.Execute("NumberCellars"); vm.CellarPrefix = "Cave "; vm.CellarStart = 12; var steps = vm.History.Items.Count;
        vm.NumberClick(new(5000, 1200)); vm.NumberClick(new(1000, 1200)); vm.NumberClick(new(3000, 1200)); vm.NumberCommit();
        Assert.Equal(steps + 1, vm.History.Items.Count);
        Assert.Equal("Cave 12", vm.CurrentLevel.Spaces.First(s => ImmeubleCad.Core.Geometry.SpaceFinder.Contains(s.Outline, new(5000, 1200))).Name);
        Assert.Equal("Cave 14", vm.CurrentLevel.Spaces.First(s => ImmeubleCad.Core.Geometry.SpaceFinder.Contains(s.Outline, new(3000, 1200))).Name);
        Assert.Equal(15, vm.CellarStart);
    }, CancellationToken.None);

    [Fact] public Task Measure_adds_nothing_and_level_menu_actions_work_with_30_levels() => f.Session.Dispatch(async () =>
    {
        var dlg = new FakeDialogService(); var vm = Basement(); vm.Dialogs = dlg; var steps = vm.History.Items.Count;
        vm.SetToolCommand.Execute("Measure"); vm.MeasureClick(new(0, 0)); vm.MeasureClick(new(3000, 4000)); Assert.Contains("5.00", vm.Status); Assert.Equal(steps, vm.History.Items.Count);
        var id = vm.Building.Levels[0].Id;
        dlg.PromptAnswers.Enqueue("Sous-sol 1"); await vm.RenameLevelCommand.ExecuteAsync(id); Assert.Equal("Sous-sol 1", vm.Building.Levels[0].Name);
        dlg.PromptAnswers.Enqueue("-2,7 ; 2,5"); await vm.SetLevelGeometryCommand.ExecuteAsync(id); Assert.Equal((-2700, 2500), (vm.Building.Levels[0].AltitudeMm, vm.Building.Levels[0].HeightMm));
        dlg.PromptAnswers.Enqueue("n'importe quoi"); var s2 = vm.History.Items.Count; await vm.SetLevelGeometryCommand.ExecuteAsync(id); Assert.Equal(s2, vm.History.Items.Count);   // invalid input: nothing happens
        vm.AddLevelAboveCommand.Execute(id); vm.AddLevelBelowCommand.Execute(id); Assert.Equal(3, vm.Building.Levels.Count);
        while (vm.Building.Levels.Count < 30) vm.AddLevelCommand.Execute(null);
        Assert.Equal(30, vm.Levels.Count); Assert.Contains("altitude", vm.Levels[0].Tooltip);
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

    [Fact] public Task Animated_transition_starts_on_the_plan_framing_and_finishes_on_the_3d_angle_then_returns() => f.Session.Dispatch(async () =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout(); w.CaptureRenderedFrame();
        var c = w.View3D.Controller; vm.SetModeCommand.Execute("Solid"); Assert.True(c.IsAnimating); Assert.True(vm.Is3D);
        Assert.InRange(c.Camera.Pitch, 1.4, 1.58);                                       // starts as a top view
        await Task.Delay(900); Assert.False(c.IsAnimating); Assert.InRange(c.Camera.Pitch, 0.1, 0.6);   // settled on the 3D angle
        vm.SetModeCommand.Execute("Plan"); Assert.True(c.IsAnimating); Assert.True(vm.Is3D);              // still showing 3D while it tilts back
        await Task.Delay(900); Assert.True(vm.IsPlan); Assert.False(c.IsAnimating);
    }, CancellationToken.None);

    [Fact] public Task Modes_filters_xray_and_cut_and_screenshots() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); vm.Settings.AnimateTransitions = false; var w = new MainWindow { DataContext = vm, Width = 1280, Height = 820 }; w.Show(); w.UpdateLayout();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        vm.SelectLevelCommand.Execute(vm.Building.Levels[1].Id); vm.SetModeCommand.Execute("Solid"); var c = w.View3D.Controller;
        Assert.True(c.Objects.Count > 0, $"objects={c.Objects.Count} level={vm.CurrentLevel?.Name} mode={vm.Mode} cut={c.CutAboveCurrent} filter={c.Filter} kinds={string.Join(",", c.Objects.GroupBy(o => o.Kind).Select(g => g.Key + "=" + g.Count()))} levels={vm.Building.Levels.Count}");
        Assert.True(c.Objects.Count(o => o.Kind == ImmeubleCad.Scene.SceneKind.Slab) == 2, $"kinds={string.Join(",", c.Objects.GroupBy(o => o.Kind).Select(g => g.Key + "=" + g.Count()))} lvl={vm.CurrentLevel?.Name} idx={vm.Building.Levels.FindIndex(l => l.Id == vm.CurrentLevelId)}");   // cut above R+1: RDC + R+1
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "3d-solid.png"));
        vm.ToggleXRayCommand.Execute(null); Assert.All(c.Objects.Where(o => o.Kind == ImmeubleCad.Scene.SceneKind.Wall), o => Assert.True(o.Alpha < 0.5f)); w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "3d-xray.png"));
        vm.ToggleCutCommand.Execute(null); Assert.Equal(3, c.Objects.Count(o => o.Kind == ImmeubleCad.Scene.SceneKind.Slab));
        vm.CycleLevelFilterCommand.Execute(null); vm.CycleLevelFilterCommand.Execute(null); Assert.Single(c.Objects, o => o.Kind == ImmeubleCad.Scene.SceneKind.Slab);   // current level only
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
