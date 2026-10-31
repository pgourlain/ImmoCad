using ImmoCad.Core;
using ImmoCad.Core.History;
using ImmoCad.Core.IO;
using ImmoCad.Core.Model;
using ImmoCad.Core.Walk;
using ImmoCad.Scene;
using Xunit;

namespace ImmoCad.Core.Tests;

public class WalkTests
{
    private static List<PointMm> Rect(int x0, int y0, int x1, int y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    /// <summary>A corridor 10 m × 1.6 m (inner faces y 200..1800) closed at both ends, with a cross wall at x 5000..5200 that has a 700 mm opening centred in the corridor.</summary>
    private static (Building B, Level L, Wall Cross) Corridor(OpeningKind? kind = OpeningKind.Door, int widthMm = 700, int? sill = null, int? openingHeight = null)
    {
        var l = new Level { Name = "L", AltitudeMm = 0, HeightMm = 2700 };
        l.Walls.Add(new Wall { Footprint = Rect(0, 0, 10000, 200) });
        l.Walls.Add(new Wall { Footprint = Rect(0, 1800, 10000, 2000) });
        l.Walls.Add(new Wall { Footprint = Rect(0, 200, 200, 1800) });
        l.Walls.Add(new Wall { Footprint = Rect(9800, 200, 10000, 1800) });
        var cross = new Wall { Footprint = Rect(5000, 200, 5200, 1800) };
        if (kind is { } k)
        {
            var o = new Opening { Kind = k, PositionMm = 800, WidthMm = widthMm };
            if (sill is { } s) o.SillMm = s; if (openingHeight is { } oh) o.HeightMm = oh; cross.Openings.Add(o);
        }
        l.Walls.Add(cross);
        var b = new Building(); b.Levels.Add(l); return (b, l, cross);
    }

    // ---- WalkArea ----
    [Fact] public void A_wall_stops_the_user_one_radius_away_and_the_move_slides_along_it()
    {
        var (b, l, _) = Corridor(kind: null); var area = new WalkArea(b, l);
        var p = area.Move(new(1000, 1000), 0, 2000, 1600, false); Assert.Equal(1000, p.X, 3); Assert.Equal(1600, p.Y, 3);          // 1800 − 200
        var s = area.Move(new(1000, 1000), 1000, 2000, 1600, false); Assert.Equal(1600, s.Y, 3); Assert.InRange(s.X, 1000 + 300, 2001);   // slid along the wall
    }

    [Fact] public void A_door_is_a_passage_and_the_same_wall_without_it_blocks()
    {
        var (b, l, _) = Corridor(); var area = new WalkArea(b, l);
        var through = area.Move(new(4000, 1000), 2000, 0, 1600, false); Assert.Equal(6000, through.X, 3);
        var (b2, l2, _) = Corridor(kind: null); var blocked = new WalkArea(b2, l2).Move(new(4000, 1000), 2000, 0, 1600, false); Assert.Equal(4800, blocked.X, 3);
    }

    [Fact] public void The_kind_of_opening_plays_no_part_only_the_presence_of_wall_does()
    {
        foreach (var kind in new[] { OpeningKind.Door, OpeningKind.Window, OpeningKind.Opening })
        {
            var (b, l, _) = Corridor(kind, sill: 900, openingHeight: 1500);                                                          // open from 900 to 2400 mm
            Assert.Equal(6000, new WalkArea(b, l).Move(new(4000, 1000), 2000, 0, 1600, false).X, 3);
        }
    }

    [Fact] public void An_opening_blocks_below_its_sill_and_above_its_lintel()
    {
        var (b, l, _) = Corridor(OpeningKind.Window, sill: 900, openingHeight: 1200); var area = new WalkArea(b, l);                // open from 900 to 2100
        Assert.Equal(6000, area.Move(new(4000, 1000), 2000, 0, 1600, false).X, 3);
        Assert.Equal(4800, area.Move(new(4000, 1000), 2000, 0, 500, false).X, 3);                                                     // eye below the sill
        Assert.Equal(4800, area.Move(new(4000, 1000), 2000, 0, 2300, false).X, 3);                                                    // eye above the lintel
    }

    [Fact] public void A_wall_lower_than_the_eye_does_not_block()
    {
        var (b, l, cross) = Corridor(kind: null); cross.HeightMm = 900; var area = new WalkArea(b, l);
        Assert.Equal(6000, area.Move(new(4000, 1000), 2000, 0, 1600, false).X, 3);
        Assert.Equal(4800, area.Move(new(4000, 1000), 2000, 0, 600, false).X, 3);
    }

    [Fact] public void A_fast_move_does_not_tunnel_through_a_thin_wall()
    {
        var (b, l, cross) = Corridor(kind: null); cross.Footprint = Rect(5000, 200, 5020, 1800);
        Assert.Equal(4800, new WalkArea(b, l).Move(new(4000, 1000), 8000, 0, 1600, false).X, 3);
    }

    [Fact] public void Ghost_mode_ignores_walls_and_only_the_world_limit_stops_it()
    {
        var (b, l, _) = Corridor(kind: null); var area = new WalkArea(b, l);
        Assert.Equal(6000, area.Move(new(4000, 1000), 2000, 0, 1600, true).X, 3);
        var far = area.Move(new(4000, 1000), 100000, 0, 1600, true); Assert.Equal(area.Bounds.MaxX, far.X, 3);
    }

    [Fact] public void The_world_limit_is_the_modelled_box_plus_a_metre_unless_a_frame_is_set()
    {
        var (b, _, _) = Corridor(); var w = WorldBounds.For(b);
        Assert.Equal((-1000, -1000, 11000, 3000), (w.MinX, w.MinY, w.MaxX, w.MaxY)); Assert.Equal((0, 2700), (w.MinZ, w.MaxZ));
        b.WorldFrame = new RectMm(-5000, -5000, 20000, 9000); w = WorldBounds.For(b); Assert.Equal((-5000, -5000, 20000, 9000), (w.MinX, w.MinY, w.MaxX, w.MaxY));
        var n = new Network(); n.Nodes.Add(new Node { Position = new(30000, 1000, 1000) }); b.WorldFrame = null; b.Networks.Add(n);
        Assert.Equal(31000, WorldBounds.For(b).MaxX);                                                                                  // a pipe modelled out to the street extends the world
        Assert.Equal(1000, WorldBounds.For(new Building()).MaxX);                                                                      // nothing modelled: a small default world
    }

    [Fact] public void The_nearest_free_point_from_inside_a_wall_prefers_the_side_the_user_came_from()
    {
        var (b, l, _) = Corridor(kind: null); var area = new WalkArea(b, l);
        var p = area.NearestWalkable(new(5100, 1000), 1600, new WalkPoint(4000, 1000)); Assert.Equal(4800, p.X, 0);
        var q = area.NearestWalkable(new(5100, 1000), 1600, new WalkPoint(6000, 1000)); Assert.Equal(5400, q.X, 0);
        Assert.Equal(new WalkPoint(2000, 1000), area.NearestWalkable(new(2000, 1000), 1600));                                          // already free: unchanged
    }

    // ---- WalkState ----
    private static WalkState Start(out Building b, out Level l, bool door = true)
    { var c = Corridor(door ? OpeningKind.Door : null); b = c.B; l = c.L; return new WalkState(b, l, new WalkPoint(1000, 1000)); }

    private static void Run(WalkState s, double seconds, WalkInput input) { for (var i = 0; i < (int)(seconds * 60); i++) s.Update(1 / 60.0, input); }

    [Fact] public void Forward_follows_the_view_direction_and_strafe_is_perpendicular_to_it()
    {
        var s = Start(out _, out _); s.Yaw = -Math.PI / 2;                                                                             // looking along +X
        Run(s, 1, new(1, 0, 0, false)); Assert.True(s.X > 1000 + 800); Assert.Equal(1000, s.Y, 1);
        var t = Start(out _, out _); t.Yaw = -Math.PI / 2; Run(t, 0.5, new(0, 1, 0, false)); Assert.True(t.Y < 1000);                  // facing +X, the right hand is −Y
    }

    [Fact] public void Walking_speeds_up_smoothly_and_running_is_faster()
    {
        var walk = Start(out _, out _); walk.Yaw = -Math.PI / 2; Run(walk, 1, new(1, 0, 0, false));
        var run = Start(out _, out _); run.Yaw = -Math.PI / 2; Run(run, 1, new(1, 0, 0, true));
        Assert.InRange(walk.X - 1000, 900, 1400); Assert.True(run.X - 1000 > 2 * (walk.X - 1000));
        var e = Start(out _, out _); e.Yaw = -Math.PI / 2; e.Update(1 / 60.0, new(1, 0, 0, false)); Assert.True(e.X - 1000 < 1400 / 60.0 * 0.5);   // the first frame is slower than full speed
    }

    [Fact] public void The_user_crosses_the_door_and_stops_at_the_end_wall()
    {
        var s = Start(out _, out _); s.Yaw = -Math.PI / 2; Run(s, 12, new(1, 0, 0, true)); Assert.Equal(9600, s.X, 0);                  // 9800 − 200
    }

    [Fact] public void Click_to_go_glides_to_the_target_and_any_key_cancels_it()
    {
        var s = Start(out _, out _); s.GoTo(new(4000, 1000)); Run(s, 6, default); Assert.Equal(4000, s.X, 0); Assert.Null(s.Goal);
        var t = Start(out _, out _); t.GoTo(new(9000, 1000)); Run(t, 0.5, default); Assert.NotNull(t.Goal); t.Update(1 / 60.0, new WalkInput(0.001, 0, 0, false)); Assert.Null(t.Goal);
    }

    [Fact] public void Click_to_go_towards_a_wall_stops_against_it()
    {
        var s = Start(out _, out _, door: false); s.GoTo(new(6000, 1000)); Run(s, 10, default); Assert.Equal(4800, s.X, 0); Assert.Null(s.Goal);
    }

    [Fact] public void The_eye_moves_between_the_floor_and_the_ceiling_and_home_resets_it()
    {
        var s = Start(out _, out _); Run(s, 10, new(0, 0, 1, false)); Assert.Equal(2600, s.EyeAboveFloor, 0);
        Run(s, 10, new(0, 0, -1, false)); Assert.Equal(200, s.EyeAboveFloor, 0);
        s.ResetEye(); Assert.Equal(1600, s.EyeAboveFloor);
    }

    private static (Building B, Level Lower, Level Upper) TwoLevels()
    {
        var (b, l0, _) = Corridor(); var l1 = new Level { Name = "U", AltitudeMm = 2900, HeightMm = 2700 };
        l1.Walls.Add(new Wall { Footprint = Rect(0, 0, 10000, 200) }); l1.Walls.Add(new Wall { Footprint = Rect(0, 1800, 10000, 2000) });
        l1.Walls.Add(new Wall { Footprint = Rect(4000, 200, 6000, 1800) });                                                           // a block right above the corridor's middle
        b.Levels.Add(l1); return (b, l0, l1);
    }

    [Fact] public void Changing_level_moves_to_the_nearest_free_place_and_says_so_and_up_then_down_comes_back_exactly()
    {
        var (b, l0, l1) = TwoLevels(); var s = new WalkState(b, l0, new WalkPoint(5100, 1000)); var notices = new List<WalkNotice>(); s.Notice += notices.Add;
        Assert.True(s.ChangeLevel(l1)); Assert.Same(l1, s.Level); Assert.Equal(2900 + 1600, s.EyeZ);
        Assert.False(s.Area.Collides(s.Position, 1600)); Assert.Contains(notices, n => n.Kind == WalkNoticeKind.Shifted && n.Value > 0.5);
        Assert.True(s.ChangeLevel(l0)); Assert.Equal(5100, s.X, 1); Assert.Equal(1000, s.Y, 1);
    }

    [Fact] public void A_level_without_walls_is_refused_and_the_user_stays_put()
    {
        var (b, l0, _) = TwoLevels(); var empty = new Level { Name = "E", AltitudeMm = 6000 }; b.Levels.Add(empty);
        var s = new WalkState(b, l0, new WalkPoint(1000, 1000)); var notices = new List<WalkNotice>(); s.Notice += notices.Add;
        Assert.False(s.ChangeLevel(empty)); Assert.Same(l0, s.Level); Assert.Equal(1000, s.X); Assert.Single(notices, n => n.Kind == WalkNoticeKind.LevelHasNoWalls);
    }

    [Fact] public void Leaving_ghost_mode_inside_a_wall_moves_out_and_switching_back_before_moving_returns_exactly()
    {
        var s = Start(out _, out _, door: false); s.SetGhost(true); s.Jump(new(5100, 1000)); Assert.Equal(5100, s.X);                  // in the wall
        var notices = new List<WalkNotice>(); s.Notice += notices.Add;
        s.SetGhost(false); Assert.False(s.Area.Collides(s.Position, 1600)); Assert.Contains(notices, n => n.Kind == WalkNoticeKind.Shifted);
        s.SetGhost(true); Assert.Equal(5100, s.X); Assert.Equal(1000, s.Y);
    }

    [Fact] public void Leaving_ghost_mode_outside_the_building_keeps_the_position_and_the_walls_block_again()
    {
        var s = Start(out _, out _); s.SetGhost(true); s.Jump(new(5000, -600)); Run(s, 0.1, default); s.SetGhost(false);
        Assert.Equal(5000, s.X); Assert.Equal(-600, s.Y);                                                                              // floating outside, nothing moved it
        s.Yaw = 0; Run(s, 3, new(1, 0, 0, false)); Assert.Equal(-200, s.Y, 0);                                                          // north: stopped 200 mm from the outer wall's face (y 0)
    }

    [Fact] public void In_ghost_mode_the_eye_crosses_the_slab_and_the_level_follows_its_altitude()
    {
        var (b, l0, l1) = TwoLevels(); var s = new WalkState(b, l0, new WalkPoint(1000, 1000)); s.SetGhost(true); var notices = new List<WalkNotice>(); s.Notice += notices.Add;
        Run(s, 8, new(0, 0, 1, false)); Assert.Same(l1, s.Level); Assert.Contains(notices, n => n.Kind == WalkNoticeKind.LevelChanged);
        Run(s, 30, new(0, 0, 1, false)); Assert.Equal(l1.AltitudeMm + l1.HeightMm - 100, s.EyeZ, 0);                                   // stops at the top of the building
        s.SetGhost(false); Assert.InRange(s.EyeAboveFloor, 200, 2600);
    }

    // ---- openings in the 3D mesh ----
    [Fact] public void A_wall_without_openings_keeps_its_single_extrusion_and_a_door_cuts_the_wall_in_bands()
    {
        var (b, _, cross) = Corridor(kind: null); var plain = SceneMapper.Map(b);
        Assert.Equal(5, plain.Walls.Count); Assert.All(plain.Walls, w => Assert.Equal(2700, w.Height));
        var (b2, _, cross2) = Corridor(); var cut = SceneMapper.Map(b2);
        Assert.True(cut.Walls.Count > plain.Walls.Count); Assert.All(cut.Walls.Where(w => w.Id == cross2.Id), w => Assert.True(w.Height <= 2700));
        Assert.Contains(cut.Walls, w => w.Id == cross2.Id && w.BaseZ == 2100);                                                          // the lintel band starts at the top of the door
    }

    [Fact] public void The_door_is_a_hole_in_the_mesh_below_the_lintel_and_the_wall_is_solid_beside_and_above_it()
    {
        var (b, l, _) = Corridor(); var input = SceneMapper.Map(b);
        var objs = SceneBuilder.Build(input, new SceneOptions(null));
        bool Hit(float y, float z) => SceneBuilder.Pick(objs, (4f, y, z), (1f, 0f, 0f)) is { Kind: SceneKind.Wall, Distance: < 1.5f };    // a ray along +X through the cross wall (x 5.0..5.2 m)
        Assert.False(Hit(1.0f, 1.0f));      // through the door
        Assert.True(Hit(1.0f, 2.4f));       // the lintel (door height 2.1 m)
        Assert.True(Hit(0.5f, 1.0f));       // the jamb beside the door (door spans y 0.65..1.35 m)
    }

    [Fact] public void A_window_leaves_the_wall_solid_under_its_sill()
    {
        var (b, _, _) = Corridor(OpeningKind.Window, sill: 900, openingHeight: 1200); var objs = SceneBuilder.Build(SceneMapper.Map(b), new SceneOptions(null));
        bool Hit(float z) => SceneBuilder.Pick(objs, (4f, 1.0f, z), (1f, 0f, 0f)) is { Kind: SceneKind.Wall, Distance: < 1.5f };
        Assert.True(Hit(0.5f)); Assert.False(Hit(1.5f)); Assert.True(Hit(2.4f));
    }

    // ---- world frame ----
    [Fact] public void The_world_frame_is_one_history_step_survives_a_save_and_old_files_have_none()
    {
        var b = new Building(); var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService());
        var count = h.Items.Count; a.SetWorldFrame(new RectMm(-1000, -2000, 30000, 20000)); Assert.Equal(count + 1, h.Items.Count); Assert.Equal(new RectMm(-1000, -2000, 30000, 20000), b.WorldFrame);
        var path = Path.Combine(Path.GetTempPath(), $"world-{Guid.NewGuid():N}.immeuble");
        try { BuildingFile.Save(b, path); Assert.Equal(b.WorldFrame, BuildingFile.Load(path).Building.WorldFrame); } finally { File.Delete(path); }
        h.Undo(b); Assert.Null(b.WorldFrame); h.Redo(b); Assert.NotNull(b.WorldFrame);
        Assert.Null(BuildingJson.Deserialize("{\"SchemaVersion\":1,\"Name\":\"old\",\"Levels\":[],\"Networks\":[],\"Equipment\":[]}").WorldFrame);
    }

    [Fact] public void The_first_look_is_along_the_longest_free_line_of_the_corridor()
    {
        var (b, l, _) = Corridor(kind: null); var area = new WalkArea(b, l);
        var yaw = area.LongestFreeYaw(new(2000, 1000), 1600);                                                                          // 7.6 m to the east, only 1.8 m to the west
        var s = new WalkState(b, l, new WalkPoint(2000, 1000), yaw); Assert.True(s.Forward.X > 0.99);
    }

    [Fact] public void Rebind_finds_the_level_again_after_an_undo_replaced_the_objects()
    {
        var (b, l, _) = Corridor(); var s = new WalkState(b, l, new WalkPoint(1000, 1000));
        var json = ImmoCad.Core.History.BuildingJson.Serialize(b); ImmoCad.Core.History.BuildingJson.Restore(b, json);                 // new objects, same ids
        Assert.NotSame(l, b.Levels[0]); Assert.True(s.Rebind()); Assert.Same(b.Levels[0], s.Level);
        b.Levels.Clear(); Assert.False(s.Rebind());
    }

    /// <summary>A 10 m × 10 m hall (walls 200 mm) with a closed 1.4 m closet in the middle.</summary>
    private static (Building B, Level L) HallWithCloset(bool gap = false)
    {
        var l = new Level { Name = "H", HeightMm = 2700 };
        l.Walls.Add(new Wall { Footprint = Rect(0, 0, 10000, 200) }); l.Walls.Add(new Wall { Footprint = Rect(0, 9800, gap ? 4000 : 10000, 10000) });
        if (gap) l.Walls.Add(new Wall { Footprint = Rect(5000, 9800, 10000, 10000) });                                                // a 1 m gap in the north wall
        l.Walls.Add(new Wall { Footprint = Rect(0, 200, 200, 9800) }); l.Walls.Add(new Wall { Footprint = Rect(9800, 200, 10000, 9800) });
        l.Walls.Add(new Wall { Footprint = Rect(4000, 4000, 5400, 5400), Holes = [Rect(4200, 4200, 5200, 5200)] });                  // closet: walls around a 1 m room
        var b = new Building(); b.Levels.Add(l); return (b, l);
    }

    [Fact] public void A_closed_closet_is_a_pocket_and_the_hall_is_the_largest_region()
    {
        var (b, l) = HallWithCloset(); var area = new WalkArea(b, l);
        var inCloset = new WalkPoint(4700, 4700); Assert.False(area.Collides(inCloset, 1600));
        var (cells, outside) = area.Region(inCloset, 1600); Assert.False(outside); Assert.InRange(cells, 1, 40);
        var (hallCells, hallOutside) = area.Region(new WalkPoint(2000, 2000), 1600); Assert.False(hallOutside); Assert.True(hallCells > 400);
        var hall = area.LargestRegionPoint(1600)!.Value; Assert.False(area.Collides(hall, 1600)); Assert.True(area.Region(hall, 1600).Cells > 400);
    }

    [Fact] public void A_gap_in_the_outer_wall_lets_the_region_leak_outside()
    {
        var (b, l) = HallWithCloset(gap: true); var area = new WalkArea(b, l);
        Assert.True(area.Region(new WalkPoint(2000, 2000), 1600).Outside);
        var hall = area.LargestRegionPoint(1600)!.Value; Assert.True(area.Region(hall, 1600).Outside);                                  // the big region wins even though it leaks, never the closet
    }

    [Fact] public void Going_to_a_smaller_level_keeps_the_user_inside_the_building_but_someone_outside_stays_outside()
    {
        var (b, l0, _) = Corridor(); var small = new Level { Name = "S", AltitudeMm = 2900, HeightMm = 2700 };
        small.Walls.Add(new Wall { Footprint = Rect(0, 0, 4000, 200) }); small.Walls.Add(new Wall { Footprint = Rect(0, 1800, 4000, 2000) }); small.Walls.Add(new Wall { Footprint = Rect(0, 200, 200, 1800) }); small.Walls.Add(new Wall { Footprint = Rect(3800, 200, 4000, 1800) });
        b.Levels.Add(small);
        var s = new WalkState(b, l0, new WalkPoint(8000, 1000)); Assert.True(s.ChangeLevel(small));                                    // 8 m east: beyond the small level's 4 m
        Assert.InRange(s.X, 0, 4000); Assert.InRange(s.Y, 0, 2000); Assert.False(s.Area.Collides(s.Position, 1600));
        Assert.True(s.ChangeLevel(l0)); Assert.Equal(8000, s.X, 1);                                                                     // the position asked for is remembered: back where it was
        var out0 = new WalkState(b, l0, new WalkPoint(8000, 1000)); out0.SetGhost(true); out0.Jump(new(8000, -800)); out0.SetGhost(false);
        Assert.True(out0.ChangeLevel(small)); Assert.Equal(-800, out0.Y, 1);                                                            // floating outside stays outside
    }

    // ---- room names ----
    private static Space Room(string name, SpaceKind kind, string? number = null, params (int X, int Y)[] pts) => new() { Name = name, Kind = kind, Number = number, Outline = pts.Select(p => new PointMm(p.X, p.Y)).ToList() };
    private static (int, int)[] Box(int x0, int y0, int x1, int y1) => [(x0, y0), (x1, y0), (x1, y1), (x0, y1)];
    private static void InLanguage(string lang, Action body) { var before = Translations.Language; Translations.Language = lang; try { body(); } finally { Translations.Language = before; } }

    [Fact] public void A_typed_name_is_shown_as_it_is_and_a_generated_one_gives_way_to_a_more_telling_kind()
    {
        InLanguage("en", () =>
        {
            Assert.Equal("Local Vélo", WalkLabels.RoomText(Room("Local Vélo", SpaceKind.BikeRoom, null, Box(0, 0, 1000, 1000))));                         // typed: as it is, the kind adds nothing
            Assert.Equal("Bike room", WalkLabels.RoomText(Room(Translations.Token("space.default.name", 3), SpaceKind.BikeRoom, null, Box(0, 0, 1000, 1000))));   // "Room 3" says nothing
            Assert.Equal("Room 3", WalkLabels.RoomText(Room(Translations.Token("space.default.name", 3), SpaceKind.Room, null, Box(0, 0, 1000, 1000))));        // kind Room: the numbered name tells rooms apart
            Assert.Equal("Cellar 12", WalkLabels.RoomText(Room(Translations.Token("space.default.name", 4), SpaceKind.Cellar, "12", Box(0, 0, 1000, 1000))));    // the number is added
            Assert.Equal("Cave 12", WalkLabels.RoomText(Room("Cave 12", SpaceKind.Cellar, "12", Box(0, 0, 1000, 1000))));                                      // unless it is already there
            Assert.Equal("Corridor", WalkLabels.RoomText(Room("", SpaceKind.Corridor, null, Box(0, 0, 1000, 1000))));                                          // no name at all: the kind
        });
        InLanguage("fr", () => Assert.Equal("Local vélos", WalkLabels.RoomText(Room(Translations.Token("space.default.name", 1), SpaceKind.BikeRoom, null, Box(0, 0, 1000, 1000)))));
    }

    [Fact] public void The_anchor_of_an_L_shaped_room_is_inside_it_and_a_long_corridor_gets_a_name_near_each_end()
    {
        PointMm[] l = [new(0, 0), new(6000, 0), new(6000, 1000), new(1000, 1000), new(1000, 6000), new(0, 6000)];
        var a = WalkLabels.AnchorsOf(l); Assert.Single(a); Assert.True(ImmoCad.Core.Geometry.SpaceFinder.Contains(l, a[0]));                                  // the centre of mass of an L is outside the L
        var corridor = Box(0, 0, 20000, 1500).Select(p => new PointMm(p.Item1, p.Item2)).ToList(); var c = WalkLabels.AnchorsOf(corridor);
        Assert.Equal(3, c.Count); Assert.All(c, p => Assert.True(ImmoCad.Core.Geometry.SpaceFinder.Contains(corridor, p))); Assert.True(c.Max(p => p.X) - c.Min(p => p.X) > 10000);
        Assert.Empty(WalkLabels.AnchorsOf([new PointMm(0, 0), new PointMm(10, 10)]));
    }

    [Fact] public void Walls_block_the_line_of_sight_except_through_an_opening_that_is_open_at_the_eye_height()
    {
        var (b, l, _) = Corridor(); var area = new WalkArea(b, l);
        Assert.True(area.LineOfSight(new(1000, 1000), new(8000, 1000), 1600));                                                          // through the door
        Assert.False(area.LineOfSight(new(1000, 500), new(8000, 500), 1600));                                                           // the wall beside it
        Assert.False(area.LineOfSight(new(1000, 1000), new(8000, 1000), 2300));                                                         // eye above the lintel
        var (b2, l2, _) = Corridor(kind: null); Assert.False(new WalkArea(b2, l2).LineOfSight(new(1000, 1000), new(8000, 1000), 1600));
        Assert.True(area.LineOfSight(new(1000, 1000), new(4000, 1100), 1600));                                                          // nothing in between
    }

    private static (WalkState S, WalkCamera Cam) Looking(Building b, Level l, double x, double y, double yaw)
    {
        var s = new WalkState(b, l, new WalkPoint(x, y), yaw); var cam = new WalkCamera { Eye = (s.X / 1000, s.Y / 1000, s.EyeZ / 1000), Yaw = s.Yaw }; return (s, cam);
    }

    [Fact] public void Names_are_placed_for_rooms_in_front_that_are_visible_and_not_the_one_you_stand_in()
    {
        var (b, l, _) = Corridor(); l.Spaces.Add(Room("West", SpaceKind.Corridor, null, Box(200, 200, 5000, 1800))); l.Spaces.Add(Room("East", SpaceKind.Room, null, Box(5200, 200, 9800, 1800)));
        var labels = new WalkLabels(); var (s, cam) = Looking(b, l, 1000, 1000, -Math.PI / 2);                                           // in the west part, looking east
        var placed = labels.Place(s, cam, 1200, 800); Assert.Single(placed); Assert.Equal("East", placed[0].Text);                       // seen through the door; "West" is where we stand
        Assert.InRange(placed[0].X, 500, 700); Assert.InRange(placed[0].Y, 0, 400);                                                       // straight ahead, above the middle of the view
        var (s2, cam2) = Looking(b, l, 1000, 1000, Math.PI / 2); Assert.Empty(labels.Place(s2, cam2, 1200, 800));                         // looking west: nothing in front
    }

    [Fact] public void A_name_behind_a_wall_is_hidden_unless_in_ghost_mode()
    {
        var (b, l, _) = Corridor(kind: null); l.Spaces.Add(Room("East", SpaceKind.Room, null, Box(5200, 200, 9800, 1800)));
        var labels = new WalkLabels(); var (s, cam) = Looking(b, l, 1000, 1000, -Math.PI / 2);
        Assert.Empty(labels.Place(s, cam, 1200, 800));
        s.SetGhost(true); Assert.Single(labels.Place(s, cam, 1200, 800));                                                                 // through the wall
    }

    [Fact] public void Names_fade_with_distance_and_vanish_beyond_fifteen_metres_and_at_most_a_few_are_kept()
    {
        var l = new Level { Name = "L", HeightMm = 2700 }; l.Walls.Add(new Wall { Footprint = Rect(-1000, -1000, -800, 1000) });
        l.Spaces.Add(Room("Near", SpaceKind.Room, null, Box(5000, -500, 6000, 500))); l.Spaces.Add(Room("Mid", SpaceKind.Room, null, Box(11000, -500, 12000, 500))); l.Spaces.Add(Room("Far", SpaceKind.Room, null, Box(20000, -500, 21000, 500)));
        var b = new Building(); b.Levels.Add(l); var labels = new WalkLabels(); var s = new WalkState(b, l, new WalkPoint(0, 0), -Math.PI / 2); s.SetGhost(true); var cam = new WalkCamera { Eye = (0, 0, 1.6), Yaw = -Math.PI / 2 };
        var placed = labels.Place(s, cam, 1200, 800); Assert.Equal(["Near", "Mid"], placed.Select(p => p.Text));                           // 21 m away: not shown
        Assert.Equal(1, placed[0].Alpha); Assert.InRange(placed[1].Alpha, 0.35, 0.99);
        for (var i = 0; i < 12; i++) l.Spaces.Add(Room($"R{i}", SpaceKind.Room, null, Box(2000 + i * 300, 100 + i * 20, 2200 + i * 300, 300 + i * 20)));
        Assert.True(labels.Place(s, cam, 1200, 800, max: 5).Count <= 5);
    }
}
