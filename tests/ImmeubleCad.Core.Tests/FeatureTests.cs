using ImmeubleCad.Core;
using ImmeubleCad.Core.Export;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Networks;
using ImmeubleCad.Core.Tools;
using Xunit;

namespace ImmeubleCad.Core.Tests;

public class FeatureTests
{
    private static (Building, BuildingActions) New()
    { var b = new Building(); return (b, new BuildingActions(b, new BuildingHistory(), new BuildingEventsService())); }

    private static Building Sample()
    {
        var (b, a) = New(); b.Name = "Test";
        var l = a.AddLevel("RDC", 0);
        a.AddWall(l.Id, [new(0, 0), new(4000, 0), new(4000, 200), new(0, 200)]);
        var n = a.AddNetwork("ECS", NetworkType.DomesticHotWater);
        a.AddPipeRun(n.Id, [new(0, 0, 0), new(0, 0, 2700), new(1000, 0, 2700)], 20);
        return b;
    }

    [Fact] public void Fittings_end_elbow()
    {
        var net = Sample().Networks[0];
        var k = FittingResolver.Resolve(net).Select(f => f.Kind).ToList();
        Assert.Equal(2, k.Count(x => x == FittingKind.EndCap));
        Assert.Single(k, FittingKind.Elbow90);
    }

    [Fact] public void Fittings_tee_reducer_and_cross()
    {
        var (b, a) = New(); var n = a.AddNetwork("N", NetworkType.ColdWater);
        var c = a.AddNode(n.Id, new(0, 0, 0));
        foreach (var p in new PointMm3[] { new(100, 0, 0), new(-100, 0, 0), new(0, 100, 0) })
        { var o = a.AddNode(n.Id, p); a.AddSegment(n.Id, c.Id, o.Id); }
        Assert.Equal(FittingKind.Tee, FittingResolver.Resolve(n).First(f => f.NodeId == c.Id).Kind);
        var o4 = a.AddNode(n.Id, new(0, -100, 0)); a.AddSegment(n.Id, c.Id, o4.Id);
        Assert.Equal(FittingKind.Cross, FittingResolver.Resolve(n).First(f => f.NodeId == c.Id).Kind);

        var r = a.AddNetwork("R", NetworkType.ColdWater);
        var m = a.AddNode(r.Id, new(0, 0, 0)); var l1 = a.AddNode(r.Id, new(-100, 0, 0)); var r1 = a.AddNode(r.Id, new(100, 0, 0));
        a.AddSegment(r.Id, l1.Id, m.Id, 20); a.AddSegment(r.Id, m.Id, r1.Id, 15);
        Assert.Equal(FittingKind.Reducer, FittingResolver.Resolve(r).First(f => f.NodeId == m.Id).Kind);
    }

    [Fact] public void Validate_flags_diagonal_segment()
    {
        var (_, a) = New(); var n = a.AddNetwork("N", NetworkType.ColdWater);
        a.AddPipeRun(n.Id, [new(0, 0, 0), new(100, 100, 0)]);
        Assert.Contains(FittingResolver.Validate(n), w => w.Contains("axis-aligned"));
    }

    [Fact] public void Bom_sums_lengths()
    {
        var p = Assert.Single(Bom.Pipes(Sample()), x => x.DiameterMm == 20);
        Assert.Equal(3.7, p.LengthM, 3);
    }

    [Fact] public void Exporters_produce_expected_content()
    {
        var b = Sample();
        Assert.Contains("o level_RDC", new ObjExporter().Export(b));
        Assert.Contains("LAYER", "LAYER"); Assert.Contains("WALLS_RDC", new DxfExporter().Export(b));
        Assert.Contains("makeCylinder", new FreeCadMacroExporter().Export(b));
        var ifc = new IfcExporter().Export(b);
        Assert.Contains("IFCBUILDINGSTOREY", ifc); Assert.Contains("IFCWALL", ifc); Assert.Contains("IFCPIPESEGMENT", ifc);
        Assert.Contains("<polygon", SvgExporter.Export(b.Levels[0]));
    }

    [Fact] public void Snapper_prefers_corner_then_grid()
    {
        var s = new Snapper();
        var poly = new List<PointMm> { new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000) };
        Assert.Equal(new SnapResult(new(0, 0), SnapKind.Corner), s.Snap(new(50, 40), [poly]));
        Assert.Equal(SnapKind.Midpoint, s.Snap(new(500, 30), [poly]).Kind);
        Assert.Equal(new SnapResult(new(2000, 3000), SnapKind.Grid), s.Snap(new(2030, 2980), []));
        Assert.Equal(SnapKind.Orthogonal, s.Snap(new(5000, 70), [], new PointMm(0, 0)).Kind);
    }

    [Fact] public void Tool_click_mode_chain_is_one_commit()
    {
        var runs = new List<IReadOnlyList<PointMm>>();
        var t = new DrawChainTool(runs.Add);
        ToolPointer P(int x, int y) => new(new(x, y), x, y);
        t.Down(P(0, 0)); t.Up(P(0, 0));
        Assert.Equal(ToolState.Placing, t.State);
        t.Move(P(100, 0)); t.Down(P(100, 0)); t.Move(P(100, 100)); t.Down(P(100, 100));
        t.Finish();
        Assert.Single(runs);
        Assert.Equal(3, runs[0].Count);
    }

    [Fact] public void Tool_drag_mode_commits_on_release_and_threshold_respected()
    {
        var runs = new List<IReadOnlyList<PointMm>>();
        var t = new DrawChainTool(runs.Add) { DragThresholdPx = 4 };
        ToolPointer P(int x) => new(new(x, 0), x, 0);
        t.Down(P(0)); t.Move(P(3)); Assert.Equal(ToolState.Pressed, t.State);
        t.Move(P(10)); Assert.Equal(ToolState.Dragging, t.State);
        t.Up(P(500));
        Assert.Equal(ToolState.Committed, t.State);
        Assert.Equal(new PointMm(500, 0), runs[0][1]);
    }

    [Theory] [InlineData("2.4", 2400)] [InlineData("2400mm", 2400)] [InlineData("1,25", 1250)]
    public void Typed_length_parses(string s, int mm) { Assert.True(DrawChainTool.TryParseLength(s, out var v)); Assert.Equal(mm, v); }

    [Fact] public void ViewState_raises_events()
    {
        var v = new ViewState(); var seen = new List<string>(); v.ViewStateChanged += seen.Add;
        v.SetMode(RenderMode.Solid); v.SetScale(2); v.Select(Guid.NewGuid());
        Assert.Equal(["Mode", "Scale", "Selection"], seen);
    }
}
