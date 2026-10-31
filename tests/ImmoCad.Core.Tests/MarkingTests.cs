using ImmoCad.Core.Geometry;
using ImmoCad.Core.History;
using ImmoCad.Core.IO;
using ImmoCad.Core.Model;
using ImmoCad.Core.Walk;
using Xunit;

namespace ImmoCad.Core.Tests;

public class MarkingTests
{
    private static List<PointMm> Rect(int x0, int y0, int x1, int y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];
    private static long Area(IEnumerable<Shape> shapes) => shapes.Sum(s => SpaceFinder.AreaMm2(s.Outline) - s.Holes.Sum(h => SpaceFinder.AreaMm2(h)));
    /// <summary>A thick wall 6 m x 300 mm with a painted line 80 mm wide going out of it at x = 2000 (a T).</summary>
    private static Shape TShape(params int[] extraLineX)
    {
        var pts = new List<PointMm> { new(0, 0), new(6000, 0), new(6000, 300) };
        var lines = new[] { 2000 }.Concat(extraLineX).OrderByDescending(x => x).ToList();
        foreach (var x in lines) { pts.Add(new(x + 80, 300)); pts.Add(new(x + 80, 5000)); pts.Add(new(x, 5000)); pts.Add(new(x, 300)); }
        pts.Add(new(0, 300)); return new Shape(pts, new());
    }

    [Fact] public void A_painted_line_going_out_of_a_thick_wall_is_split_off_and_the_wall_stays()
    {
        var (thin, rest) = PolygonOps.SplitThin(TShape(), 150);
        Assert.Single(thin); Assert.InRange(Area(thin), 80L * 4700 * 90 / 100, 80L * 4700 * 112 / 100);
        Assert.InRange(Area(rest), 6000L * 300 * 95 / 100, 6000L * 300 * 105 / 100);
        Assert.All(rest.SelectMany(s => s.Outline), p => Assert.True(p.Y <= 320));                                                          // nothing of the line is left on the wall
    }

    [Fact] public void A_thick_wall_is_left_alone_and_a_wall_that_is_all_thin_becomes_all_marking()
    {
        var thick = new Shape(Rect(0, 0, 6000, 300), new()); var (t0, r0) = PolygonOps.SplitThin(thick, 150); Assert.Empty(t0); Assert.Same(thick, Assert.Single(r0));
        var line = new Shape(Rect(0, 0, 80, 5000), new()); var (t1, r1) = PolygonOps.SplitThin(line, 150);
        Assert.InRange(Area(t1), 80L * 5000 * 90 / 100, 80L * 5000 * 112 / 100); Assert.True(Area(r1) < 80L * 5000 / 20);
        var (t2, _) = PolygonOps.SplitThin(thick, 400); Assert.NotEmpty(t2);                                                                  // a threshold above the wall's own thickness makes it thin too
    }

    [Fact] public void The_zone_limits_what_is_split_off()
    {
        var two = TShape(4000); var (all, _) = PolygonOps.SplitThin(two, 150); Assert.Equal(2, all.Count);
        var (inZone, rest) = PolygonOps.SplitThin(two, 150, FloorMarkings.RectZone(new(1500, 200), new(2600, 5200)));
        Assert.Single(inZone); Assert.InRange(Area(inZone), 80L * 4700 * 90 / 100, 80L * 4700 * 112 / 100); Assert.True(Area(rest) > 6000L * 300 + 80L * 4000);                          // the line at x = 4000 stays on the wall
    }

    private static (Building B, BuildingActions A, BuildingHistory H, Level L) Level3Thin()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var l = a.AddLevel("SS", 0);
        a.ImportWalls(l.Id, new[] { Rect(0, 0, 8000, 300).ToArray(), Rect(1000, 600, 1080, 3100).ToArray(), Rect(2000, 600, 2080, 3100).ToArray(), Rect(3000, 600, 3080, 3100).ToArray() });
        return (b, a, h, b.Levels[0]);
    }

    [Fact] public void Painting_a_zone_turns_the_thin_walls_into_markings_in_one_undoable_step()
    {
        var (b, a, h, l) = Level3Thin(); var steps = h.Items.Count; var before = BuildingJson.Serialize(b);
        var plan = FloorMarkings.PlanPaint(l, FloorMarkings.RectZone(new(500, 500), new(3500, 3300)), 150); Assert.Equal(3, plan.Count);          // the thick wall is outside the zone
        Assert.Equal(3, a.PaintMarkings(l.Id, plan)); Assert.Equal(steps + 1, h.Items.Count);
        Assert.Single(l.Walls); Assert.Equal(3, l.Markings.Count); Assert.All(l.Markings, m => Assert.True(Math.Abs(SpaceFinder.AreaMm2(m.Outline)) > 80L * 2500 * 80 / 100));
        var after = BuildingJson.Serialize(b); h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b)); h.Redo(b); Assert.Equal(after, BuildingJson.Serialize(b)); l = b.Levels[0];
        Assert.Empty(FloorMarkings.PlanPaint(l, FloorMarkings.RectZone(new(500, 500), new(3500, 3300)), 150));                                           // nothing thin is left
    }

    [Fact] public void A_wall_that_is_partly_thin_keeps_its_id_and_openings_on_the_remaining_wall()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService()); var l = a.AddLevel("SS", 0);
        a.ImportWalls(l.Id, new[] { TShape().Outline.ToArray() }); var w = l.Walls[0]; var id = w.Id; var op = a.AddOpening(l.Id, w.Id, OpeningKind.Door, 1000);
        var plan = FloorMarkings.PlanPaint(l, FloorMarkings.RectZone(new(-100, -100), new(6100, 5100)), 150); a.PaintMarkings(l.Id, plan);
        Assert.Equal(id, l.Walls[0].Id); Assert.Single(l.Walls[0].Openings); Assert.Single(l.Markings);
    }

    [Fact] public void Selected_walls_convert_to_markings_and_back_and_markings_can_be_erased()
    {
        var (b, a, h, l) = Level3Thin(); var ids = l.Walls.Skip(1).Select(w => w.Id).ToList(); var before = BuildingJson.Serialize(b);
        Assert.Equal(3, a.ConvertWallsToMarkings(l.Id, ids)); Assert.Single(l.Walls); Assert.Equal(3, l.Markings.Count);
        h.Undo(b); Assert.Equal(before, BuildingJson.Serialize(b)); h.Redo(b); l = b.Levels[0];                                  // undo and redo replace the model objects
        var inZone = FloorMarkings.InZone(l, FloorMarkings.RectZone(new(1500, 500), new(2500, 3300))); Assert.Single(inZone);                           // only the middle one reaches into this zone
        Assert.Equal(1, a.MarkingsToWalls(l.Id, inZone.Select(m => m.Id))); Assert.Equal(2, l.Walls.Count); Assert.Equal(2, l.Markings.Count);
        Assert.Equal(2, a.EraseMarkings(l.Id, l.Markings.Select(m => m.Id).ToList())); Assert.Empty(l.Markings);
    }

    [Fact] public void Markings_are_saved_old_files_have_none_and_they_block_nothing_and_cut_no_room()
    {
        var (b, a, _, l) = Level3Thin(); a.ConvertWallsToMarkings(l.Id, l.Walls.Skip(1).Select(w => w.Id).ToList());
        var path = Path.Combine(Path.GetTempPath(), $"mark-{Guid.NewGuid():N}.immeuble");
        try { BuildingFile.Save(b, path); var back = BuildingFile.Load(path).Building; Assert.Equal(3, back.Levels[0].Markings.Count); Assert.Equal(l.Markings[0].Outline, back.Levels[0].Markings[0].Outline); } finally { File.Delete(path); }
        Assert.Empty(BuildingJson.Deserialize("{\"SchemaVersion\":1,\"Name\":\"old\",\"Levels\":[{\"Name\":\"L\",\"Walls\":[]}],\"Networks\":[],\"Equipment\":[]}").Levels[0].Markings);
        Assert.False(new WalkArea(b, l).Collides(new WalkPoint(1040, 1500), 1600));                                                                       // standing on a painted line
        // a room closed by four walls with painted lines across it is still one room
        var b2 = new Building(); var h2 = new BuildingHistory(); var a2 = new BuildingActions(b2, h2, new BuildingEventsService()); var l2 = a2.AddLevel("SS", 0);
        a2.ImportWalls(l2.Id, new[] { Rect(0, 0, 8000, 200).ToArray(), Rect(0, 5800, 8000, 6000).ToArray(), Rect(0, 200, 200, 5800).ToArray(), Rect(7800, 200, 8000, 5800).ToArray() });
        l2.Markings.Add(new FloorMarking { Outline = Rect(2000, 300, 2080, 5000) }); l2.Markings.Add(new FloorMarking { Outline = Rect(4000, 300, 4080, 5000) });
        var probe = SpaceFinder.Probe(new SpaceGrid(l2.Walls), new PointMm(1000, 3000), 0); Assert.NotNull(probe.Outline); Assert.InRange(SpaceFinder.AreaMm2(probe.Outline!), 7600L * 5600 * 97 / 100, 7600L * 5600 * 103 / 100);
    }
}
