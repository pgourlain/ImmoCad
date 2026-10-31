using ImmoCad.Core.Model;

namespace ImmoCad.Core.Geometry;

/// <summary>What painting a zone does to one wall: the thin parts become floor markings, the rest stays a wall (possibly in several pieces, possibly nothing).</summary>
public sealed record WallSplit(Guid WallId, List<Shape> Thin, List<Shape> Rest);

/// <summary>Planning of the conversions between walls and floor markings (no model change here: <see cref="History.BuildingActions"/> applies a plan as one history step).</summary>
public static class FloorMarkings
{
    public static List<PointMm> RectZone(PointMm a, PointMm b) { var r = RectMm.Of(a, b); return [new(r.X0, r.Y0), new(r.X1, r.Y0), new(r.X1, r.Y1), new(r.X0, r.Y1)]; }

    /// <summary>The walls with material thinner than <paramref name="maxThicknessMm"/> inside the zone, and what becomes of each.</summary>
    public static List<WallSplit> PlanPaint(Level level, IReadOnlyList<PointMm> zone, int maxThicknessMm, CancellationToken ct = default)
    {
        var res = new List<WallSplit>(); if (zone.Count < 3) return res;
        int zx0 = zone.Min(p => p.X), zy0 = zone.Min(p => p.Y), zx1 = zone.Max(p => p.X), zy1 = zone.Max(p => p.Y);
        var candidates = level.Walls.Where(w => w.Footprint.Count >= 3 && w.Footprint.Max(p => p.X) >= zx0 && w.Footprint.Min(p => p.X) <= zx1 && w.Footprint.Max(p => p.Y) >= zy0 && w.Footprint.Min(p => p.Y) <= zy1).ToList();
        var results = new WallSplit?[candidates.Count];
        Parallel.For(0, candidates.Count, new ParallelOptions { CancellationToken = ct }, i =>        // every wall is rasterised on its own: independent, so they run side by side
        {
            var w = candidates[i]; var (thin, rest) = PolygonOps.SplitThin(new Shape(w.Footprint.ToList(), w.Holes.Select(h => h.ToList()).ToList()), maxThicknessMm, zone);
            if (thin.Count > 0) results[i] = new WallSplit(w.Id, thin, rest);
        });
        foreach (var r in results) if (r != null) res.Add(r);
        return res;
    }

    /// <summary>The markings that reach into the zone.</summary>
    public static List<FloorMarking> InZone(Level level, IReadOnlyList<PointMm> zone) =>
        level.Markings.Where(m => PolygonOps.Touches(new Shape(m.Outline, m.Holes), zone)).ToList();
}
