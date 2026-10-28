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
