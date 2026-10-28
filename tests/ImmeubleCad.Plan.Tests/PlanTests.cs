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
