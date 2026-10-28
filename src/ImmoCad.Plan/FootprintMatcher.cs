namespace ImmoCad.Plan;

public sealed record EdgeMatch(int PlanEdge, int FootprintEdge, double AngleDiffDeg, double DistanceMm);

/// <summary>Matches plan outline edges to cadastral footprint edges (direction + distance). Robust to missing or extra edges.</summary>
public static class FootprintMatcher
{
    public static List<EdgeMatch> Match(IReadOnlyList<PointPx> plan, IReadOnlyList<PointPx> footprint, double maxAngleDeg = 15, double maxDistance = 1500)
    {
        var res = new List<EdgeMatch>();
        for (var i = 0; i < plan.Count; i++)
        {
            var (a, b) = (plan[i], plan[(i + 1) % plan.Count]);
            if (Len(a, b) < 1) continue;
            EdgeMatch? best = null;
            for (var j = 0; j < footprint.Count; j++)
            {
                var (c, d) = (footprint[j], footprint[(j + 1) % footprint.Count]);
                var diff = AngleDiff(Ang(a, b), Ang(c, d)); if (Math.Abs(diff) > maxAngleDeg) continue;
                var dist = Math.Abs(Dist(a, b, c, d)); if (dist > maxDistance) continue;
                if (best == null || dist < best.DistanceMm) best = new(i, j, diff, dist);
            }
            if (best != null) res.Add(best);
        }
        return res;
    }

    /// <summary>Unique footprint edge directions (degrees mod 180) → ReferenceGeometry for the straightener.</summary>
    public static List<double> ReferenceAngles(IReadOnlyList<PointPx> footprint) =>
        Enumerable.Range(0, footprint.Count).Select(i => Ang(footprint[i], footprint[(i + 1) % footprint.Count])).Select(a => (a % 180 + 180) % 180).ToList();

    /// <summary>Corners whose angle differs from the footprint by more than the threshold.</summary>
    public static List<int> AngleMismatches(IReadOnlyList<PointPx> plan, IReadOnlyList<PointPx> footprint, double thresholdDeg = 2)
        => Match(plan, footprint).Where(m => Math.Abs(m.AngleDiffDeg) > thresholdDeg).Select(m => m.PlanEdge).ToList();

    private static double Ang(PointPx a, PointPx b) => Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
    private static double AngleDiff(double a, double b) { var d = ((a - b) % 180 + 270) % 180 - 90; return d; }
    private static double Len(PointPx a, PointPx b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double Dist(PointPx a, PointPx b, PointPx c, PointPx d)
    {
        var mx = (a.X + b.X) / 2; var my = (a.Y + b.Y) / 2; var l = Len(c, d); if (l < 1e-9) return double.MaxValue;
        return ((d.X - c.X) * (c.Y - my) - (c.X - mx) * (d.Y - c.Y)) / l;
    }
}
