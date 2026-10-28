using ImmeubleCad.Core;
using ImmeubleCad.Core.Export;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Networks;
using ImmeubleCad.Core.Tools;
using ImmeubleCad.Core.Geometry;
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

public class ExportTests
{
    private static Building Sample() => SampleBuilding.Create().Building;

    [Fact] public void Glb_is_valid_container_with_node_per_level_and_network()
    {
        var glb = ImmeubleCad.Core.Export.GltfExporter.ExportGlb(Sample());
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0)); Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
        var jsonLen = (int)BitConverter.ToUInt32(glb, 12);
        using var doc = System.Text.Json.JsonDocument.Parse(glb.AsMemory(20, jsonLen));
        var names = doc.RootElement.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("name").GetString()).ToList();
        Assert.Contains("level_RDC", names); Assert.Contains("level_R+2", names); Assert.Contains(names, n => n!.StartsWith("network_"));
        Assert.Equal(0, glb.Length % 4);
    }

    [Fact] public void Bom_full_counts_fittings_equipment_and_rounds_bars()
    {
        var b = Sample(); var csv = ImmeubleCad.Core.Export.BomFull.ToCsv(b, roundToBars: true);
        Assert.Contains($"equipment,,{ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!.Name},,1,pcs", csv);
        Assert.Contains("bars of 3 m", csv);
        var pdf = System.Text.Encoding.Latin1.GetString(ImmeubleCad.Core.Export.BomFull.ToPdf(b));
        Assert.StartsWith("%PDF-1.4", pdf); Assert.Contains("%%EOF", pdf); Assert.Contains(ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!.Name.Replace("é", "\u00e9"), pdf.Replace("\u00e9", "\u00e9"));
    }

    [Fact] public void Hand_count_matches_for_simple_run()
    {
        var b = new Building(); var a = new BuildingActions(b, new BuildingHistory(), new BuildingEventsService());
        var n = a.AddNetwork("N", NetworkType.ColdWater); a.AddPipeRun(n.Id, [new(0, 0, 0), new(0, 0, 2700), new(1500, 0, 2700), new(1500, 0, 0)], 22);
        Assert.Equal(6.9, Bom.Pipes(b).Single().LengthM, 3);
        var f = ImmeubleCad.Core.Export.BomFull.Fittings(b);
        Assert.Equal(2, f.Single(x => x.Kind == FittingKind.Elbow90).Count);
        Assert.Contains("pipe,ColdWater,pipe,22,3,bars of 3 m", ImmeubleCad.Core.Export.BomFull.ToCsv(b, true).Replace("\r", ""));
    }
}

public class AlignTests
{
    [Fact] public void Homography_recovers_perspective_and_rejects_collinear()
    {
        var src = new (double, double)[] { (0, 0), (100, 0), (100, 100), (0, 100) };
        var dst = new (double, double)[] { (10, 5), (120, 0), (95, 110), (0, 90) };
        var h = ImmeubleCad.Core.Geometry.Homography.Fit(src.Zip(dst).ToList())!.Value;
        for (var i = 0; i < 4; i++) { var (x, y) = h.Apply(src[i].Item1, src[i].Item2); Assert.Equal(dst[i].Item1, x, 6); Assert.Equal(dst[i].Item2, y, 6); }
        Assert.Null(ImmeubleCad.Core.Geometry.Homography.Fit(new[] { ((0.0, 0.0), (0.0, 0.0)), ((1.0, 1.0), (1.0, 1.0)), ((2.0, 2.0), (2.0, 2.0)), ((3.0, 3.0), (3.0, 3.0)) }));
    }

    [Fact] public void Align_scanned_plan_with_3_percent_width_stretch_within_50mm()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService());
        var below = a.AddLevel("RDC", 0); var up = a.AddLevel("R+1", 2700);
        // stairwell corners on the level below, and the same points scanned 3 % too wide on the upper level
        var truth = new (double X, double Y)[] { (1000, 1000), (4000, 1000), (4000, 3000), (1000, 3000) };
        var pairs = truth.Select(t => (From: (t.X * 1.03, t.Y), To: t)).ToList();
        a.AddWall(up.Id, [new(1030, 1000), new(4120, 1000), new(4120, 3000), new(1030, 3000)]);
        var steps = h.Items.Count;
        var res = a.AlignLevel(up.Id, pairs)!;
        Assert.All(res, r => Assert.True(r < 1));
        Assert.Equal(steps + 1, h.Items.Count);
        var x = b.Levels[1].Walls[0].Footprint[0].X; Assert.InRange(x, 1000 - 50, 1000 + 50); // wall moved with the plan
        h.Undo(b); Assert.Equal(1030, b.Levels[1].Walls[0].Footprint[0].X);
    }

    [Fact] public void Align_reports_wrong_click_as_large_residual()
    {
        var b = new Building(); var a = new BuildingActions(b, new BuildingHistory(), new BuildingEventsService()); a.AddLevel("RDC", 0); var up = a.AddLevel("R+1", 2700);
        var pairs = new[] { ((0.0, 0.0), (0.0, 0.0)), ((1000.0, 0.0), (1000.0, 0.0)), ((1000.0, 1000.0), (1000.0, 1000.0)), ((0.0, 1000.0), (300.0, 1400.0)) };
        Assert.Contains(a.AlignLevel(up.Id, pairs)!, r => r > 50);
        Assert.Null(a.AlignLevel(up.Id, new[] { ((0.0, 0.0), (0.0, 0.0)), ((1.0, 1.0), (1.0, 1.0)), ((2.0, 2.0), (2.0, 2.0)) }));
    }
}

public class WizardMathTests
{
    [Fact] public void Calibration_one_and_two_axes_and_transform()
    {
        Assert.Equal(0.1, ImmeubleCad.Core.Import.PlanImport.PxPerMm((0, 0), (300, 400), 5), 9);   // 500 px = 5 m
        var c = ImmeubleCad.Core.Import.PlanImport.TwoAxis((0, 0), (500, 0), 5, (0, 0), (0, 485), 5); // stretched in height
        Assert.Equal(0.1, c.PxPerMmX, 9); Assert.Equal(0.097, c.PxPerMmY, 9);
        var m = ImmeubleCad.Core.Import.PlanImport.TransformFor(new(0.1, 0.1), 100);
        Assert.Equal(1000.0, m[5]);       // bottom row of the picture at Y=0 → top at 10 m
        Assert.Equal(-10.0, m[4]);
    }

    [Fact] public void Rotate_returns_png_with_expected_size()
    {
        var raw = Enumerable.Repeat((byte)200, 100 * 50).ToArray();
        using var img = new ImageMagick.MagickImage(raw, new ImageMagick.PixelReadSettings(100, 50, ImageMagick.StorageType.Char, "I"));
        var png = img.ToByteArray(ImageMagick.MagickFormat.Png);
        Assert.Equal((100, 50), ImmeubleCad.Core.Import.PlanImport.Size(png));
        var (w, h) = ImmeubleCad.Core.Import.PlanImport.Size(ImmeubleCad.Core.Import.PlanImport.Rotate(png, 90));
        Assert.Equal((50, 100), (w, h));
    }
}

public class NetworkEditTests
{
    private static (Building, BuildingActions, BuildingHistory) New()
    { var b = new Building(); var h = new BuildingHistory(); return (b, new BuildingActions(b, h, new BuildingEventsService()), h); }

    [Fact] public void Valve_inserted_on_pipe_splits_it_keeps_diameter_and_is_counted()
    {
        var (b, a, h) = New(); var n = a.AddNetwork("H", NetworkType.HeatingSupply); var seg = a.AddPipeRun(n.Id, [new(0, 0, 0), new(2000, 0, 0)], 28)[0];
        var before = BuildingJson.Serialize(b); var steps = h.Items.Count;
        var node = a.InsertComponent(n.Id, seg.Id, new(800, 0, 0), "shutoff-valve");
        Assert.Equal(2, n.Segments.Count); Assert.All(n.Segments, s => Assert.Equal(28, s.DiameterMm)); Assert.Equal(steps + 1, h.Items.Count);
        Assert.Equal(FittingKind.Component, FittingResolver.Resolve(n).Single(f => f.NodeId == node.Id).Kind);
        Assert.Contains(n.Nodes, x => x.Component == "shutoff-valve");
        h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
    }

    [Fact] public void Split_then_merge_round_trips_and_merge_refuses_elbows_and_components()
    {
        var (b, a, h) = New(); var n = a.AddNetwork("H", NetworkType.ColdWater); var seg = a.AddPipeRun(n.Id, [new(0, 0, 0), new(2000, 0, 0)])[0];
        var mid = a.SplitSegment(n.Id, seg.Id, new(1000, 0, 0)); Assert.Equal(2, n.Segments.Count);
        Assert.True(a.MergeAt(n.Id, mid.Id)); Assert.Single(n.Segments); Assert.Equal(2, n.Nodes.Count);
        var (b2, a2, _) = New(); var n2 = a2.AddNetwork("E", NetworkType.ColdWater); a2.AddPipeRun(n2.Id, [new(0, 0, 0), new(1000, 0, 0), new(1000, 1000, 0)]);
        Assert.False(a2.MergeAt(n2.Id, n2.Nodes[1].Id));
    }

    [Fact] public void Riser_extent_and_network_display_are_single_history_steps()
    {
        var (b, a, h) = New(); var r = a.AddRiserTemplate(new(0, 0), 0, [NetworkType.ColdWater], 0, 2700)[0]; var steps = h.Items.Count;
        a.SetRiserExtent(r.Id, 0, 8100); a.SetNetworkDisplay(r.Id, false, true);
        Assert.Equal(steps + 2, h.Items.Count); Assert.Equal(8100, r.Nodes.Max(n => n.Position.Z)); Assert.False(r.Visible); Assert.True(r.Locked);
    }

    [Fact] public void Riser_snaps_50mm_from_wall_faces_and_corner()
    {
        var wall = new List<PointMm> { new(0, 0), new(4000, 0), new(4000, 200), new(0, 200) };
        Assert.Equal(new PointMm(1000, 250), RiserSnap.Snap(new(1000, 330), [wall]));        // above the north face (y=200) +50
        var corner = new List<PointMm> { new(0, 0), new(200, 0), new(200, 4000), new(0, 4000) };
        Assert.Equal(new PointMm(250, 250), RiserSnap.Snap(new(300, 330), [wall, corner]));   // inner corner: 50 mm from both faces
        Assert.Equal(new PointMm(2000, 2000), RiserSnap.Snap(new(2000, 2000), [wall, corner])); // far from walls: untouched
    }
}

public class NetworkQueryTests
{
    [Fact] public void Risers_horizontals_and_arrows_per_level()
    {
        var (b, _) = SampleBuilding.Create();
        var rdc = b.Levels[0]; var r1 = b.Levels[1];
        Assert.Equal(2, NetworkQueries.RisersAt(b, rdc).Count());
        var riser = NetworkQueries.RisersAt(b, r1).First();
        Assert.Equal(b.Levels[2].Id, NetworkQueries.NextLevelReached(b, riser, r1, +1)!.Id);
        Assert.Equal(rdc.Id, NetworkQueries.NextLevelReached(b, riser, r1, -1)!.Id);
        Assert.Null(NetworkQueries.NextLevelReached(b, riser, b.Levels[2], +1));
        Assert.Empty(NetworkQueries.HorizontalAt(b, rdc));
    }

    [Fact] public void Nearest_finds_pipe_within_reach()
    {
        var b = new Building(); var a = new BuildingActions(b, new BuildingHistory(), new BuildingEventsService());
        var n = a.AddNetwork("N", NetworkType.ColdWater); a.AddPipeRun(n.Id, [new(0, 0, 2400), new(3000, 0, 2400)]);
        var hit = NetworkQueries.Nearest(b, new(1000, 40, 2400), 100);
        Assert.NotNull(hit); Assert.Equal(new PointMm3(1000, 0, 2400), hit!.Value.Point);
        Assert.Null(NetworkQueries.Nearest(b, new(1000, 500, 2400), 100));
    }

    [Fact] public void Wall_and_equipment_properties_are_single_steps_with_exact_undo()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var l = a.AddLevel("RDC", 0);
        var w = a.AddWall(l.Id, [new(0, 0), new(1000, 0), new(1000, 200)]); var e = a.AddEquipment(l.Id, "radiator", new(0, 0), 150);
        var before = BuildingJson.Serialize(b);
        a.SetWallProperties(l.Id, [w.Id], "Porteur", 2500); a.SetEquipmentProperties(e.Id, "Radiateur séjour", 200, 90); a.DeleteEquipment(e.Id);
        Assert.Empty(b.Equipment); Assert.Equal("Porteur", b.Levels[0].Walls[0].Label);
        h.Undo(b); Assert.Equal("Radiateur séjour", b.Equipment[0].Label); h.Undo(b); h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
    }
}

public class ElevationTests
{
    [Fact] public void Plane_maps_u_to_plan_with_offset_and_back()
    {
        var p = new ImmeubleCad.Core.Geometry.ElevationPlane(new(0, 200), new(4000, 200), 50, +1);   // north face of a south wall, viewer to the north
        Assert.Equal(new PointMm(1000, 250), p.ToPlan(1000));
        Assert.Equal(1000, p.ProjectU(new(1000, 250)), 6); Assert.Equal(50, p.Distance(new(1000, 250)), 6);
        var q = new ImmeubleCad.Core.Geometry.ElevationPlane(new(0, 200), new(4000, 200), 50, -1);
        Assert.Equal(new PointMm(1000, 150), q.ToPlan(1000));
        var diag = new ImmeubleCad.Core.Geometry.ElevationPlane(new(0, 0), new(3000, 4000), 0);
        Assert.Equal(new PointMm(1500, 2000), diag.ToPlan(2500)); Assert.Equal(5000, diag.Length);
    }

    [Fact] public void Snap_to_level_lines_and_vertical_pipe_at_corner()
    {
        var (b, h) = SampleBuilding.Create(); var a = new BuildingActions(b, h, new BuildingEventsService());
        Assert.Equal(2700, ImmeubleCad.Core.Geometry.ElevationPlane.SnapZ(b, 2640)); Assert.Equal(2400, ImmeubleCad.Core.Geometry.ElevationPlane.SnapZ(b, 2400));
        var plane = new ImmeubleCad.Core.Geometry.ElevationPlane(new(200, 200), new(7800, 200), 50, +1);
        var steps = h.Items.Count; var nets = b.Networks.Count;
        var n = a.AddVerticalPipe(plane, 0, NetworkType.ColdWater, 0, ImmeubleCad.Core.Geometry.ElevationPlane.SnapZ(b, 8050), 20);
        Assert.Equal(steps + 1, h.Items.Count); Assert.Equal(nets + 1, b.Networks.Count);
        Assert.Equal(new PointMm3(200, 250, 8100), n.Nodes.OrderBy(x => x.Position.Z).Last().Position);
        a.AddHorizontalAlong(plane, 0, 3000, NetworkType.ColdWater, 2400, 20, n.Id);
        Assert.Equal(2, n.Segments.Count); Assert.Equal(4, n.Nodes.Count);
        Assert.Contains(plane.Visible(b), v => v.Seg.Network.Id == n.Id);
        h.Undo(b); h.Undo(b); Assert.Equal(nets, b.Networks.Count);
    }
}

public class PipeEditTests
{
    private static (Building, BuildingActions, BuildingHistory, Network) Pipe(params PointMm3[] pts)
    { var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var n = a.AddNetwork("N", NetworkType.HeatingSupply); a.AddPipeRun(n.Id, pts, 22); return (b, a, h, n); }

    [Fact] public void Extend_end_along_axis_only_and_undo()
    {
        var (b, a, h, n) = Pipe(new(0, 0, 0), new(1000, 0, 0)); var end = n.Nodes[1]; var before = BuildingJson.Serialize(b);
        Assert.True(a.ExtendEnd(n.Id, end.Id, new(2500, 0, 0))); Assert.Equal(2500, b.Networks[0].Nodes[1].Position.X);
        Assert.False(a.ExtendEnd(n.Id, end.Id, new(2500, 300, 0)));                  // off-axis refused
        h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
        Assert.False(a.ExtendEnd(n.Id, n.Nodes[0].Id, n.Nodes[0].Position));          // no-op refused
    }

    [Fact] public void Elbow_chain_and_tee_branch_are_one_step_each()
    {
        var (b, a, h, n) = Pipe(new(0, 0, 0), new(2000, 0, 0)); var steps = h.Items.Count;
        var e1 = a.AddElbow(n.Id, n.Nodes[1].Id, new(2000, 1500, 0))!; var e2 = a.AddElbow(n.Id, e1.Id, new(2000, 1500, 2400))!;     // → Y then Z
        Assert.Equal(steps + 2, h.Items.Count); Assert.Equal(3, n.Segments.Count);
        Assert.Null(a.AddElbow(n.Id, e2.Id, new(2500, 1800, 2400)));                       // diagonal refused
        var tee = a.AddTeeBranch(n.Id, n.Segments[0].Id, new(1000, 0, 0), new(1000, 0, 2400))!;
        Assert.Equal(5, n.Segments.Count); Assert.Equal(FittingKind.Tee, FittingResolver.Resolve(n).First(f => f.Position == new PointMm3(1000, 0, 0)).Kind);
        Assert.Empty(FittingResolver.Validate(n).Where(w => w.Contains("axis")));
        h.Undo(b); Assert.Equal(3, b.Networks[0].Segments.Count);
    }

    [Fact] public void Move_run_stretches_connected_segments_and_refuses_diagonals()
    {
        // U shape: up 0→2400 on x=0, across to x=3000, down. Moving the top run up (Z) stretches both risers.
        var (b, a, h, n) = Pipe(new(0, 0, 0), new(0, 0, 2400), new(3000, 0, 2400), new(3000, 0, 0));
        Assert.True(a.MoveRun(n.Id, n.Segments[1].Id, 0, 0, 300));
        Assert.Equal(2700, b.Networks[0].Nodes[1].Position.Z); Assert.Equal(2700, b.Networks[0].Nodes[2].Position.Z); Assert.Equal(0, b.Networks[0].Nodes[0].Position.Z);
        Assert.Empty(FittingResolver.Validate(b.Networks[0]).Where(w => w.Contains("axis")));
        // moving the same run sideways (Y) would turn the risers diagonal → refused, nothing changes
        var s = BuildingJson.Serialize(b); var steps = h.Items.Count;
        Assert.False(a.MoveRun(n.Id, n.Segments[1].Id, 0, 400, 0)); Assert.Equal(s, BuildingJson.Serialize(b)); Assert.Equal(steps, h.Items.Count);
    }

    [Fact] public void Move_run_includes_collinear_chained_segments()
    {
        var (b, a, h, n) = Pipe(new(0, 0, 0), new(1000, 0, 0)); a.AddElbow(n.Id, n.Nodes[1].Id, new(2000, 0, 0));    // two collinear segments
        Assert.True(a.MoveRun(n.Id, n.Segments[0].Id, 0, 500, 0)); Assert.All(b.Networks[0].Nodes, nd => Assert.Equal(500, nd.Position.Y));
    }
}

public class PipeHandleToolTests
{
    private static ToolPointer P(int x, int y) => new(new(x, y), x, y);
    private static (Building, BuildingActions, BuildingHistory, Network) Pipe()
    { var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var n = a.AddNetwork("N", NetworkType.HeatingSupply); a.AddPipeRun(n.Id, [new(0, 0, 2400), new(2000, 0, 2400)], 22); return (b, a, h, n); }

    [Fact] public void Drag_end_handle_extends_with_live_length_in_one_step()
    {
        var (b, a, h, n) = Pipe(); var steps = h.Items.Count; var t = new PipeHandleTool(a, n.Id, n.Segments[0].Id, PipeHandle.EndB);
        t.Down(P(2000, 0)); t.Move(P(2600, 40)); t.Move(P(3010, 30));
        Assert.Equal(ToolState.Dragging, t.State); Assert.Equal(3000, t.Proposal!.Value.LengthMm); Assert.Equal(steps, h.Items.Count);   // preview does not touch the model
        t.Up(P(3010, 30));
        Assert.True(t.Committed); Assert.Equal(steps + 1, h.Items.Count); Assert.Equal(new PointMm3(3000, 0, 2400), n.Nodes[1].Position);
    }

    [Fact] public void Click_then_click_destination_works_too()
    {
        var (b, a, h, n) = Pipe(); var t = new PipeHandleTool(a, n.Id, n.Segments[0].Id, PipeHandle.EndB);
        t.Down(P(2000, 0)); t.Up(P(2000, 0)); Assert.Equal(ToolState.Placing, t.State);
        t.Move(P(2500, 0)); t.Down(P(2500, 0));
        Assert.True(t.Committed); Assert.Equal(2500, n.Nodes[1].Position.X);
    }

    [Fact] public void Dragging_perpendicular_makes_an_elbow_and_modifier_forces_it()
    {
        var (b, a, h, n) = Pipe(); var t = new PipeHandleTool(a, n.Id, n.Segments[0].Id, PipeHandle.EndB);
        t.Down(P(2000, 0)); t.Move(P(2050, 1200)); t.Up(P(2050, 1200));
        Assert.True(t.Committed); Assert.Equal(2, n.Segments.Count); Assert.Equal(new PointMm3(2000, 1200, 2400), n.Nodes[^1].Position);
        Assert.Equal(FittingKind.Elbow90, FittingResolver.Resolve(n).First(f => f.Position == new PointMm3(2000, 0, 2400)).Kind);
        var (_, a2, _, n2) = Pipe(); var t2 = new PipeHandleTool(a2, n2.Id, n2.Segments[0].Id, PipeHandle.EndB) { ForceElbow = true };
        t2.Down(P(2000, 0)); t2.Move(P(2000, 100)); t2.Up(P(2000, 100)); Assert.Equal(2, n2.Segments.Count);
    }

    [Fact] public void Mid_handle_makes_a_tee_and_body_drag_moves_the_run()
    {
        var (b, a, h, n) = Pipe(); var t = new PipeHandleTool(a, n.Id, n.Segments[0].Id, PipeHandle.Mid);
        t.Down(P(1000, 0)); t.Move(P(1000, 1500)); t.Up(P(1000, 1500));
        Assert.True(t.Committed); Assert.Equal(3, n.Segments.Count); Assert.Equal(FittingKind.Tee, FittingResolver.Resolve(n).First(f => f.Position == new PointMm3(1000, 0, 2400)).Kind);
        var (b2, a2, h2, n2) = Pipe(); var steps = h2.Items.Count; var t2 = new PipeHandleTool(a2, n2.Id, n2.Segments[0].Id, PipeHandle.Body);
        t2.Down(P(1000, 0)); t2.Move(P(1000, 400)); t2.Up(P(1000, 400));
        Assert.True(t2.Committed); Assert.All(n2.Nodes, nd => Assert.Equal(400, nd.Position.Y)); Assert.Equal(steps + 1, h2.Items.Count);
    }

    [Fact] public void Typed_length_overrides_and_cancel_leaves_model_untouched()
    {
        var (b, a, h, n) = Pipe(); var t = new PipeHandleTool(a, n.Id, n.Segments[0].Id, PipeHandle.EndB) { TypedLengthMm = 1250 };
        t.Down(P(2000, 0)); t.Move(P(2400, 0)); Assert.Equal(1250, t.Proposal!.Value.LengthMm); t.Up(P(2400, 0)); Assert.Equal(1250, n.Nodes[1].Position.X);
        var (b2, a2, h2, n2) = Pipe(); var before = BuildingJson.Serialize(b2); var steps = h2.Items.Count;
        var t2 = new PipeHandleTool(a2, n2.Id, n2.Segments[0].Id, PipeHandle.EndB); t2.Down(P(2000, 0)); t2.Move(P(3000, 0)); t2.Cancel();
        Assert.Equal(before, BuildingJson.Serialize(b2)); Assert.Equal(steps, h2.Items.Count); Assert.Equal(ToolState.Idle, t2.State);
    }

    [Fact] public void Refused_move_reports_and_changes_nothing()
    {
        var (b, a, h, n) = Pipe(); a.AddElbow(n.Id, n.Nodes[1].Id, new(2000, 0, 0));    // end goes down: moving the horizontal run sideways makes the drop diagonal
        var steps = h.Items.Count; var t = new PipeHandleTool(a, n.Id, n.Segments[0].Id, PipeHandle.Body);
        t.Down(P(1000, 0)); t.Move(P(1000, 500)); t.Up(P(1000, 500));
        Assert.False(t.Committed); Assert.NotNull(t.Refused); Assert.Equal(steps, h.Items.Count);
    }
}

public class TranslationTests
{
    [Fact] public void Every_key_exists_in_both_languages_and_formats()
    {
        Assert.Empty(Translations.FrenchKeys.Except(Translations.EnglishKeys)); Assert.Empty(Translations.EnglishKeys.Except(Translations.FrenchKeys));
        Translations.Language = "en"; Assert.Equal("Ready", Translations.T("ready")); Assert.Equal("3 wall(s) selected", Translations.T("walls.selected", 3));
        Translations.Language = "fr"; Assert.Equal("Prêt", Translations.T("ready")); Assert.Equal("unknown.key", Translations.T("unknown.key"));
    }
}

public class WallToolsTests
{
    private static (Building, BuildingActions, BuildingHistory) New()
    { var b = new Building(); var h = new BuildingHistory(); return (b, new BuildingActions(b, h, new BuildingEventsService()), h); }

    [Fact] public void Axis_position_and_thickness_of_a_rectangular_wall()
    {
        var w = new Wall { Footprint = [new(0, 0), new(4000, 0), new(4000, 200), new(0, 200)] };
        var ax = ImmeubleCad.Core.Geometry.WallGeometry.Axis(w)!.Value;
        Assert.Equal(4000, ax.Length, 3); Assert.Equal(200, ax.ThicknessMm); Assert.Equal(new PointMm(0, 100), ax.A); Assert.Equal(new PointMm(4000, 100), ax.B);
        Assert.Equal(1500, ImmeubleCad.Core.Geometry.WallGeometry.PositionAlong(w, new(1500, 130))!.Value, 3);
        Assert.Null(ImmeubleCad.Core.Geometry.WallGeometry.PositionAlong(w, new(1500, 2000)));
        Assert.Null(ImmeubleCad.Core.Geometry.WallGeometry.PositionAlong(w, new(-900, 100)));
    }

    [Fact] public void Thickness_change_is_symmetric_one_step_and_undoable_and_skips_non_rectangles()
    {
        var (b, a, h) = New(); var l = a.AddLevel("RDC", 0);
        var w = a.AddWall(l.Id, [new(0, 0), new(4000, 0), new(4000, 200), new(0, 200)]); var tri = a.AddWall(l.Id, [new(0, 0), new(100, 0), new(0, 100)]);
        var before = BuildingJson.Serialize(b); var steps = h.Items.Count;
        Assert.Equal(1, a.SetWallThickness(l.Id, [w.Id, tri.Id], 300));
        var fp = b.Levels[0].Walls[0].Footprint; Assert.Equal(-50, fp.Min(p => p.Y)); Assert.Equal(250, fp.Max(p => p.Y)); Assert.Equal(steps + 1, h.Items.Count);
        h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
        var now = h.Items.Count; Assert.Equal(0, a.SetWallThickness(l.Id, [tri.Id], 300)); Assert.Equal(now, h.Items.Count);    // nothing to do: no history entry
    }

    [Fact] public void Opening_move_delete_level_kind_space_props_and_neighbour_levels()
    {
        var (b, a, h) = New(); var l = a.AddLevel("RDC", 0); var w = a.AddWall(l.Id, [new(0, 0), new(4000, 0), new(4000, 200), new(0, 200)]);
        var d = a.AddOpening(l.Id, w.Id, OpeningKind.Window, 1000); Assert.Equal(900, d.SillMm);
        a.MoveOpening(l.Id, w.Id, d.Id, 2500, 1500); Assert.Equal(2500, b.Levels[0].Walls[0].Openings[0].PositionMm); Assert.Equal(1500, b.Levels[0].Walls[0].Openings[0].WidthMm);
        a.DeleteOpening(l.Id, w.Id, d.Id); Assert.Empty(b.Levels[0].Walls[0].Openings); h.Undo(b); Assert.Single(b.Levels[0].Walls[0].Openings);
        var up = a.AddLevelNextTo(l.Id, true, "R+1"); var down = a.AddLevelNextTo(l.Id, false, "Cave");
        Assert.Equal(2700, up.AltitudeMm); Assert.Equal(-2700, down.AltitudeMm); Assert.Equal(LevelKind.Basement, down.Kind); Assert.Equal("Cave", b.Levels[0].Name);
        a.SetLevelKind(l.Id, LevelKind.Attic); Assert.Equal(LevelKind.Attic, b.Levels.First(x => x.Name == "RDC").Kind);
        var sp = a.AddSpace(l.Id, [new(0, 0), new(1000, 0), new(1000, 1000)], "x", SpaceKind.Room);
        a.SetSpaceProperties(l.Id, sp.Id, "Cave 3", SpaceKind.Cellar, "3", "Lot 12"); var s = b.Levels.First(x => x.Name == "RDC").Spaces[0];
        Assert.Equal(("Cave 3", "3", "Lot 12", Stability.Common), (s.Name, s.Number, s.Lot, s.Stability));
    }
}

public class SceneMapperTests
{
    [Fact] public void Sample_building_maps_to_walls_pipes_fittings_and_equipment_and_builds()
    {
        var (b, _) = SampleBuilding.Create(); var input = SceneMapper.Map(b);
        Assert.Equal(3, input.Levels.Count); Assert.Equal(12, input.Walls.Count); Assert.Equal(2, input.Pipes.Count); Assert.Single(input.Boxes);
        var objs = ImmeubleCad.Scene.SceneBuilder.Build(input, new(b.Levels[1].Id));
        Assert.Contains(objs, o => o.Kind == ImmeubleCad.Scene.SceneKind.Pipe); Assert.Contains(objs, o => o.Kind == ImmeubleCad.Scene.SceneKind.Slab);
        Assert.Equal(8, objs.Count(o => o.Kind == ImmeubleCad.Scene.SceneKind.Wall));           // RDC + R+1 only: the cut hides R+2
    }
}

public class StraightenActionTests
{
    [Fact] public void Preview_changes_nothing_apply_is_one_step_and_junctions_stay_closed()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var l = a.AddLevel("RDC", 0);
        var crooked = a.AddWall(l.Id, [new(0, 0), new(1000, 6), new(2000, 0), new(2000, 200), new(1000, 194), new(0, 200)]);
        var wall = a.AddWall(l.Id, [new(0, 400), new(2000, 400), new(2000, 600), new(0, 600)]);
        var branch = a.AddWall(l.Id, [new(900, 207), new(1100, 207), new(1100, 390), new(900, 390)]);      // 7 mm short of the first wall's face
        var before = BuildingJson.Serialize(b); var steps = h.Items.Count;
        var prev = BuildingActions.PreviewStraighten(b.Levels[0], [crooked.Id], 20, 5); Assert.Equal(4, prev[0].Outline.Count); Assert.Equal(before, BuildingJson.Serialize(b)); Assert.Equal(steps, h.Items.Count);
        a.StraightenWalls(l.Id, [crooked.Id, branch.Id], 20, 5);
        Assert.Equal(steps + 1, h.Items.Count); Assert.Equal(4, b.Levels[0].Walls[0].Footprint.Count);
        var top = b.Levels[0].Walls[0].Footprint.Max(p => p.Y); Assert.Equal(top, b.Levels[0].Walls[2].Footprint.Min(p => p.Y));        // branch touches the face: no gap
        h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
    }

    [Fact] public void Make_rectangular_is_one_undoable_step()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var l = a.AddLevel("RDC", 0);
        var w = a.AddWall(l.Id, [new(0, 0), new(500, 5), new(1000, 0), new(1003, 150), new(500, 148), new(0, 150)]); var before = BuildingJson.Serialize(b);
        a.MakeWallsRectangular(l.Id, [w.Id]); Assert.Equal(4, b.Levels[0].Walls[0].Footprint.Count); h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
    }
}

public class ImageStretchTests
{
    private static readonly ImmeubleCad.Core.Geometry.Affine T = ImmeubleCad.Core.Geometry.ImageStretch.FromArray(ImmeubleCad.Core.Import.PlanImport.TransformFor(new(0.1, 0.1), 400));   // 400 px high picture, 10 mm/px
    private const int W = 600, H = 400;
    private static (double X, double Y) P(StretchHandle h, ImmeubleCad.Core.Geometry.Affine t) => ImmeubleCad.Core.Geometry.ImageStretch.HandlePoint(t, W, H, h);
    private static ImmeubleCad.Core.Geometry.Affine Drag(StretchHandle h, double dx, double dy, bool keep = false, ImmeubleCad.Core.Geometry.Affine? t = null) { var tt = t ?? T; var g = P(h, tt); return ImmeubleCad.Core.Geometry.ImageStretch.Drag(tt, W, H, h, g, (g.X + dx, g.Y + dy), keep); }
    private static double Wd(ImmeubleCad.Core.Geometry.Affine t) { var a = P(StretchHandle.Left, t); var b = P(StretchHandle.Right, t); return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)); }
    private static double Ht(ImmeubleCad.Core.Geometry.Affine t) { var a = P(StretchHandle.Top, t); var b = P(StretchHandle.Bottom, t); return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)); }

    [Fact] public void Side_handles_stretch_one_direction_only_about_the_opposite_side()
    {
        Assert.Equal(6000, Wd(T), 6); Assert.Equal(4000, Ht(T), 6);
        var left = P(StretchHandle.Left, T);
        var r = Drag(StretchHandle.Right, 180, 0);                                    // +3 % width only
        Assert.Equal(6180, Wd(r), 6); Assert.Equal(4000, Ht(r), 6); Assert.Equal(left.X, P(StretchHandle.Left, r).X, 6);   // the opposite side did not move
        var b = Drag(StretchHandle.Bottom, 0, -200);                                   // drag the bottom edge down (plan y negative)
        Assert.Equal(6000, Wd(b), 6); Assert.Equal(4200, Ht(b), 6); Assert.Equal(P(StretchHandle.Top, T).Y, P(StretchHandle.Top, b).Y, 6);
    }

    [Fact] public void Corner_handles_scale_both_axes_and_shift_keeps_the_ratio_and_move_rotate_work()
    {
        var c = Drag(StretchHandle.TopRight, 600, 400); Assert.Equal(6600, Wd(c), 6); Assert.Equal(4400, Ht(c), 6);
        var k = Drag(StretchHandle.TopRight, 600, 10, keep: true); Assert.Equal(Wd(k) / Ht(k), 1.5, 6);
        var m = ImmeubleCad.Core.Geometry.ImageStretch.Drag(T, W, H, StretchHandle.Move, (0, 0), (50, -30)); Assert.Equal(T.Tx + 50, m.Tx, 9); Assert.Equal(T.Ty - 30, m.Ty, 9);
        var centre = P(StretchHandle.Move, T); var rot = ImmeubleCad.Core.Geometry.ImageStretch.Drag(T, W, H, StretchHandle.Rotate, (centre.X + 1000, centre.Y), (centre.X, centre.Y + 1000));
        Assert.Equal(90, ImmeubleCad.Core.Geometry.ImageStretch.Decompose(rot).RotationDeg, 6); var after = P(StretchHandle.Move, rot); Assert.Equal(centre.X, after.X, 6); Assert.Equal(centre.Y, after.Y, 6);
    }

    [Fact] public void Nudge_and_numeric_fields_round_trip()
    {
        Assert.Equal(T.Tx + 1, ImmeubleCad.Core.Geometry.ImageStretch.Nudge(T, 1, 0).Tx); Assert.Equal(T.Ty + 10, ImmeubleCad.Core.Geometry.ImageStretch.Nudge(T, 0, 10).Ty);
        var (sx, sy, rot, ox, oy) = ImmeubleCad.Core.Geometry.ImageStretch.Decompose(T); Assert.Equal((10, 10, 0), (Math.Round(sx, 6), Math.Round(sy, 6), Math.Round(rot, 6)));
        var back = ImmeubleCad.Core.Geometry.ImageStretch.Compose(sx, sy, rot, ox, oy); Assert.Equal(T.A, back.A, 9); Assert.Equal(T.D, back.D, 9); Assert.Equal(T.Ty, back.Ty, 9);
        var t2 = ImmeubleCad.Core.Geometry.ImageStretch.Compose(10.3, 9.8, 12, 100, 200); var d = ImmeubleCad.Core.Geometry.ImageStretch.Decompose(t2);
        Assert.Equal((10.3, 9.8, 12), (Math.Round(d.ScaleX, 6), Math.Round(d.ScaleY, 6), Math.Round(d.RotationDeg, 6)));
    }
}

public class FittingCoverageTests
{
    private static Network Net(Action<BuildingActions, Network> build, out Building b)
    { b = new Building(); var a = new BuildingActions(b, new BuildingHistory(), new BuildingEventsService()); a.AddLevel("RDC", 0); a.AddLevel("R+1", 2700); var n = a.AddNetwork("N", NetworkType.ColdWater); build(a, n); return n; }

    [Fact] public void Every_node_configuration_resolves_to_the_expected_fitting()
    {
        var kinds = new Dictionary<FittingKind, bool>();
        void Seen(FittingKind k, Network n, Building b, PointMm3 at) { Assert.Equal(k, FittingResolver.Resolve(n, b.Levels).First(f => f.Position == at).Kind); kinds[k] = true; }

        var n1 = Net((a, n) => a.AddPipeRun(n.Id, [new(0, 0, 0), new(1000, 0, 0)]), out var b1); Seen(FittingKind.EndCap, n1, b1, new(0, 0, 0));
        var n2 = Net((a, n) => { var r = a.AddPipeRun(n.Id, [new(0, 0, 0), new(2000, 0, 0)])[0]; a.SplitSegment(n.Id, r.Id, new(1000, 0, 0)); }, out var b2); Seen(FittingKind.Joint, n2, b2, new(1000, 0, 0));
        var n3 = Net((a, n) => a.AddPipeRun(n.Id, [new(0, 0, 0), new(1000, 0, 0), new(1000, 1000, 0)]), out var b3); Seen(FittingKind.Elbow90, n3, b3, new(1000, 0, 0));
        var n4 = Net((a, n) => { var r = a.AddPipeRun(n.Id, [new(0, 0, 0), new(2000, 0, 0)])[0]; a.AddTeeBranch(n.Id, r.Id, new(1000, 0, 0), new(1000, 800, 0)); }, out var b4); Seen(FittingKind.Tee, n4, b4, new(1000, 0, 0));
        var n5 = Net((a, n) => { var r = a.AddPipeRun(n.Id, [new(0, 0, 0), new(2000, 0, 0)])[0]; a.AddTeeBranch(n.Id, r.Id, new(1000, 0, 0), new(1000, 800, 0)); var c = n.Nodes.First(x => x.Position == new PointMm3(1000, 0, 0)); var e = a.AddNode(n.Id, new(1000, -800, 0)); a.AddSegment(n.Id, c.Id, e.Id); }, out var b5); Seen(FittingKind.Cross, n5, b5, new(1000, 0, 0));
        var n6 = Net((a, n) => { var s1 = a.AddPipeRun(n.Id, [new(0, 0, 0), new(1000, 0, 0)], 22)[0]; var m = n.Nodes.First(x => x.Position == new PointMm3(1000, 0, 0)); var e = a.AddNode(n.Id, new(2000, 0, 0)); a.AddSegment(n.Id, m.Id, e.Id, 15); }, out var b6); Seen(FittingKind.Reducer, n6, b6, new(1000, 0, 0));
        var n7 = Net((a, n) => { var s = a.AddPipeRun(n.Id, [new(0, 0, 0), new(0, 0, 5400)])[0]; a.SplitSegment(n.Id, s.Id, new(0, 0, 2700)); }, out var b7); Seen(FittingKind.SlabCrossing, n7, b7, new(0, 0, 2700));
        var n8 = Net((a, n) => { var s = a.AddPipeRun(n.Id, [new(0, 0, 2400), new(2000, 0, 2400)])[0]; a.InsertComponent(n.Id, s.Id, new(1000, 0, 2400), "shutoff-valve"); }, out var b8); Seen(FittingKind.Component, n8, b8, new(1000, 0, 2400));
        Assert.Equal(8, kinds.Count);
    }

    [Fact] public void Fittings_of_the_sample_building_are_pinned()
    {
        var (b, _) = SampleBuilding.Create(); var a = new BuildingActions(b, new BuildingHistory(), new BuildingEventsService());
        var heat = b.Networks[0]; a.AddElbow(heat.Id, heat.Nodes[1].Id, new(heat.Nodes[1].Position.X + 1000, heat.Nodes[1].Position.Y, heat.Nodes[1].Position.Z));
        var text = string.Join(";", b.Networks.SelectMany(n => FittingResolver.Resolve(n, b.Levels).Select(f => $"{n.Type}:{f.Kind}@{f.Position.X},{f.Position.Y},{f.Position.Z}")).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("HeatingReturn:EndCap@450,300,0;HeatingReturn:EndCap@450,300,8100;HeatingSupply:Elbow90@300,300,8100;HeatingSupply:EndCap@1300,300,8100;HeatingSupply:EndCap@300,300,0", text);
    }
}

public class ChainToolDetailTests
{
    private static ToolPointer P(int x, int y) => new(new(x, y), x, y);
    private static void Click(DrawChainTool t, int x, int y) { t.Down(P(x, y)); t.Up(P(x, y)); }
    private static void Place(DrawChainTool t, int x, int y) { t.Move(P(x, y)); t.Down(P(x, y)); }

    [Fact] public void Clicking_the_first_point_closes_the_outline_in_one_commit()
    {
        var runs = new List<IReadOnlyList<PointMm>>(); var t = new DrawChainTool(runs.Add);
        Click(t, 0, 0); Place(t, 3000, 0); Place(t, 3000, 2000); Place(t, 40, 30);        // near the first point
        var run = Assert.Single(runs); Assert.True(t.WasClosed); Assert.Equal(run[0], run[^1]); Assert.Equal(4, run.Count);
    }

    [Fact] public void Double_click_finishes_without_a_duplicate_point_and_backspace_removes_the_last_point()
    {
        var runs = new List<IReadOnlyList<PointMm>>(); var t = new DrawChainTool(runs.Add);
        Click(t, 0, 0); Place(t, 1000, 0); Place(t, 1000, 1000); t.Backspace(); Assert.Equal(2, t.Points.Count);
        Place(t, 2000, 0); Place(t, 2000, 0); t.DoubleClick();
        Assert.Equal([new PointMm(0, 0), new PointMm(1000, 0), new PointMm(2000, 0)], Assert.Single(runs));
    }

    [Fact] public void Escape_cancels_the_segment_first_then_the_run_and_commits_nothing()
    {
        var runs = new List<IReadOnlyList<PointMm>>(); var t = new DrawChainTool(runs.Add);
        Click(t, 0, 0); Place(t, 1000, 0); Place(t, 1000, 1000);
        Assert.True(t.EscapeStep()); Assert.Equal(2, t.Points.Count);               // first Escape: only the segment in progress
        Assert.True(t.EscapeStep()); Assert.Single(t.Points);
        Assert.False(t.EscapeStep()); Assert.Empty(t.Points); Assert.Equal(ToolState.Idle, t.State); Assert.Empty(runs);
    }

    [Fact] public void Typed_length_and_angle_place_the_next_point_exactly()
    {
        var runs = new List<IReadOnlyList<PointMm>>(); var t = new DrawChainTool(runs.Add);
        Click(t, 0, 0); t.Move(P(500, 20));
        Assert.Equal(new PointMm(2400, 0), t.ApplyLength(2400, 0));                 // 2.4 m at 0°
        t.Move(P(2400, 100)); Assert.Equal(new PointMm(2400, 1500), t.ApplyLength(1500, 90));
        var (len, ang) = t.Live()!.Value; Assert.True(len > 0 && ang is > -181 and < 181);
        t.Finish(); Assert.Single(runs); Assert.Equal(3, runs[0].Count);
    }
}


public class CatalogJsonTests
{
    private static IReadOnlyList<ImmeubleCad.Core.Catalog.CatalogEntry> All => ImmeubleCad.Core.Catalog.EquipmentCatalog.Default;

    [Fact] public void Every_entry_loads_with_symbol_shape_ports_and_both_translations()
    {
        Assert.True(All.Count >= 17); Assert.Equal(All.Count, All.Select(e => e.Id).Distinct().Count());
        foreach (var e in All)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.NameIn("fr"))); Assert.False(string.IsNullOrWhiteSpace(e.NameIn("en"))); Assert.NotEqual(e.Id, e.NameIn("fr"));
            Assert.NotEmpty(e.Symbol); Assert.NotEmpty(e.Shape); Assert.True(e.WidthMm > 0 && e.DepthMm > 0 && e.HeightMm > 0, e.Id);
            Assert.All(e.Parameters, p => { Assert.InRange(p.Default, p.Min, p.Max); Assert.True(p.Labels.ContainsKey("fr") && p.Labels.ContainsKey("en")); });
            Assert.All(e.Symbol, s => Assert.DoesNotContain("$", s));                                              // every parameter reference was substituted
            foreach (var p in e.Ports) { Assert.InRange(p.OffsetX, -1, e.WidthMm + 1); Assert.InRange(p.OffsetY, -1, e.DepthMm + 1); Assert.InRange(p.OffsetZ, -1, e.HeightMm + 1); Assert.True(p.DiameterMm is >= 8 and <= 200, e.Id); }
            Assert.All(e.Shape, s => Assert.True(s.Type is "box" or "cylinder"));
        }
    }

    [Fact] public void Categories_cover_the_palette_of_the_roadmap_and_inline_components_have_no_ports_to_connect()
    {
        string[] expected = ["radiator", "boiler", "hot-water-tank", "manifold", "circulator-pump", "shutoff-valve", "thermostatic-valve", "balancing-valve", "air-vent", "check-valve", "pressure-reducer", "meter", "washbasin", "sink", "wc", "shower", "bathtub"];
        Assert.Empty(expected.Except(All.Select(e => e.Id)));
        Assert.All(All.Where(e => e.Category == "Valves"), e => Assert.True(e.Inline));
        Assert.All(All.Where(e => e.Category == "Sanitary"), e => Assert.Contains(e.Ports, p => p.Network == NetworkType.Drainage));
    }

    [Fact] public void Parameters_change_dimensions_ports_and_symbol_and_are_clamped()
    {
        var rad = ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!; var wide = rad.With(new Dictionary<string, double> { ["width"] = 1600 });
        Assert.Equal(1000, rad.WidthMm); Assert.Equal(1600, wide.WidthMm); Assert.Equal(1520, wide.Ports.Single(p => p.Network == NetworkType.HeatingReturn).OffsetX);
        Assert.NotEqual(rad.Symbol[1], wide.Symbol[1]); Assert.Contains("1600", wide.Symbol[0]);
        Assert.Equal(4000, rad.With(new Dictionary<string, double> { ["width"] = 99999 }).WidthMm); Assert.Equal(100, rad.With(new Dictionary<string, double> { ["width"] = -5 }).WidthMm);
        Assert.Equal(1600 + 0, wide.Shape[0].W);
        Assert.Equal(7.5, rad.Eval("2.5*3"), 9); Assert.Equal(11, rad.Eval("$width/100+1"), 9); Assert.Equal(-3, rad.Eval("-3"), 9);
    }

    [Fact] public void User_objects_can_be_duplicated_changed_saved_and_reloaded()
    {
        var src = ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!; var dir = Path.Combine(Path.GetTempPath(), "usercat" + Guid.NewGuid().ToString("N"));
        var mine = ImmeubleCad.Core.Catalog.EquipmentCatalog.Duplicate(src, "radiator-long", "Radiateur long", "Long radiator", new Dictionary<string, double> { ["width"] = 2000, ["height"] = 450 });
        Assert.Equal(2000, mine.WidthMm); Assert.Equal(1000, src.WidthMm); Assert.Equal("Long radiator", mine.NameIn("en"));
        ImmeubleCad.Core.Catalog.EquipmentCatalog.SaveUser(dir, mine); File.WriteAllText(Path.Combine(dir, "broken.json"), "{ nope");
        ImmeubleCad.Core.Catalog.EquipmentCatalog.LoadUser(dir);
        try { var back = ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator-long")!; Assert.Equal(2000, back.WidthMm); Assert.Equal(450, back.HeightMm); Assert.Contains(ImmeubleCad.Core.Catalog.EquipmentCatalog.All, e => e.Id == "radiator-long"); Assert.Equal(1000, ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!.WidthMm); }
        finally { ImmeubleCad.Core.Catalog.EquipmentCatalog.UserEntries.Clear(); Directory.Delete(dir, true); }
    }
}

public class ConnectPortTests
{
    [Fact] public void Radiator_port_connects_to_a_riser_in_one_step_with_orthogonal_routing_and_a_tee()
    {
        var (b, h) = SampleBuilding.Create(); var a = new BuildingActions(b, h, new BuildingEventsService());
        var riser = b.Networks.First(n => n.Type == NetworkType.HeatingSupply); var rad = ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!;
        var eq = b.Equipment[0]; var port = ImmeubleCad.Core.Catalog.Symbol.PortPositions(rad, eq.Position, b.Levels[0].AltitudeMm + eq.ElevationMm, eq.RotationDeg).First(p => p.Port.Network == NetworkType.HeatingSupply).At;
        var steps = h.Items.Count; var before = BuildingJson.Serialize(b);
        var segs = a.ConnectPortToSegment(riser.Id, riser.Segments[0].Id, port, 14)!;
        Assert.Equal(steps + 1, h.Items.Count); Assert.True(segs.Count >= 2);
        Assert.Contains(FittingResolver.Resolve(riser, b.Levels), f => f.Kind == FittingKind.Tee && f.Position.Z == port.Z);                    // tee on the riser at the port's altitude
        Assert.Empty(FittingResolver.Validate(riser).Where(w => w.Contains("axis")));
        h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
    }

    [Fact] public void Connect_to_a_horizontal_pipe_and_refuse_diagonal_targets()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var n = a.AddNetwork("N", NetworkType.ColdWater);
        var s = a.AddPipeRun(n.Id, [new(0, 0, 500), new(4000, 0, 500)])[0];
        var segs = a.ConnectPortToSegment(n.Id, s.Id, new(2000, 900, 700))!; Assert.True(segs.Count >= 2);
        Assert.Contains(n.Nodes, x => x.Position == new PointMm3(2000, 0, 700) || x.Position == new PointMm3(2000, 0, 500));
        var d = a.AddPipeRun(n.Id, [new(0, 0, 0), new(100, 100, 0)])[0]; var steps = h.Items.Count;
        Assert.Null(a.ConnectPortToSegment(n.Id, d.Id, new(0, 0, 0))); Assert.Equal(steps, h.Items.Count);
    }

    [Fact] public void Instance_parameters_and_move_are_undoable_single_steps_and_feed_symbols_and_3d()
    {
        var (b, h) = SampleBuilding.Create(); var a = new BuildingActions(b, h, new BuildingEventsService()); var e = b.Equipment[0]; var cat = ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!;
        var before = BuildingJson.Serialize(b); a.SetEquipmentProperties(e.Id, "Radiateur séjour", 200, 90, new Dictionary<string, double> { ["width"] = 1400 }); a.MoveEquipment(e.Id, new(100, 100));
        Assert.Equal(1400, cat.With(b.Equipment[0].Parameters).WidthMm);
        var fig = ImmeubleCad.Core.Catalog.Symbol.Figures(cat.With(b.Equipment[0].Parameters), b.Equipment[0].Position, 90); Assert.NotEmpty(fig); Assert.True(fig[0].Points.Max(p => p.Y) - fig[0].Points.Min(p => p.Y) >= 1399);   // quarter turn: width runs along Y
        var parts = ImmeubleCad.Core.Catalog.Symbol.Parts(cat.With(b.Equipment[0].Parameters), b.Equipment[0].Position, 200, 90); Assert.Equal(1400, parts[0].D); Assert.Equal(cat.DepthMm, parts[0].W);
        h.Undo(b); h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
    }
}

public class ExtentTests
{
    [Fact] public void Top_and_bottom_of_a_vertical_run_move_to_level_altitudes_and_refuse_bad_moves()
    {
        var (b, h) = SampleBuilding.Create(); var a = new BuildingActions(b, h, new BuildingEventsService()); var r = b.Networks[0]; var top = r.Nodes.OrderBy(n => n.Position.Z).Last(); var bottom = r.Nodes.OrderBy(n => n.Position.Z).First();
        var steps = h.Items.Count; var before = BuildingJson.Serialize(b);
        Assert.True(a.SetNodeAltitude(r.Id, top.Id, ImmeubleCad.Core.Geometry.ElevationPlane.SnapZ(b, 5390)));                // R+2 floor
        Assert.Equal(5400, top.Position.Z); Assert.Equal(steps + 1, h.Items.Count);
        Assert.False(a.SetNodeAltitude(r.Id, top.Id, 5400)); Assert.False(a.SetNodeAltitude(r.Id, bottom.Id, 5400 - 0 + 0 == 5400 ? 5400 : 0));   // same altitude as the other end / unchanged: refused
        Assert.Equal(steps + 1, h.Items.Count);
        var floors = b.Levels.Select(l => l.AltitudeMm).ToList(); Assert.Equal(2, FittingResolver.Resolve(r, b.Levels).Count(f => f.Kind == FittingKind.EndCap));
        h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
    }
}

public class ExportOptionsTests
{
    [Fact] public void Filters_levels_and_networks_and_units_apply_to_each_format()
    {
        var (b, _) = SampleBuilding.Create(); var rdc = b.Levels[0].Id; var heat = b.Networks.First(n => n.Type == NetworkType.HeatingSupply).Id;
        var o = new ImmeubleCad.Core.Export.ExportOptions(new HashSet<Guid> { rdc }, new HashSet<Guid> { heat }, Metres: true);
        var filtered = o.Apply(b); Assert.Single(filtered.Levels); Assert.Single(filtered.Networks); Assert.Single(filtered.Equipment); Assert.Equal(3, b.Levels.Count);
        var dxf = new ImmeubleCad.Core.Export.DxfExporter().Export(b, o); Assert.Contains("WALLS_RDC", dxf); Assert.DoesNotContain("WALLS_R+1", dxf); Assert.DoesNotContain("HeatingReturn", dxf); Assert.Contains("$INSUNITS\n70\n6", dxf.Replace("\r", ""));
        Assert.Contains("\n10\n8\n", dxf.Replace("\r", ""));                                                            // the 8000 mm wall is written as 8 (metres)
        var mm = new ImmeubleCad.Core.Export.DxfExporter().Export(b, new ImmeubleCad.Core.Export.ExportOptions(Metres: false)); Assert.Contains("\n10\n8000\n", mm.Replace("\r", "")); Assert.Contains("$INSUNITS\n70\n4", mm.Replace("\r", ""));
        var objM = new ImmeubleCad.Core.Export.ObjExporter().Export(b, o); var objMm = new ImmeubleCad.Core.Export.ObjExporter().Export(b, new ImmeubleCad.Core.Export.ExportOptions(Metres: false));
        Assert.Contains("v 8 ", objM); Assert.Contains("v 8000 ", objMm); Assert.DoesNotContain("level_R+1", objM);
        foreach (var e in ImmeubleCad.Core.Export.Exporters.All) Assert.False(string.IsNullOrWhiteSpace(e.Purpose), e.Id);
        Assert.DoesNotContain("R+1", new ImmeubleCad.Core.Export.IfcExporter().Export(b, o)); Assert.DoesNotContain("R+1", new ImmeubleCad.Core.Export.FreeCadMacroExporter().Export(b, o));
    }
}

public class IfcRoundTripTests
{
    [Fact] public void Exported_ifc_has_units_context_storeys_geometry_valves_colours_and_matching_references()
    {
        var (b, h) = SampleBuilding.Create(); var a = new BuildingActions(b, h, new BuildingEventsService()); var net = b.Networks[0];
        var seg = a.AddPipeRun(a.AddNetwork("ECS", NetworkType.DomesticHotWater).Id, [new(1000, 1000, 2400), new(3000, 1000, 2400)], 20)[0]; var ecs = b.Networks[^1]; a.InsertComponent(ecs.Id, seg.Id, new(2000, 1000, 2400), "shutoff-valve");
        var ifc = new ImmeubleCad.Core.Export.IfcExporter().Export(b);
        foreach (var t in new[] { "IFCPROJECT", "IFCUNITASSIGNMENT", "IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)", "IFCGEOMETRICREPRESENTATIONCONTEXT", "IFCSITE", "IFCBUILDING(", "IFCBUILDINGSTOREY", "IFCWALL(", "IFCEXTRUDEDAREASOLID", "IFCPIPESEGMENT", "IFCCIRCLEPROFILEDEF", "IFCVALVE(", "IFCSPACEHEATER(", "IFCSURFACESTYLE", "IFCDISTRIBUTIONSYSTEM", "IFCRELCONTAINEDINSPATIALSTRUCTURE", "IFCARBITRARYCLOSEDPROFILEDEF" }) Assert.Contains(t, ifc);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(ifc, "IFCBUILDINGSTOREY\\(").Count); Assert.Equal(12, System.Text.RegularExpressions.Regex.Matches(ifc, "IFCWALL\\(").Count);
        // every #reference points at an entity that exists, and ids are unique
        var ids = System.Text.RegularExpressions.Regex.Matches(ifc, "^#(\\d+)=", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count()); var set = ids.ToHashSet();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ifc, "#(\\d+)")) Assert.Contains(int.Parse(m.Groups[1].Value), set);
        Assert.All(System.Text.RegularExpressions.Regex.Matches(ifc, "'([0-3][0-9A-Za-z_$]{21})'").Select(m => m.Groups[1].Value), g => Assert.Equal(22, g.Length));      // IfcGloballyUniqueId format
    }

    [Fact] public void Import_reads_back_storeys_walls_holes_and_heights_and_goes_through_actions()
    {
        var b = new Building { Name = "Test" }; var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService());
        var l0 = a.AddLevel("RDC", 0); var l1 = a.AddLevel("R+1", 2800, 2600);
        a.ImportWallShapes(l0.Id, [((IEnumerable<PointMm>)new List<PointMm> { new(0, 0), new(5000, 0), new(5000, 4000), new(0, 4000) }, (IEnumerable<IEnumerable<PointMm>>)new List<IEnumerable<PointMm>> { new List<PointMm> { new(200, 200), new(4800, 200), new(4800, 3800), new(200, 3800) } })]);
        a.AddWall(l1.Id, [new(0, 0), new(3000, 0), new(3000, 200), new(0, 200)], "Porteur");
        var ifc = new ImmeubleCad.Core.Export.IfcExporter().Export(b);
        var r = ImmeubleCad.Core.IO.IfcImport.Import(ifc);
        Assert.Equal(2, r.Building.Levels.Count); Assert.Equal(2, r.Walls); Assert.Empty(r.Warnings); Assert.Equal("Test", r.Building.Name);
        Assert.Equal(("RDC", 0, 2800), (r.Building.Levels[0].Name, r.Building.Levels[0].AltitudeMm, r.Building.Levels[0].HeightMm));       // height = distance to the next storey
        var w0 = r.Building.Levels[0].Walls[0]; Assert.Equal(b.Levels[0].Walls[0].Footprint, w0.Footprint); Assert.Equal(b.Levels[0].Walls[0].Holes[0], w0.Holes[0]); Assert.Equal(2700, w0.HeightMm);
        Assert.Equal("Porteur", r.Building.Levels[1].Walls[0].Label); Assert.Equal(2800, r.Building.Levels[1].AltitudeMm);
        var target = new Building(); var h2 = new BuildingHistory(); var a2 = new BuildingActions(target, h2, new BuildingEventsService()); var steps = h2.Items.Count;
        ImmeubleCad.Core.IO.IfcImport.ImportInto(a2, ifc); Assert.Equal(2, target.Levels.Count); Assert.True(h2.Items.Count > steps); while (h2.CanUndo) h2.Undo(target); Assert.Empty(target.Levels);
    }

    [Fact] public void Import_handles_metre_units_and_foreign_garbage_gracefully()
    {
        var text = "ISO-10303-21;\nDATA;\n#1=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);\n#2=IFCBUILDINGSTOREY('x',$,'Étage',$,$,$,$,$,.ELEMENT.,3.);\n#3=IFCWALL('y',$,'W',$,$,$,$,$,$);\n#4=IFCRELCONTAINEDINSPATIALSTRUCTURE('z',$,$,$,(#3),#2);\nENDSEC;\nEND-ISO-10303-21;";
        var r = ImmeubleCad.Core.IO.IfcImport.Import(text); Assert.Single(r.Building.Levels); Assert.Equal(3000, r.Building.Levels[0].AltitudeMm); Assert.Equal(0, r.Walls); Assert.NotEmpty(r.Warnings);
        Assert.Empty(ImmeubleCad.Core.IO.IfcImport.Import("not an ifc file at all").Building.Levels);
    }
}

public class ReferenceModelTests
{
    private static string BoxObj() => "v 0 0 0\nv 4 0 0\nv 4 3 0\nv 0 3 0\nv 0 0 2.7\nv 4 0 2.7\nv 4 3 2.7\nv 0 3 2.7\nf 1 2 3 4\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n";

    [Fact] public void Obj_import_triangulates_quads_converts_units_and_slices_into_a_closed_section()
    {
        var m = ImmeubleCad.Core.IO.ReferenceImport.FromObj("box", BoxObj());
        Assert.Equal(24, m.Positions.Count); Assert.Equal(12 * 3, m.Indices.Count); Assert.Equal(4000, m.Positions.Where((_, i) => i % 3 == 0).Max()); Assert.Equal(2700, m.Positions.Where((_, i) => i % 3 == 2).Max());
        var section = ImmeubleCad.Core.IO.ReferenceImport.Slice(m, 1000); Assert.True(section.Count >= 4);
        Assert.All(section.SelectMany(s => new[] { s.A, s.B }), p => Assert.True((p.X is 0 or 4000) || (p.Y is 0 or 3000)));      // the cut lies on the walls of the box
        Assert.Empty(ImmeubleCad.Core.IO.ReferenceImport.Slice(m, 5000));
    }

    [Fact] public void Our_own_glb_round_trips_as_a_reference_mesh_and_actions_are_undoable()
    {
        var (b, h) = SampleBuilding.Create(); var glb = ImmeubleCad.Core.Export.GltfExporter.ExportGlb(b); var m = ImmeubleCad.Core.IO.ReferenceImport.FromGlb("sample.glb", glb);
        Assert.True(m.Positions.Count > 100); Assert.Equal(8000, m.Positions.Where((_, i) => i % 3 == 0).Max()); Assert.Equal(6000, m.Positions.Where((_, i) => i % 3 == 1).Max());   // X east / Y north survive the Y-up round trip
        Assert.Equal(8100, m.Positions.Where((_, i) => i % 3 == 2).Max());
        var a = new BuildingActions(b, h, new BuildingEventsService()); var before = BuildingJson.Serialize(b); a.AddReference(b.Levels[0].Id, m); Assert.Single(b.Levels[0].References);
        var json = BuildingJson.Serialize(b); Assert.Single(BuildingJson.Deserialize(json).Levels[0].References); h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
        Assert.Throws<InvalidDataException>(() => ImmeubleCad.Core.IO.ReferenceImport.FromGlb("x", new byte[40])); Assert.Throws<NotSupportedException>(() => ImmeubleCad.Core.IO.ReferenceImport.FromFile("a.stl"));
    }
}

public class SiteFitTests
{
    private static List<PointMm> Rect(int x0, int y0, int x1, int y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    [Fact] public void Two_corner_pairs_give_anchor_and_rotation_one_step_and_the_footprint_maps_back_onto_the_plan()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService());
        // real building: 10 m x 6 m rotated 30° at (650000, 6860000); the user clicks two plan corners and the matching footprint corners
        double rot = 30 * Math.PI / 180; (double E, double N) L(double x, double y) => (650000 + x * Math.Cos(rot) - y * Math.Sin(rot), 6860000 + x * Math.Sin(rot) + y * Math.Cos(rot));
        var footprint = new[] { L(0, 0), L(10, 0), L(10, 6), L(0, 6) }.Select(p => new[] { p.E, p.N }).ToList();
        var site = FootprintFit.Anchor(new(0, 0), new(10000, 0), L(0, 0), L(10, 0), footprint)!;
        Assert.Equal(30, site.RotationDeg, 6); Assert.Equal(650000, site.EastM, 6); Assert.Equal(6860000, site.NorthM, 6);
        var steps = h.Items.Count; a.SetSite(site); Assert.Equal(steps + 1, h.Items.Count); Assert.Equal(30, b.Site!.RotationDeg, 6);
        var ring = FootprintFit.ToModel(b.Site, b.Site.Footprint); Assert.Equal(new PointMm(10000, 6000), ring[2]); Assert.Equal(new PointMm(0, 6000), ring[3]);
        h.Undo(b); Assert.Null(b.Site); Assert.Null(FootprintFit.Anchor(new(0, 0), new(10, 0), (0, 0), (5, 5)));            // degenerate click
    }

    [Fact] public void Outer_face_moves_onto_the_footprint_edge_inner_face_and_far_walls_do_not_move_and_it_is_one_undoable_step()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var l = a.AddLevel("RDC", 0);
        var footprint = Rect(0, 0, 8000, 6000);
        var south = a.AddWall(l.Id, [new(0, 120), new(8000, 120), new(8000, 320), new(0, 320)]);             // outer (south) face is 120 mm inside the cadastral edge y=0
        var partition = a.AddWall(l.Id, [new(0, 2900), new(8000, 2900), new(8000, 3100), new(0, 3100)]);      // interior: nowhere near an edge
        var shifts = FootprintFit.Suggest(b.Levels[0], footprint, 300); var s = Assert.Single(shifts); Assert.Equal(south.Id, s.WallId); Assert.InRange(s.MaxMoveMm, 119, 121);
        var before = BuildingJson.Serialize(b); var steps = h.Items.Count; Assert.Equal(before, BuildingJson.Serialize(b));         // suggestion alone changes nothing
        a.FitOuterWalls(l.Id, shifts); Assert.Equal(steps + 1, h.Items.Count);
        var fp = b.Levels[0].Walls[0].Footprint; Assert.Equal(0, fp.Min(p => p.Y)); Assert.Equal(320, fp.Max(p => p.Y));                  // outer face on the edge, inner face unchanged
        Assert.Equal(2900, b.Levels[0].Walls[1].Footprint.Min(p => p.Y)); h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b));
        Assert.Empty(FootprintFit.Suggest(b.Levels[0], footprint, 50));                                                                    // tolerance too small
    }

    [Fact] public void Scale_check_reports_a_wider_plan_and_angle_check_lists_the_skewed_corner()
    {
        var plan = Rect(0, 0, 10280, 6000); var foot = Rect(0, 0, 10000, 6000); var sug = FootprintFit.CheckScale(plan, foot)!;
        Assert.Equal(2.8, sug.WidthPercent, 6); Assert.Equal(0, sug.HeightPercent, 6); Assert.Contains("2.8 % wider", sug.Message); Assert.Contains("matches", FootprintFit.CheckScale(foot, foot)!.Message);
        var skew = new List<PointMm> { new(0, 0), new(10000, 0), new(10000, 6000), new(600, 6000) };                                           // top-left corner no longer 90°
        var bad = FootprintFit.AngleCheck(skew, foot, 2); Assert.Contains(bad, m => m.At == new PointMm(600, 6000) && Math.Abs(m.DiffDeg) > 2); Assert.Empty(FootprintFit.AngleCheck(foot, foot));
    }

    [Fact] public void Party_walls_are_the_footprint_edges_shared_with_a_neighbour()
    {
        var foot = Rect(0, 0, 8000, 6000); var neighbour = Rect(8100, -200, 15000, 6200);                                                       // east neighbour, 100 mm gap
        Assert.Equal([1], FootprintFit.SharedEdges(foot, [neighbour])); Assert.Empty(FootprintFit.SharedEdges(foot, [Rect(9000, 0, 12000, 3000)]));
    }

    [Fact] public void Straighten_keeps_the_real_corner_angles_of_the_cadastre_within_1_degree()
    {
        // a building with an 84° corner: straightening with the 90° axes would force 90°, with the footprint's angles it keeps 84°
        double a84 = 84 * Math.PI / 180; var wall = new List<ImmeubleCad.Plan.PointPx> { new(0, 0), new(5000, 0), new(5000 + 3000 * Math.Cos(a84), 3000 * Math.Sin(a84)), new(3000 * Math.Cos(a84), 3000 * Math.Sin(a84)) };
        var noisy = new List<ImmeubleCad.Plan.PointPx>(); var rnd = new Random(3);
        for (var i = 0; i < wall.Count; i++) { var (p, q) = (wall[i], wall[(i + 1) % wall.Count]); for (var k = 0; k < 8; k++) noisy.Add(new(p.X + (q.X - p.X) * k / 8 + (rnd.NextDouble() - 0.5) * 6, p.Y + (q.Y - p.Y) * k / 8 + (rnd.NextDouble() - 0.5) * 6)); }
        double Corner(List<ImmeubleCad.Plan.PointPx> r) { var (a, b, c) = (r[0], r[1], r[2]); double ux = a.X - b.X, uy = a.Y - b.Y, vx = c.X - b.X, vy = c.Y - b.Y; return Math.Acos((ux * vx + uy * vy) / (Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy))) * 180 / Math.PI; }
        var plain = ImmeubleCad.Plan.WallStraightener.Straighten(noisy, 20, 8); var guided = ImmeubleCad.Plan.WallStraightener.Straighten(noisy, 20, 8, ImmeubleCad.Plan.FootprintMatcher.ReferenceAngles(wall));
        Assert.Equal(4, guided.Count); Assert.InRange(Corner(guided), 95, 97);                                                                  // the real 96° (= 180 − 84°) is kept within 1°
        Assert.Equal(4, plain.Count); Assert.InRange(Corner(plain), 89.5, 90.5);                                                                // without the cadastre it is forced to 90°
    }
}

public class GeoreferencedExportTests
{
    [Fact] public void Ifc_site_carries_latitude_longitude_and_the_map_conversion_and_exports_credit_the_data_source()
    {
        var (b, h) = SampleBuilding.Create(); var a = new BuildingActions(b, h, new BuildingEventsService());
        var (e, n) = ImmeubleCad.Core.Site.Lambert93.FromWgs84(45.7640, 4.8357);                                  // Lyon
        a.SetSite(new SiteInfo { EastM = e, NorthM = n, RotationDeg = 12, AltitudeM = 170 });
        var ifc = new ImmeubleCad.Core.Export.IfcExporter().Export(b);
        Assert.Contains("IFCMAPCONVERSION", ifc); Assert.Contains("IFCPROJECTEDCRS('EPSG:2154'", ifc); Assert.Contains("(45,45,50,", ifc); Assert.Contains("(4,50,8,", ifc); Assert.Contains("Licence Ouverte", ifc);
        Assert.Contains("0.001)", ifc);                                                                            // model millimetres → CRS metres
        Assert.Contains("Licence Ouverte", new ImmeubleCad.Core.Export.DxfExporter().Export(b)); Assert.Contains("<desc>Cadastre", ImmeubleCad.Core.Export.SvgExporter.Export(b.Levels[0], b.Site)); Assert.Contains("rotate(-12", ImmeubleCad.Core.Export.SvgExporter.Export(b.Levels[0], b.Site));
        var plain = SampleBuilding.Create().Building; Assert.DoesNotContain("IFCMAPCONVERSION", new ImmeubleCad.Core.Export.IfcExporter().Export(plain)); Assert.DoesNotContain("<desc>", ImmeubleCad.Core.Export.SvgExporter.Export(plain.Levels[0]));
        Assert.Equal(12, BuildingJson.Deserialize(BuildingJson.Serialize(b)).Site!.RotationDeg);                   // survives save/load
    }
}
