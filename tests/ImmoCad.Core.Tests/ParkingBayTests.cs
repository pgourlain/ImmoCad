using ImmoCad.Core.Geometry;
using ImmoCad.Core.Model;
using Xunit;

namespace ImmoCad.Core.Tests;

public class ParkingBayTests
{
    private static List<PointMm> R(int x0, int y0, int x1, int y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    /// <summary>Three bays 2.5 m wide, 5 m deep: side lines 100 mm wide every 2.5 m and a line at the back; the aisle is open.</summary>
    private static Level Row(bool turned = false)
    {
        var l = new Level(); Func<int, int, int, int, List<PointMm>> r = turned ? (a, b, c, d) => R(b, a, d, c) : (a, b, c, d) => R(a, b, c, d);
        for (var i = 0; i <= 3; i++) l.Markings.Add(new FloorMarking { Outline = r(i * 2500, 0, i * 2500 + 100, 5000) });
        l.Markings.Add(new FloorMarking { Outline = r(0, 5000, 7600, 5100) });
        return l;
    }

    [Fact] public void A_bay_is_the_strip_between_two_side_lines_closed_at_the_back_and_open_on_the_aisle()
    {
        var bay = new ParkingBays(Row()).Find(new(3700, 2000))!;
        Assert.NotNull(bay); Assert.InRange(bay.Max(p => p.X) - bay.Min(p => p.X), 2398, 2402); Assert.InRange(bay.Min(p => p.X), 2598, 2602);      // between the faces of the lines, to the millimetre Assert.InRange(bay.Max(p => p.Y) - bay.Min(p => p.Y), 4900, 5100);
        Assert.InRange(bay.Max(p => p.Y), 4998, 5002);                                                  // up to the face of the back line
        Assert.InRange(bay.Min(p => p.Y), -2, 2);                                                      // down to the end of the side lines, on the aisle
        Assert.Contains(new ParkingBays(Row()).Find(new(1200, 4000))!, p => Math.Abs(p.X - 100) <= 2);
    }

    [Fact] public void A_row_turned_a_quarter_gives_the_same_bay_and_a_click_in_the_aisle_or_on_a_line_gives_none()
    {
        var bay = new ParkingBays(Row(turned: true)).Find(new(2000, 3700))!;
        Assert.InRange(bay.Max(p => p.Y) - bay.Min(p => p.Y), 2398, 2402); Assert.InRange(bay.Max(p => p.X), 4998, 5002); Assert.InRange(bay.Min(p => p.X), -2, 2);
        var row = new ParkingBays(Row());
        Assert.Null(row.Find(new(3700, -4000))); Assert.Null(row.Find(new(2550, 2000)));        // far in the aisle (no line to the left or right), on a side line
    }

    [Fact] public void A_bay_inside_a_parking_room_is_still_found_and_the_walls_can_close_its_ends()
    {
        var l = Row(); l.Walls.Add(new Wall { Footprint = R(-300, -300, 8000, -200) });          // a wall along the aisle ends of the lines, the room's wall at the back
        Assert.NotNull(new ParkingBays(l).Find(new(1200, 2500)));
        var narrow = new Level(); narrow.Markings.Add(new FloorMarking { Outline = R(0, 0, 100, 5000) }); narrow.Markings.Add(new FloorMarking { Outline = R(900, 0, 1000, 5000) });
        Assert.Null(new ParkingBays(narrow).Find(new(500, 2500)));                                // 0.8 m between two lines: not a car
    }

    [Fact] public void A_rounded_wall_is_a_rectangle_with_half_round_ends_no_longer_than_the_drawn_line()
    {
        var pts = ImmoCad.Core.Geometry.WallGeometry.RoundedRectangle(new(0, 0), new(1200, 0), 400, ImmoCad.Core.Geometry.WallAlign.Center).ToList();
        Assert.InRange(pts.Min(p => p.X), -1, 1); Assert.InRange(pts.Max(p => p.X), 1199, 1201); Assert.InRange(pts.Min(p => p.Y), -201, -199); Assert.InRange(pts.Max(p => p.Y), 199, 201);
        Assert.True(pts.Count >= 16); Assert.True(SpaceFinder.AreaMm2(pts) is > 400 * 800 and < 400 * 1200);                  // between the straight part and the full rectangle
        Assert.Contains(pts, p => Math.Abs(p.X - 200) <= 1 && Math.Abs(p.Y - 200) <= 1);                                      // where the straight side meets the cap
        var left = ImmoCad.Core.Geometry.WallGeometry.RoundedRectangle(new(0, 0), new(1200, 0), 400, ImmoCad.Core.Geometry.WallAlign.Left).ToList();
        Assert.InRange(left.Min(p => p.Y), -1, 1); Assert.InRange(left.Max(p => p.Y), 399, 401);                              // the side of the line, like a square wall
        var stub = ImmoCad.Core.Geometry.WallGeometry.RoundedRectangle(new(0, 0), new(200, 0), 400, ImmoCad.Core.Geometry.WallAlign.Center).ToList();
        Assert.InRange(stub.Max(p => p.X), 199, 201);                                                                          // a line shorter than the thickness: half ellipses, never longer than drawn
    }

    [Fact] public void The_name_of_a_parking_space_is_painted_flat_inside_it_and_a_parking_never_floats_its_name()
    {
        var bay = R(0, 0, 2500, 5000);
        var shapes = PaintedText.ForSpace(bay, "P12"); Assert.True(shapes.Count >= 8);
        var pts = shapes.SelectMany(x => x).ToList(); Assert.All(pts, p => Assert.True(p.X >= 0 && p.X <= 2500 && p.Y >= 0 && p.Y <= 5000));        // inside the bay
        var w = pts.Max(p => p.X) - pts.Min(p => p.X); var h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
        Assert.True(h > w * 0.8 || w < 2500 * 0.8);                                                                                              // sized to fit, not across the lines
        var building = new Building(); var lv = new Level { AltitudeMm = 0 }; building.Levels.Add(lv);
        lv.Spaces.Add(new Space { Outline = bay, Name = "P1", Kind = SpaceKind.Parking }); lv.Spaces.Add(new Space { Outline = R(5000, 0, 8000, 3000), Name = "Cave 1", Kind = SpaceKind.Cellar });
        var input = SceneMapper.Map(building);
        Assert.Contains(input.Markings!, m => m.Outline.All(p => p.X <= 2500));                                                                  // the paint of P1 is in the scene
        Assert.DoesNotContain(input.Markings!, m => m.Outline.Any(p => p.X >= 5000));                                                            // a cellar's name is not painted
        Assert.Empty(PaintedText.ForSpace(bay, "")); Assert.Empty(PaintedText.ForSpace(R(0, 0, 10, 10).Take(2).ToList(), "P1"));
    }

    [Fact] public void Scraps_of_paint_and_plain_rooms_are_not_bays()
    {
        var scraps = new Level();                                                                                                         // short tilted strokes around the click, as a noisy scan leaves
        foreach (var (x, y) in new[] { (-2500, -1200), (2300, 800), (-300, 3000), (1500, -2800), (3500, 3200) }) scraps.Markings.Add(new FloorMarking { Outline = [new(x, y), new(x + 900, y + 250), new(x + 880, y + 340), new(x - 20, y + 90)] });
        Assert.Null(new ParkingBays(scraps).Find(new(0, 0)));
        var room = new Level(); foreach (var w in new[] { R(0, 0, 8000, 200), R(0, 5800, 8000, 6000), R(0, 0, 200, 6000), R(7800, 0, 8000, 6000) }) room.Walls.Add(new Wall { Footprint = w });
        room.Markings.Add(new FloorMarking { Outline = R(9000, 0, 9100, 5000) });                                                           // a line somewhere else
        Assert.Null(new ParkingBays(room).Find(new(4000, 3000)));                                                                         // a room between walls: not a bay
        var broken = Row(); broken.Markings.RemoveAt(1); broken.Markings.RemoveAt(1);                                                   // the lines of the middle bays are missing
        Assert.Null(new ParkingBays(broken).Find(new(3700, 2000)));
    }
}

public class RepairedShapeTests
{
    [Fact] public void A_bow_tie_is_traced_again_into_simple_pieces_and_a_square_is_left_alone()
    {
        var bow = new ImmoCad.Core.Geometry.Shape([new(0, 0), new(2000, 2000), new(2000, 0), new(0, 2000)], []);
        Assert.True(ImmoCad.Core.Geometry.PolygonOps.SelfCrosses(bow));
        var pieces = ImmoCad.Core.Geometry.PolygonOps.Repaired(bow);
        Assert.NotEmpty(pieces); Assert.All(pieces, p => Assert.False(ImmoCad.Core.Geometry.PolygonOps.SelfCrosses(p)));
        var square = new ImmoCad.Core.Geometry.Shape([new(0, 0), new(2000, 0), new(2000, 2000), new(0, 2000)], []);
        Assert.Same(square, Assert.Single(ImmoCad.Core.Geometry.PolygonOps.Repaired(square)));
    }
}
