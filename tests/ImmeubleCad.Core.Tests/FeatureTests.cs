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
        Assert.Contains("<path", SvgExporter.Export(b.Levels[0]));
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

public class ActionTests
{
    private static (Building, BuildingActions, BuildingHistory) New()
    { var b = new Building(); var h = new BuildingHistory(); return (b, new BuildingActions(b, h, new BuildingEventsService()), h); }

    private static void AssertUndoExact(Building b, BuildingHistory h, Action act)
    {
        var before = BuildingJson.Serialize(b); var n = h.Items.Count;
        act();
        Assert.Equal(n + 1, h.Items.Count);               // exactly one history step
        var after = BuildingJson.Serialize(b);
        h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
        h.Redo(b); Assert.Equal(after, BuildingJson.Serialize(b));
    }

    [Fact] public void Cellars_numbered_in_one_step_with_common_default()
    {
        var (b, a, h) = New(); var l = a.AddLevel("Cave", -2700, 2500, LevelKind.Basement);
        var ids = Enumerable.Range(0, 3).Select(i => a.AddSpace(l.Id, [new(i * 2000, 0), new(i * 2000 + 1800, 0), new(i * 2000 + 1800, 2000), new(i * 2000, 2000)], "", SpaceKind.Cellar).Id).ToList();
        Assert.All(b.Levels[0].Spaces, s => Assert.Equal(Stability.Common, s.Stability));
        AssertUndoExact(b, h, () => a.NumberSpaces(l.Id, ids, "Cave ", 12));
        Assert.Equal(["Cave 12", "Cave 13", "Cave 14"], b.Levels[0].Spaces.Select(s => s.Name));
    }

    [Fact] public void Door_defaults_by_level_kind_and_flip()
    {
        var (b, a, h) = New(); var l = a.AddLevel("Cave", 0, 2500, LevelKind.Basement); var w = a.AddWall(l.Id, [new(0, 0), new(2000, 0), new(2000, 200), new(0, 200)]);
        var d = a.AddOpening(l.Id, w.Id, OpeningKind.Door, 500);
        Assert.Equal(700, d.WidthMm);
        AssertUndoExact(b, h, () => a.FlipOpening(l.Id, w.Id, d.Id));
        Assert.True(w.Openings[0].SwingLeft);
    }

    [Fact] public void Straighten_walls_one_step_and_reduces_points()
    {
        var (b, a, h) = New(); var l = a.AddLevel("RDC", 0);
        var w = a.AddWall(l.Id, [new(0, 0), new(2000, 10), new(4000, 0), new(4010, 1000), new(4000, 2000), new(2000, 1990), new(0, 2000), new(0, 1000)]);
        AssertUndoExact(b, h, () => a.StraightenWalls(l.Id, [w.Id], 30, 5));
        Assert.Equal(4, b.Levels[0].Walls[0].Footprint.Count);
    }

    [Fact] public void Riser_template_creates_n_networks_one_step()
    {
        var (b, a, h) = New();
        AssertUndoExact(b, h, () => a.AddRiserTemplate(new(100, 100), 150, [NetworkType.ColdWater, NetworkType.DomesticHotWater, NetworkType.Drainage], 0, 8100));
        Assert.Equal(3, b.Networks.Count); Assert.Equal(250, b.Networks[1].Nodes[0].Position.X);
    }

    [Fact] public void Connect_routes_orthogonally()
    {
        var (b, a, h) = New(); var n = a.AddNetwork("H", NetworkType.HeatingSupply);
        var riser = a.AddRiserTemplate(new(0, 0), 100, [NetworkType.HeatingSupply], 0, 2700)[0];
        var target = riser.Nodes[0];
        AssertUndoExact(b, h, () => a.ConnectOrthogonal(riser.Id, new(1000, 500, 500), target.Id));
        Assert.Empty(FittingResolver.Validate(riser).Where(w => w.Contains("axis-aligned")));
    }

    [Fact] public void Spaces_csv_and_common_filter_and_crossing()
    {
        var (b, a, _) = New(); var l = a.AddLevel("Cave", 0, 2500, LevelKind.Basement);
        a.AddSpace(l.Id, [new(0, 0), new(2000, 0), new(2000, 2000), new(0, 2000)], "Cave 1", SpaceKind.Cellar, "1");
        var up = a.AddLevel("R+1", 2700);
        a.AddSpace(up.Id, [new(3000, 0), new(5000, 0), new(5000, 2000), new(3000, 2000)], "Salon", SpaceKind.Apartment);
        Assert.Contains("Cave,Cellar,1,Cave 1,,4,Common", Spaces.ToCsv(b).Replace("\r", ""));
        Assert.Contains("R+1,Apartment,,Salon,,4,Private", Spaces.ToCsv(b).Replace("\r", ""));
        Assert.Single(Spaces.CommonSpaces(b.Levels[0])); Assert.Empty(Spaces.CommonSpaces(b.Levels[1]));
        var r = a.AddRiserTemplate(new(1000, 1000), 0, [NetworkType.ColdWater], 0, 2500)[0];
        Assert.Equal("Cave 1", Assert.Single(Spaces.CrossedBy(b, r, r.Segments[0])).Name);
    }
}

public class SiteTests
{
    [Fact] public void Lambert93_known_point_and_round_trip()
    {
        // Tour Eiffel ≈ (648 240 E, 6 862 270 N)
        var (e, n) = ImmeubleCad.Core.Site.Lambert93.FromWgs84(48.8584, 2.2945);
        Assert.InRange(e, 648100, 648400); Assert.InRange(n, 6862100, 6862400);
        var (lat, lon) = ImmeubleCad.Core.Site.Lambert93.ToWgs84(e, n);
        Assert.Equal(48.8584, lat, 8); Assert.Equal(2.2945, lon, 8);
        // IGN reference: lon 3°, lat 46.5° maps to (700000, 6600000)
        var (e0, n0) = ImmeubleCad.Core.Site.Lambert93.FromWgs84(46.5, 3.0);
        Assert.Equal(700000, e0, 3); Assert.Equal(6600000, n0, 3);
    }

    [Fact] public void Anchor_round_trip_with_rotation()
    {
        var a = new ImmeubleCad.Core.Site.SiteAnchor(650000, 6860000, 30);
        var (e, n) = a.ToLambert(10000, 5000);
        Assert.Equal((10000, 5000), a.FromLambert(e, n));
    }
}

public class SettingsTests
{
    [Fact] public void Settings_round_trip_recent_and_corrupt_file_is_ignored()
    {
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "/settings.json");
        var s = new AppSettings(); for (var i = 0; i < 12; i++) s.AddRecent("f" + i); s.AddRecent("f5");
        s.RiserTemplates.Add(new("ECS+EF", 150, [NetworkType.DomesticHotWater, NetworkType.ColdWater]));
        SettingsStore.Save(s, p);
        var r = SettingsStore.Load(p);
        Assert.Equal(10, r.RecentFiles.Count); Assert.Equal("f5", r.RecentFiles[0]); Assert.Equal(150, r.RiserTemplates[0].SpacingMm);
        File.WriteAllText(p, "{ not json"); Assert.Equal("fr", SettingsStore.Load(p).Language);
    }

    [Fact] public void Autosave_offers_recovery_only_after_a_crash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "as" + Guid.NewGuid().ToString("N")); var svc = new AutosaveService(dir);
        var b = new Building { Name = "x" };
        svc.MarkStarted(); svc.Save(b); Assert.True(svc.HasRecovery); Assert.Equal("x", svc.Recover()!.Name);
        svc.MarkCleanExit(); Assert.False(svc.HasRecovery);
    }

    [Fact] public void Sample_building_is_complete_and_clean()
    {
        var (b, h) = SampleBuilding.Create();
        Assert.Equal(3, b.Levels.Count); Assert.Equal(2, b.Networks.Count); Assert.Single(b.Equipment); Assert.False(h.IsDirty);
        Assert.Empty(FittingResolver.Validate(b.Networks[0]).Where(w => w.Contains("axis")));
    }
}

public class WallMoveTests
{
    private static (Building, BuildingActions, BuildingHistory) New()
    { var b = new Building(); var h = new BuildingHistory(); return (b, new BuildingActions(b, h, new BuildingEventsService()), h); }

    [Fact] public void Thirty_levels_stay_sorted_and_undo_is_exact()
    {
        var (b, a, h) = New();
        for (var i = 29; i >= 0; i--) a.AddLevel(i == 0 ? "RDC" : "R+" + i, i * 2700);
        Assert.Equal(30, b.Levels.Count); Assert.Equal("RDC", b.Levels[0].Name); Assert.Equal("R+29", b.Levels[^1].Name);
        for (var i = 0; i < 30; i++) h.Undo(b);
        Assert.Empty(b.Levels);
    }

    [Fact] public void Move_group_within_level_and_between_levels_is_one_step()
    {
        var (b, a, h) = New(); var l0 = a.AddLevel("RDC", 0); var l1 = a.AddLevel("R+1", 2700);
        var w1 = a.AddWall(l0.Id, [new(0, 0), new(1000, 0), new(1000, 200), new(0, 200)]);
        var w2 = a.AddWall(l0.Id, [new(0, 0), new(200, 0), new(200, 1000), new(0, 1000)]);
        var n = h.Items.Count; var before = BuildingJson.Serialize(b);
        a.MoveWalls(l0.Id, [w1.Id, w2.Id], 500, -100);
        Assert.Equal(n + 1, h.Items.Count); Assert.Equal(new PointMm(500, -100), l0.Walls[0].Footprint[0]);
        a.MoveWalls(l0.Id, [w1.Id, w2.Id], 0, 0, l1.Id);
        Assert.Empty(l0.Walls); Assert.Equal(2, l1.Walls.Count);
        h.Undo(b); h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
        a.MoveWalls(l0.Id, [w1.Id], 0, 0, l1.Id); h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
    }

    [Fact] public void Copy_and_delete_walls()
    {
        var (b, a, h) = New(); var l0 = a.AddLevel("RDC", 0); var l1 = a.AddLevel("R+1", 2700); var l2 = a.AddLevel("R+2", 5400);
        var w = a.AddWall(l0.Id, [new(0, 0), new(1000, 0), new(1000, 200)]);
        a.CopyWalls(l0.Id, [w.Id], [l1.Id, l2.Id]);
        Assert.Single(l1.Walls); Assert.Single(l2.Walls); Assert.NotEqual(w.Id, l1.Walls[0].Id);
        a.DeleteWalls(l1.Id, [l1.Walls[0].Id]); Assert.Empty(l1.Walls);
        h.Undo(b); Assert.Single(b.Levels[1].Walls); // undo restores a fresh object graph
    }
}
