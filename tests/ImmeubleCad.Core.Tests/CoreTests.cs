using ImmeubleCad.Core.History;
using ImmeubleCad.Core.IO;
using ImmeubleCad.Core.Model;
using Xunit;

namespace ImmeubleCad.Core.Tests;

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
