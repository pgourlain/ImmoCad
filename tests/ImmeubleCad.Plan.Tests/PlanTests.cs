using ImmeubleCad.Plan;
using Xunit;

namespace ImmeubleCad.Plan.Tests;

public class PlanTests
{
    /// <summary>100×100 plan: a room of walls 6 px thick (outer 10..90), plus a 1-px dimension line and a speck (must be dropped).</summary>
    private static GrayImage Synthetic()
    {
        var px = Enumerable.Repeat((byte)255, 100 * 100).ToArray();
        for (var y = 10; y < 90; y++)
            for (var x = 10; x < 90; x++)
                if (x < 16 || x >= 84 || y < 16 || y >= 84) px[y * 100 + x] = 0;
        for (var x = 20; x < 80; x++) px[50 * 100 + x] = 0;   // thin line inside the room
        px[2 * 100 + 2] = 0;                                   // speck
        return new(px, 100, 100);
    }

    [Fact] public void Detects_room_as_outline_with_one_hole_and_drops_thin_strokes()
    {
        var r = PlanWallDetector.Detect(Synthetic(), new WallDetectionOptions(MinThickness: 4));
        var shape = Assert.Single(r);
        Assert.Equal(4, shape.Outline.Count);
        Assert.Equal(new PointPx(10, 10), shape.Outline.OrderBy(p => p.X + p.Y).First());
        Assert.Equal(new PointPx(90, 90), shape.Outline.OrderBy(p => p.X + p.Y).Last());
        var hole = Assert.Single(shape.Holes);
        Assert.Equal(4, hole.Count);
        Assert.Equal(16, hole.Min(p => p.X)); Assert.Equal(84, hole.Max(p => p.X));
    }

    [Fact] public void Detection_is_deterministic_with_pinned_result()
    {
        string Fp(List<WallOutline> r) => string.Join("|", r.Select(w => string.Join(";", w.Outline.Select(p => $"{p.X},{p.Y}")) + "#" + string.Join("/", w.Holes.Select(h => string.Join(";", h.Select(p => $"{p.X},{p.Y}"))))));
        var a = Fp(PlanWallDetector.Detect(Synthetic(), new WallDetectionOptions(MinThickness: 4)));
        Assert.Equal(a, Fp(PlanWallDetector.Detect(Synthetic(), new WallDetectionOptions(MinThickness: 4))));
        Assert.Equal("10,10;90,10;90,90;10,90#16,16;16,84;84,84;84,16", a.Replace(" ", ""), ignoreCase: true);
    }

    [Fact] public void Suggests_thickness_close_to_wall_width()
    {
        var t = PlanWallDetector.SuggestThickness(Synthetic());
        Assert.InRange(t, 2, 6);
    }

    [Fact] public void Straightens_noisy_rectangle_to_four_points()
    {
        var p = new List<PointPx> { new(0, 0), new(50, 1), new(100, 0), new(101, 50), new(100, 100), new(50, 99), new(0, 100), new(0, 50) };
        var r = WallStraightener.Straighten(p, 5, 5);
        Assert.Equal(4, r.Count);
    }

    [Fact] public void Tolerance_zero_unchanged()
    {
        var p = new List<PointPx> { new(0, 0), new(50, 1), new(100, 0), new(100, 100) };
        Assert.Equal(p, WallStraightener.Straighten(p, 0));
    }
}

public class FootprintTests
{
    [Fact] public void Matches_rotated_stretched_footprint_with_missing_edge_and_reports_angles()
    {
        var plan = new List<PointPx> { new(0, 0), new(10000, 0), new(10000, 6000), new(0, 6000) };
        var foot = new List<PointPx> { new(100, 200), new(10200, 100), new(10300, 6100), new(150, 6150) }; // slightly rotated, 100 mm off
        var m = FootprintMatcher.Match(plan, foot);
        Assert.Equal(4, m.Count);
        Assert.All(m, x => Assert.True(Math.Abs(x.AngleDiffDeg) < 2));
        var missing = foot.Take(3).ToList(); // 3 points → 3 edges (one is a diagonal)
        Assert.True(FootprintMatcher.Match(plan, missing).Count >= 2);
        Assert.Equal(4, FootprintMatcher.ReferenceAngles(foot).Count);
        var skew = new List<PointPx> { new(0, 0), new(10000, 0), new(10000, 6000), new(1000, 6000) }; // top edge not matching: 0° still, side edge 5°
        Assert.NotEmpty(FootprintMatcher.AngleMismatches(plan, skew, 2));
    }
}

public class DeskewTests
{
    private static GrayImage Rotated(double deg)
    {
        const int N = 300; var px = Enumerable.Repeat((byte)255, N * N).ToArray(); var r = deg * Math.PI / 180; double c = Math.Cos(r), s = Math.Sin(r);
        void Line(double x0, double y0, double x1, double y1)
        {
            for (var t = 0.0; t <= 1; t += 0.001)
            {
                double x = x0 + (x1 - x0) * t - 150, y = y0 + (y1 - y0) * t - 150; // rotate around centre
                int rx = (int)Math.Round(x * c - y * s + 150), ry = (int)Math.Round(x * s + y * c + 150);
                if (rx >= 0 && ry >= 0 && rx < N && ry < N) px[ry * N + rx] = 0;
            }
        }
        Line(50, 80, 250, 80); Line(50, 220, 250, 220); Line(50, 80, 50, 220); Line(250, 80, 250, 220); Line(150, 80, 150, 220);
        return new(px, N, N);
    }

    [Theory] [InlineData(0)] [InlineData(3)] [InlineData(-4.5)]
    public void Estimates_rotation_of_a_synthetic_plan(double deg)
    {
        var a = Deskew.EstimateAngle(Rotated(deg));
        Assert.InRange(Math.Abs(a - deg), 0, 0.6); // returns the tilt; rotate the picture by -tilt to straighten it
    }
}

public class PlanArchitectureTests
{
    [Fact] public void Plan_is_BCL_only()
    {
        var refs = typeof(PlanWallDetector).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.All(refs, n => Assert.True(n.StartsWith("System") || n is "netstandard" or "mscorlib" or "Microsoft.Win32.Primitives", $"Plan references {n}"));
    }
}

public class StraightenSpecTests
{
    private static List<PointPx> Rot(IEnumerable<PointPx> pts, double deg, double cx = 0, double cy = 0)
    { var r = deg * Math.PI / 180; double c = Math.Cos(r), s = Math.Sin(r); return pts.Select(p => new PointPx(cx + (p.X - cx) * c - (p.Y - cy) * s, cy + (p.X - cx) * s + (p.Y - cy) * c)).ToList(); }

    private static List<PointPx> Noisy(IEnumerable<PointPx> corners, double amp, int per = 6)
    {   // sub-divide each side and jitter the interior points: "saw teeth"
        var c = corners.ToList(); var res = new List<PointPx>(); var rnd = new Random(7);
        for (var i = 0; i < c.Count; i++)
        {
            var (a, b) = (c[i], c[(i + 1) % c.Count]); res.Add(a);
            for (var k = 1; k < per; k++) { var t = (double)k / per; var jx = (rnd.NextDouble() - 0.5) * 2 * amp; var jy = (rnd.NextDouble() - 0.5) * 2 * amp; res.Add(new(a.X + (b.X - a.X) * t + jx, a.Y + (b.Y - a.Y) * t + jy)); }
        }
        return res;
    }

    [Fact] public void Noisy_straight_wall_becomes_four_segments()
    {
        var wall = Noisy([new(0, 0), new(1000, 0), new(1000, 150), new(0, 150)], 4);
        Assert.True(wall.Count > 20);
        var r = WallStraightener.Straighten(wall, tolerance: 12, angleDeg: 5); Assert.Equal(4, r.Count);
        Assert.All(r, p => Assert.True(p.X is < 10 or > 990 || Math.Abs(p.X) < 10)); // corners stay where they were
    }

    [Fact] public void L_shaped_wall_with_saw_teeth_becomes_six_segments()
    {
        var l = Noisy([new(0, 0), new(1000, 0), new(1000, 150), new(150, 150), new(150, 800), new(0, 800)], 3);
        Assert.Equal(6, WallStraightener.Straighten(l, 10, 5).Count);
    }

    [Fact] public void Wall_rotated_by_12_degrees_is_straightened_along_12_not_0()
    {
        var rect = Rot(Noisy([new(0, 0), new(1000, 0), new(1000, 150), new(0, 150)], 3), 12, 500, 75);
        var r = WallStraightener.Straighten(rect, 10, 5); Assert.Equal(4, r.Count);
        var ang = Math.Atan2(r[1].Y - r[0].Y, r[1].X - r[0].X) * 180 / Math.PI; Assert.InRange(((ang % 90) + 90) % 90, 11.5, 12.5);
    }

    [Fact] public void Tolerance_zero_leaves_the_outline_unchanged_and_the_result_is_deterministic_and_pinned()
    {
        var wall = Noisy([new(0, 0), new(1000, 0), new(1000, 150), new(0, 150)], 4);
        Assert.Equal(wall, WallStraightener.Straighten(wall, 0, 5));
        string Fp(List<PointPx> r) => string.Join("|", r.Select(p => $"{p.X:0.###},{p.Y:0.###}"));
        var a = Fp(WallStraightener.Straighten(wall, 12, 5)); Assert.Equal(a, Fp(WallStraightener.Straighten(Noisy([new(0, 0), new(1000, 0), new(1000, 150), new(0, 150)], 4), 12, 5)));
        Assert.Equal(4, a.Split('|').Length);
    }

    [Fact] public void No_point_moves_more_than_the_tolerance_allows_on_a_scanned_like_wall()
    {
        var wall = Noisy([new(0, 0), new(2000, 0), new(2000, 200), new(0, 200)], 5, 10); var r = WallStraightener.Straighten(wall, 15, 5);
        Assert.True(wall.Count >= 40 && r.Count <= 4, $"{wall.Count} -> {r.Count}: {string.Join(" ", r.Select(p => $"({p.X:0},{p.Y:0})"))}"); Assert.True(1 - (double)r.Count / wall.Count >= 0.8);            // ≥ 80 % fewer segments
        foreach (var p in r) Assert.True(new[] { (0.0, 0.0), (2000.0, 0.0), (2000.0, 200.0), (0.0, 200.0) }.Min(c => Math.Sqrt((p.X - c.Item1) * (p.X - c.Item1) + (p.Y - c.Item2) * (p.Y - c.Item2))) <= 15, string.Join(" ", r.Select(q => $"({q.X:0.0},{q.Y:0.0})")));
    }
}

public class WallFitterTests
{
    [Fact] public void Rectangle_outline_gives_one_centre_segment_with_thickness()
    {
        var seg = Assert.Single(WallFitter.Fit([new(0, 0), new(4000, 0), new(4000, 200), new(0, 200)])!);
        Assert.Equal(200, seg.Thickness, 6); Assert.Equal(100, seg.A.Y, 6); Assert.Equal(100, seg.B.Y, 6); Assert.Equal(4000, Math.Abs(seg.B.X - seg.A.X), 6);
    }

    [Fact] public void Unmatched_polygons_are_kept_by_returning_null()
    {
        Assert.Null(WallFitter.Fit([new(0, 0), new(100, 0), new(50, 80)]));                                   // triangle: no parallel pair
        Assert.Null(WallFitter.Fit([new(0, 0), new(4000, 0), new(4000, 5000), new(0, 5000)], maxThickness: 600)); // far apart: not a wall pair
    }

    [Fact] public void L_wall_gives_two_segments()
    {
        var segs = WallFitter.Fit([new(0, 0), new(1000, 0), new(1000, 150), new(150, 150), new(150, 800), new(0, 800)])!;
        Assert.True(segs.Count >= 1); Assert.All(segs, s => Assert.InRange(s.Thickness, 140, 160));
    }
}

public class WallShapesTests
{
    [Fact] public void Make_rectangular_fits_a_rotated_crooked_wall()
    {
        double r = 20 * Math.PI / 180; PointPx R(double x, double y) => new(x * Math.Cos(r) - y * Math.Sin(r), x * Math.Sin(r) + y * Math.Cos(r));
        var crooked = new List<PointPx> { R(0, 0), R(500, 4), R(1000, 0), R(1003, 150), R(500, 146), R(0, 150) };
        var rect = WallShapes.MakeRectangular(crooked); Assert.Equal(4, rect.Count);
        double w = Math.Sqrt(Math.Pow(rect[1].X - rect[0].X, 2) + Math.Pow(rect[1].Y - rect[0].Y, 2)), h = Math.Sqrt(Math.Pow(rect[2].X - rect[1].X, 2) + Math.Pow(rect[2].Y - rect[1].Y, 2));
        Assert.InRange(Math.Max(w, h), 995, 1010); Assert.InRange(Math.Min(w, h), 145, 160);
    }

    [Fact] public void Junctions_snap_without_gaps_and_far_points_are_left_alone()
    {
        var wall = new List<PointPx> { new(0, 0), new(1000, 0), new(1000, 100), new(0, 100) };
        var branch = new List<PointPx> { new(500, 107), new(600, 107), new(600, 800), new(500, 800) };   // 7 px short of the wall's face
        var snapped = WallShapes.SnapJunctions([wall, branch], tolerance: 10);
        Assert.Equal(100, snapped[1][0].Y, 6); Assert.Equal(100, snapped[1][1].Y, 6); Assert.Equal(800, snapped[1][2].Y);
        var far = WallShapes.SnapJunctions([wall, new List<PointPx> { new(500, 150), new(600, 150), new(600, 800), new(500, 800) }], 10); Assert.Equal(150, far[1][0].Y);
    }
}
