namespace ImmoCad.Plan;

/// <summary>Centre-line segment with a thickness, in the same units as the input outline.</summary>
public readonly record struct WallSegment(PointPx A, PointPx B, double Thickness);

public static class WallFitter
{
    /// <summary>
    /// From a traced outline find pairs of parallel sides facing each other → centre segments with a thickness.
    /// Sides without a partner (or when no pair is found) leave the polygon alone: the caller keeps it.
    /// </summary>
    public static List<WallSegment>? Fit(IReadOnlyList<PointPx> outline, double maxThickness = 600, double minOverlap = 0.5)
    {
        var n = outline.Count; var res = new List<WallSegment>(); var used = new bool[n];
        double Len(int i) => Dist(outline[i], outline[(i + 1) % n]);
        for (var i = 0; i < n; i++)
        {
            if (used[i]) continue;
            var (a, b) = (outline[i], outline[(i + 1) % n]); var l = Len(i); if (l < 1) continue;
            double ux = (b.X - a.X) / l, uy = (b.Y - a.Y) / l; int best = -1; double bestD = double.MaxValue;
            for (var j = 0; j < n; j++)
            {
                if (j == i || used[j]) continue;
                var (c, d) = (outline[j], outline[(j + 1) % n]); var l2 = Len(j); if (l2 < 1) continue;
                double vx = (d.X - c.X) / l2, vy = (d.Y - c.Y) / l2;
                if (Math.Abs(ux * vx + uy * vy + 1) > 0.02) continue;                         // must run in the opposite direction (outline is a loop)
                var dist = Math.Abs((c.X - a.X) * -uy + (c.Y - a.Y) * ux); if (dist > maxThickness || dist < 1) continue;
                // overlap along the axis
                double s0 = 0, s1 = l, t0 = (c.X - a.X) * ux + (c.Y - a.Y) * uy, t1 = (d.X - a.X) * ux + (d.Y - a.Y) * uy; if (t0 > t1) (t0, t1) = (t1, t0);
                var ov = Math.Min(s1, t1) - Math.Max(s0, t0); if (ov < minOverlap * Math.Min(l, l2)) continue;
                if (dist < bestD) { bestD = dist; best = j; }
            }
            if (best < 0) continue;
            var (p, q) = (outline[best], outline[(best + 1) % n]);
            double nx = -uy, ny = ux; var side = (p.X - a.X) * nx + (p.Y - a.Y) * ny; var half = side / 2;
            double lo = Math.Max(0, Math.Min((p.X - a.X) * ux + (p.Y - a.Y) * uy, (q.X - a.X) * ux + (q.Y - a.Y) * uy)), hi = Math.Min(l, Math.Max((p.X - a.X) * ux + (p.Y - a.Y) * uy, (q.X - a.X) * ux + (q.Y - a.Y) * uy));
            res.Add(new WallSegment(new(a.X + ux * lo + nx * half, a.Y + uy * lo + ny * half), new(a.X + ux * hi + nx * half, a.Y + uy * hi + ny * half), Math.Abs(side)));
            used[i] = used[best] = true;
        }
        return res.Count == 0 ? null : res;
    }

    private static double Dist(PointPx a, PointPx b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

public static class WallShapes
{
    /// <summary>
    /// "Make rectangular": the oriented minimal-area rectangle of a polygon, aligned on the polygon's dominant axis (not the image axis).
    /// </summary>
    public static List<PointPx> MakeRectangular(IReadOnlyList<PointPx> poly)
    {
        var axis = WallStraightener.DominantAxis(poly) * Math.PI / 180; double c = Math.Cos(axis), s = Math.Sin(axis);
        // try the dominant axis and a few refinements around it, keep the smallest area
        double bestArea = double.MaxValue; var best = new List<PointPx>();
        for (var d = -3.0; d <= 3.0001; d += 0.25)
        {
            var a = axis + d * Math.PI / 180; double ca = Math.Cos(a), sa = Math.Sin(a);
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var p in poly) { var u = p.X * ca + p.Y * sa; var v = -p.X * sa + p.Y * ca; minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v); }
            var area = (maxU - minU) * (maxV - minV);
            if (area < bestArea - 1e-9) { bestArea = area; PointPx P(double u, double v) => new(u * ca - v * sa, u * sa + v * ca); best = [P(minU, minV), P(maxU, minV), P(maxU, maxV), P(minU, maxV)]; }
        }
        _ = c; _ = s; return best;
    }

    /// <summary>
    /// After straightening, endpoints within the tolerance of another wall's face are snapped onto it, so T and L junctions have no gap or overlap.
    /// Only vertices are moved, and only perpendicular to the face they snap to.
    /// </summary>
    public static List<List<PointPx>> SnapJunctions(IReadOnlyList<IReadOnlyList<PointPx>> polygons, double tolerance)
    {
        var res = polygons.Select(p => p.ToList()).ToList();
        for (var i = 0; i < res.Count; i++)
            for (var k = 0; k < res[i].Count; k++)
            {
                var p = res[i][k]; double bestD = tolerance + 1e-9; PointPx? bestQ = null;
                for (var j = 0; j < res.Count; j++)
                {
                    if (j == i) continue;
                    for (var e = 0; e < res[j].Count; e++)
                    {
                        var a = res[j][e]; var b = res[j][(e + 1) % res[j].Count]; double dx = b.X - a.X, dy = b.Y - a.Y; var l2 = dx * dx + dy * dy; if (l2 < 1e-9) continue;
                        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2; if (t < -0.01 || t > 1.01) continue;
                        var q = new PointPx(a.X + t * dx, a.Y + t * dy); var d = Dist(p, q);
                        if (d > 1e-9 && d < bestD) { bestD = d; bestQ = q; }
                    }
                }
                if (bestQ is { } snap) res[i][k] = snap;
            }
        return res;
    }
    private static double Dist(PointPx a, PointPx b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
