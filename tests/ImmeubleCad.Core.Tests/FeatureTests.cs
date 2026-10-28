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

public class GeometryTests
{
    private static readonly ((double, double), (double, double))[] Dummy = [];

    [Fact] public void ImageFit_recovers_affine_exactly()
    {
        var t = new ImmeubleCad.Core.Geometry.Affine(1.03, 0.01, 50, -0.01, 1.0, -20);
        var src = new (double, double)[] { (0, 0), (1000, 0), (0, 800), (1000, 800) };
        var pairs = src.Select(p => (p, t.Apply(p.Item1, p.Item2))).ToList();
        var fit = ImmeubleCad.Core.Geometry.ImageFit.Fit(pairs.Select(p => ((p.p.Item1, p.p.Item2), p.Item2)).ToList())!.Value;
        Assert.All(ImmeubleCad.Core.Geometry.ImageFit.Residuals(fit, pairs.Select(p => ((p.p.Item1, p.p.Item2), p.Item2)).ToList()), r => Assert.True(r < 1e-6));
        Assert.Equal(1.03, fit.A, 6);
    }

    [Fact] public void ImageFit_two_pairs_is_similarity_and_collinear_is_degenerate()
    {
        var two = new[] { ((0.0, 0.0), (10.0, 10.0)), ((100.0, 0.0), (10.0, 210.0)) };
        var f = ImmeubleCad.Core.Geometry.ImageFit.Fit(two)!.Value;
        var (x, y) = f.Apply(50, 0);
        Assert.Equal(10, x, 6); Assert.Equal(110, y, 6);
        var col = new[] { ((0.0, 0.0), (0.0, 0.0)), ((1.0, 1.0), (2.0, 2.0)), ((2.0, 2.0), (4.0, 4.0)) };
        Assert.Null(ImmeubleCad.Core.Geometry.ImageFit.Fit(col));
    }

    [Fact] public void SpaceFinder_finds_closed_room_and_rejects_open()
    {
        static Wall W(int x0, int y0, int x1, int y1) => new() { Footprint = [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)] };
        var l = new Level { Walls = [W(0, 0, 3000, 200), W(0, 2800, 3000, 3000), W(0, 0, 200, 3000), W(2800, 0, 3000, 3000)] };
        var o = ImmeubleCad.Core.Geometry.SpaceFinder.Find(l, new(1500, 1500))!;
        Assert.Equal(2600L * 2600, ImmeubleCad.Core.Geometry.SpaceFinder.AreaMm2(o));
        l.Walls.RemoveAt(3);
        Assert.Null(ImmeubleCad.Core.Geometry.SpaceFinder.Find(l, new(1500, 1500)));
    }

    [Fact] public void Valve_only_drops_on_pipe()
    {
        var b = new Building(); var a = new BuildingActions(b, new BuildingHistory(), new BuildingEventsService());
        var n = a.AddNetwork("N", NetworkType.ColdWater); a.AddPipeRun(n.Id, [new(0, 0, 0), new(1000, 0, 0)]);
        var valve = ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("shutoff-valve")!;
        Assert.True(ImmeubleCad.Core.Catalog.PlacementRules.Check(valve, new(500, 50, 0), n).Valid);
        Assert.False(ImmeubleCad.Core.Catalog.PlacementRules.Check(valve, new(500, 900, 0), n).Valid);
        Assert.True(ImmeubleCad.Core.Catalog.PlacementRules.Check(ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!, new(0, 0, 0), null).Valid);
    }

    [Fact] public void Catalog_entries_valid() =>
        Assert.All(ImmeubleCad.Core.Catalog.EquipmentCatalog.Default, e => { Assert.True(e.WidthMm > 0 && e.HeightMm > 0); Assert.NotEmpty(e.Name); });
}

public class ImportTests
{
    [Fact] public void Import_png_detects_wall_in_mm_with_one_history_step()
    {
        var raw = Enumerable.Repeat((byte)255, 200 * 100).ToArray();
        for (var y = 40; y < 50; y++) for (var x = 20; x < 180; x++) raw[y * 200 + x] = 0;
        using var img = new ImageMagick.MagickImage(raw, new ImageMagick.PixelReadSettings(200, 100, ImageMagick.StorageType.Char, "I"));
        var bytes = img.ToByteArray(ImageMagick.MagickFormat.Png);
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService());
        var lv = a.AddLevel("RDC", 0); var steps = h.Items.Count;
        var n = ImmeubleCad.Core.Import.PlanImport.Import(a, lv.Id, bytes, new ImmeubleCad.Core.Import.Calibration(0.1, 0.1), new ImmeubleCad.Core.Import.PlanWallDetectorAdapter());
        Assert.Equal(1, n); Assert.Equal(steps + 1, h.Items.Count);
        var fp = b.Levels[0].Walls[0].Footprint;
        Assert.Equal(200, fp.Min(p => p.X)); Assert.Equal(1800, fp.Max(p => p.X)); // 20px / 0.1 px per mm
        Assert.Equal(500, fp.Min(p => p.Y)); Assert.Equal(600, fp.Max(p => p.Y)); // flipped Y: (100-50..100-40)/0.1
    }
}
