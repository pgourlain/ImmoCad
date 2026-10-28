using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless;
using Avalonia.Controls;
using ImmeubleCad.Desktop;
using Microsoft.Extensions.Logging;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ImmeubleCad.Desktop.Tests.TestAppBuilder))]

namespace ImmeubleCad.Desktop.Tests;

public sealed class HeadlessFixture
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessFixture).Assembly);
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
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm }; w.Show();
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
