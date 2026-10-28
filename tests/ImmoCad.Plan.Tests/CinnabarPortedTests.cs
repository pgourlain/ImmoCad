using ImmoCad.Plan;
using Xunit;

namespace ImmoCad.Plan.Tests;

// Ported from CinnabarSharp (WallExtractionTests / MaskTracerTests), adapted to grayscale input and WallOutline. Same synthetic plans, same pinned checksum.
public class LineExtractionPortedTests
{
    private const int W = 200, H = 140;

    private static void Fill(byte[] gray, int x, int y, int w, int h, byte v = 0) { for (var j = y; j < y + h; j++) for (var i = x; i < x + w; i++) gray[j * W + i] = v; }

    /// <summary>A room with 8-pixel walls, a 1-pixel dimension line above it and small "text" marks.</summary>
    private static byte[] Plan(bool lightLines = false)
    {
        var g = Enumerable.Repeat((byte)255, W * H).ToArray();
        Fill(g, 30, 30, 120, 8); Fill(g, 30, 102, 120, 8); Fill(g, 30, 30, 8, 80); Fill(g, 142, 30, 8, 80);
        Fill(g, 30, 15, 120, 1); Fill(g, 30, 10, 1, 12); Fill(g, 60, 60, 2, 9); Fill(g, 65, 60, 2, 9);
        if (lightLines) for (var i = 0; i < g.Length; i++) g[i] = (byte)(255 - g[i]);
        return g;
    }
    private static bool On(byte[] mask, int x, int y) => mask[y * W + x] != 0;
    private static LineExtractionOptions Options(LineMode mode = LineMode.Walls, int thickness = 5, int bridge = 0, bool light = false) => new(mode, 0, thickness, bridge, 20, light);

    [Fact] public void Walls_stay_and_thin_strokes_go()
    {
        var mask = LineExtraction.Extract(Plan(), W, H, Options());
        Assert.True(On(mask, 34, 70)); Assert.True(On(mask, 80, 34)); Assert.True(On(mask, 146, 70)); Assert.True(On(mask, 80, 106));
        Assert.False(On(mask, 80, 15)); Assert.False(On(mask, 30, 12)); Assert.False(On(mask, 61, 64)); Assert.False(On(mask, 80, 70));
    }

    [Fact] public void Walls_keep_their_full_thickness_and_ends() => Assert.Equal(2 * 120 * 8 + 2 * 8 * 64, LineExtraction.Extract(Plan(), W, H, Options()).Count(v => v != 0));

    [Fact] public void Light_lines_on_a_dark_background()
    {
        var mask = LineExtraction.Extract(Plan(lightLines: true), W, H, Options(light: true));
        Assert.True(On(mask, 34, 70)); Assert.False(On(mask, 80, 15)); Assert.Equal(2 * 120 * 8 + 2 * 8 * 64, mask.Count(v => v != 0));
    }

    [Fact] public void Thin_lines_mode_keeps_the_dimension_line_and_not_the_walls()
    {
        var mask = LineExtraction.Extract(Plan(), W, H, Options(LineMode.ThinLines) with { MinArea = 5 });
        Assert.True(On(mask, 80, 15)); Assert.True(On(mask, 61, 64)); Assert.False(On(mask, 34, 70)); Assert.False(On(mask, 80, 34));
    }

    [Fact] public void A_break_in_a_wall_is_bridged_up_to_the_gap()
    {
        var plan = Plan(); Fill(plan, 80, 30, 4, 8, 255);
        var open = LineExtraction.Extract(plan, W, H, Options()); var bridged = LineExtraction.Extract(plan, W, H, Options(bridge: 4));
        Assert.False(On(open, 81, 34)); Assert.True(On(bridged, 81, 34)); Assert.True(On(bridged, 40, 34));
    }

    [Fact] public void Small_specks_are_removed()
    {
        var plan = Plan(); Fill(plan, 100, 60, 6, 6);
        Assert.True(On(LineExtraction.Extract(plan, W, H, Options() with { MinArea = 20 }), 102, 62)); Assert.False(On(LineExtraction.Extract(plan, W, H, Options() with { MinArea = 40 }), 102, 62));
    }

    [Fact] public void Suggested_thickness_separates_walls_from_thin_lines()
    {
        var t = LineExtraction.SuggestThickness(Plan(), W, H); Assert.InRange(t, 3, 8);
        var mask = LineExtraction.Extract(Plan(), W, H, Options(thickness: t)); Assert.True(On(mask, 34, 70)); Assert.False(On(mask, 80, 15));
    }

    [Fact] public void Extraction_is_deterministic_and_pinned()
    {
        var first = LineExtraction.Extract(Plan(), W, H, Options()); var second = LineExtraction.Extract(Plan(), W, H, Options());
        Assert.Equal(first, second);
        Assert.Equal("D01156166D1523EE849AFADE15779D1E276AA420A868A73BDDECA2958691D6A8", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first)));
    }

    [Fact] public void Cancelling_stops_the_extraction()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LineExtraction.Extract(Plan(), W, H, Options(), cancellation: cts.Token));
    }
}

public class MaskTracerPortedTests
{
    private const int W = 80, H = 60;
    private static byte[] Mask(params (int X, int Y, int Width, int Height)[] rects)
    {
        var m = new byte[W * H]; foreach (var (x, y, w, h) in rects) for (var j = y; j < y + h; j++) for (var i = x; i < x + w; i++) m[j * W + i] = 255; return m;
    }

    [Fact] public void Rectangle_is_four_exact_corners()
    {
        var shape = Assert.Single(MaskTracer.Trace(Mask((5, 5, 40, 10)), W, H));
        Assert.Equal([new PointPx(5, 5), new PointPx(45, 5), new PointPx(5, 15), new PointPx(45, 15)], shape.Outline.OrderBy(p => p.Y).ThenBy(p => p.X).ToList());
    }

    [Fact] public void L_shape_has_six_corners()
    {
        var shape = Assert.Single(MaskTracer.Trace(Mask((5, 5, 10, 40), (5, 35, 40, 10)), W, H));
        Assert.Equal(6, shape.Outline.Count); Assert.All(shape.Outline, c => Assert.True(c.X is 5 or 15 or 45 && c.Y is 5 or 35 or 45));
    }

    [Fact] public void Room_is_one_shape_with_the_outline_and_a_hole()
    {
        var shape = Assert.Single(MaskTracer.Trace(Mask((5, 5, 50, 6), (5, 39, 50, 6), (5, 5, 6, 40), (49, 5, 6, 40)), W, H));
        Assert.Equal(4, shape.Outline.Count); Assert.Equal(4, Assert.Single(shape.Holes).Count);
    }

    [Fact] public void Separate_walls_are_separate_shapes() => Assert.Equal(2, MaskTracer.Trace(Mask((5, 5, 20, 6), (5, 30, 20, 6)), W, H).Count);

    [Fact] public void Ragged_edges_become_straight_orthogonal_sides()
    {
        var mask = Mask((10, 10, 50, 12)); for (var x = 14; x < 56; x += 5) { mask[9 * W + x] = 255; mask[21 * W + x] = 0; }
        var c = Assert.Single(MaskTracer.Trace(mask, W, H)).Outline;
        Assert.Equal(4, c.Count); Assert.Equal(2, c.Select(p => Math.Round(p.X, 3)).Distinct().Count()); Assert.Equal(2, c.Select(p => Math.Round(p.Y, 3)).Distinct().Count());
        Assert.InRange(c.Min(p => p.X), 9.5, 10.5); Assert.InRange(c.Max(p => p.X), 59.5, 60.5);
    }

    [Fact] public void Specks_below_the_minimum_area_are_dropped() => Assert.Single(MaskTracer.Trace(Mask((5, 5, 20, 6), (50, 40, 1, 1)), W, H, new TraceOptions(MinArea: 4)));
    [Fact] public void Scale_multiplies_the_coordinates() => Assert.Contains(new PointPx(90, 30), Assert.Single(MaskTracer.Trace(Mask((5, 5, 40, 10)), W, H, new TraceOptions(Scale: 2))).Outline);

    [Fact] public void Tracing_is_deterministic()
    {
        var mask = Mask((5, 5, 50, 6), (5, 39, 50, 6), (5, 5, 6, 40), (49, 5, 6, 40), (20, 20, 3, 3));
        string F(IEnumerable<WallOutline> s) => string.Join("|", s.Select(w => string.Join(";", w.Outline.Select(p => $"{p.X},{p.Y}")) + "#" + string.Join("/", w.Holes.Select(h => string.Join(";", h.Select(p => $"{p.X},{p.Y}"))))));
        Assert.Equal(F(MaskTracer.Trace(mask, W, H)), F(MaskTracer.Trace(mask, W, H)));
    }

    [Fact] public void Filling_the_outlines_gives_back_the_mask()
    {
        // A room with a door gap plus a wall stub: even-odd fill of the traced shapes must cover the same pixels.
        var mask = Mask((5, 5, 50, 6), (5, 39, 20, 6), (35, 39, 20, 6), (5, 5, 6, 40), (49, 5, 6, 40), (25, 20, 6, 15));
        var shapes = MaskTracer.Trace(mask, W, H); var different = 0;
        bool In(IReadOnlyList<PointPx> poly, double x, double y) { var inside = false; for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) if ((poly[i].Y > y) != (poly[j].Y > y) && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside; return inside; }
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++)
        {
            var filled = shapes.Any(s => In(s.Outline, x + 0.5, y + 0.5) != s.Holes.Any(h => In(h, x + 0.5, y + 0.5)) && In(s.Outline, x + 0.5, y + 0.5));
            if (filled != (mask[y * W + x] != 0)) different++;
        }
        Assert.True(different <= 2, $"{different} pixels differ");
    }
}
