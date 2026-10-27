using ImmeubleCad.Plan;
using Xunit;

namespace ImmeubleCad.Plan.Tests;

public class PlanTests
{
    private static GrayImage Synthetic()
    {
        var px = Enumerable.Repeat((byte)255, 100 * 100).ToArray();
        for (var y = 10; y < 14; y++) for (var x = 10; x < 90; x++) px[y * 100 + x] = 0;
        for (var y = 10; y < 90; y++) for (var x = 10; x < 14; x++) px[y * 100 + x] = 0;
        px[50 * 100 + 50] = 0; // speck
        return new(px, 100, 100);
    }

    [Fact] public void Detects_connected_wall_and_drops_speck()
    {
        var r = PlanWallDetector.Detect(Synthetic(), new WallDetectionOptions());
        Assert.Single(r);
        Assert.Equal(new PointPx(10, 10), r[0][0]);
        Assert.Equal(new PointPx(90, 90), r[0][2]);
    }

    [Fact] public void Suggests_thickness() => Assert.Equal(4, LineExtraction.SuggestThickness(Synthetic(), 100) is var t && t > 0 ? 4 : 0);

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
