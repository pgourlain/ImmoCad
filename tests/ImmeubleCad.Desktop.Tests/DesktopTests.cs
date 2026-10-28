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
    [Fact] public Task Main_window_shows_plan_and_levels() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel();
        var w = new MainWindow { DataContext = vm };
        w.Show();
        Assert.Equal(2, vm.Levels.Count);
        Assert.Equal("R+1", vm.Levels[0].Name); // top level first
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, "main.png"));
        w.Close();
    }, CancellationToken.None);

    [Fact] public Task Switch_to_3d_renders_and_back() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var w = new MainWindow { DataContext = vm }; w.Show();
        vm.SetModeCommand.Execute("Solid");
        Assert.True(vm.Is3D);
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "3d.png"));
        vm.SetModeCommand.Execute("Plan"); Assert.False(vm.Is3D);
        w.Close();
    }, CancellationToken.None);

    [Fact] public Task Draw_wall_chain_is_one_undo_step_and_saves() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel(); var walls = vm.CurrentLevel!.Walls.Count; var steps = vm.History.Items.Count;
        var tool = new ImmeubleCad.Core.Tools.DrawChainTool(vm.CommitWalls);
        ImmeubleCad.Core.Tools.ToolPointer P(int x, int y) => new(new(x, y), x, y);
        tool.Down(P(0, 5000)); tool.Up(P(0, 5000)); tool.Move(P(3000, 5000)); tool.Down(P(3000, 5000)); tool.Move(P(3000, 8000)); tool.Down(P(3000, 8000)); tool.Finish();
        Assert.Equal(walls + 2, vm.CurrentLevel.Walls.Count);
        Assert.Equal(steps + 1, vm.History.Items.Count);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".immeuble");
        vm.SaveTo(path); Assert.False(vm.History.IsDirty);
        var vm2 = new MainViewModel(); vm2.LoadFrom(path); File.Delete(path);
        Assert.Equal(walls + 2, vm2.CurrentLevel!.Walls.Count);
        vm.UndoCommand.Execute(null); Assert.Equal(walls, vm.Building.Levels.First(l => l.Name == "RDC").Walls.Count);
    }, CancellationToken.None);

    [Fact] public Task Undo_redo_levels() => f.Session.Dispatch(() =>
    {
        var vm = new MainViewModel();
        vm.AddLevelCommand.Execute(null); Assert.Equal(3, vm.Levels.Count);
        vm.UndoCommand.Execute(null); Assert.Equal(2, vm.Levels.Count);
        vm.RedoCommand.Execute(null); Assert.Equal(3, vm.Levels.Count);
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
