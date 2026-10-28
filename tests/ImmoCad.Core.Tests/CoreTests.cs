using ImmoCad.Core.History;
using ImmoCad.Core.IO;
using ImmoCad.Core.Model;
using Xunit;

namespace ImmoCad.Core.Tests;

public class CoreArchitectureTests
{
    [Theory]
    [InlineData("Avalonia")] [InlineData("SkiaSharp")] [InlineData("Silk.NET")] [InlineData("System.Drawing")]
    public void Core_does_not_reference_ui(string prefix)
    {
        var refs = typeof(Building).Assembly.GetReferencedAssemblies().Select(a => a.Name!);
        Assert.DoesNotContain(refs, n => n.StartsWith(prefix));
    }
}

public class CoreArchitectureExtraTests
{
    [Fact] public void Core_has_no_dispatcher_dialog_or_pinvoke()
    {
        var types = typeof(Building).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Any(m => (m.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0));
        var allowed = new[] { "System", "Microsoft.Extensions", "Magick", "ImmoCad", "netstandard", "mscorlib", "Microsoft.Win32" };
        Assert.All(typeof(Building).Assembly.GetReferencedAssemblies().Select(a => a.Name!), n => Assert.True(allowed.Any(n.StartsWith), $"Core references {n}"));
    }
}

public class HistoryTests
{
    private static (Building, BuildingActions, BuildingHistory) New()
    {
        var b = new Building(); var h = new BuildingHistory();
        return (b, new BuildingActions(b, h, new BuildingEventsService()), h);
    }

    [Fact]
    public void Actions_undo_restores_model_exactly()
    {
        var (b, a, h) = New();
        var l = a.AddLevel("RDC", 0);
        var steps = new List<Action>
        {
            () => a.AddLevel("R+1", 2700),
            () => a.RenameLevel(l.Id, "Ground"),
            () => a.SetLevelGeometry(l.Id, 100, 2800),
            () => { var w = a.AddWall(l.Id, new[] { new PointMm(0, 0), new PointMm(100, 0), new PointMm(100, 10) }); a.MoveWall(l.Id, w.Id, 5, 5); a.DeleteWall(l.Id, w.Id); },
            () => a.ImportWalls(l.Id, new[] { new[] { new PointMm(0, 0), new PointMm(1, 1) } }),
            () => a.SetImageTransform(l.Id, new[] { 1.0, 0, 0, 0, 1, 0 }),
            () => a.AddSpace(l.Id, new[] { new PointMm(0, 0) }, "Cave 1", SpaceKind.Cellar, "1"),
            () => { var n = a.AddNetwork("ECS", NetworkType.DomesticHotWater); a.AddPipeRun(n.Id, new[] { new PointMm3(0, 0, 0), new PointMm3(0, 0, 2700) }); },
            () => a.AddEquipment(l.Id, "radiator", new PointMm(10, 10), 150),
            () => a.DeleteLevel(l.Id),
        };
        foreach (var s in steps)
        {
            var before = BuildingJson.Serialize(b);
            var count = h.Items.Count;
            s();
            var after = BuildingJson.Serialize(b);
            while (h.Items.Count > count && h.CanUndo && h.Pointer >= count) h.Undo(b);
            Assert.Equal(before, BuildingJson.Serialize(b));
            while (h.CanRedo) h.Redo(b);
            Assert.Equal(after, BuildingJson.Serialize(b));
        }
    }

    [Fact]
    public void Two_hundred_undo_redo_cycles_on_ten_levels_stay_fast_and_exact()
    {
        var (b, h) = SampleBuilding.Create(); var a = new BuildingActions(b, h, new BuildingEventsService());
        for (var i = 3; i < 10; i++) a.AddLevel("R+" + i, i * 2700);
        var before = BuildingJson.Serialize(b); var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) { a.AddWall(b.Levels[0].Id, [new(0, 0), new(100, 0), new(100, 100)]); h.Undo(b); h.Redo(b); h.Undo(b); }
        Assert.Equal(before, BuildingJson.Serialize(b)); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"{sw.Elapsed}");
    }

    [Fact]
    public void Dirty_follows_pointer()
    {
        var (b, a, h) = New();
        a.AddLevel("RDC", 0); h.MarkClean(); Assert.False(h.IsDirty);
        a.AddLevel("R+1", 2700); Assert.True(h.IsDirty);
        h.Undo(b); Assert.False(h.IsDirty);
    }

    [Fact]
    public void File_round_trip()
    {
        var (b, a, _) = New();
        var l = a.AddLevel("RDC", 0);
        a.AddWall(l.Id, new[] { new PointMm(0, 0), new PointMm(1000, 0) });
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".immeuble");
        BuildingFile.Save(b, p, new Dictionary<string, byte[]> { ["a.png"] = new byte[] { 1, 2, 3 } });
        var (b2, imgs) = BuildingFile.Load(p);
        File.Delete(p);
        Assert.Equal(BuildingJson.Serialize(b), BuildingJson.Serialize(b2));
        Assert.Equal(new byte[] { 1, 2, 3 }, imgs["a.png"]);
    }
}
