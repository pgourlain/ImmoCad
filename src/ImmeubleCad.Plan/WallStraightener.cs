namespace ImmeubleCad.Plan;

public static class WallStraightener
{
    /// <summary>
    /// Straightens a traced wall outline with as few segments as possible:
    /// 1. dominant axes of the polygon (so "straight" means aligned with the wall, not the image);
    /// 2. Douglas–Peucker removes jogs and saw teeth smaller than the tolerance;
    /// 3. every remaining side is a least-squares line through the original points it replaces;
    /// 4. lines within <paramref name="angleDeg"/> of the main axes are made exactly parallel/perpendicular to them;
    /// 5. corners are rebuilt as line intersections and collinear neighbours are merged.
    /// Tolerance 0 leaves the outline unchanged.
    /// </summary>
    public static List<PointPx> Straighten(IReadOnlyList<PointPx> poly, double tolerance = 30, double angleDeg = 5, IReadOnlyList<double>? referenceAnglesDeg = null)
    {
        if (poly.Count < 4 || tolerance <= 0) return poly.ToList();
        var axis = DominantAxis(poly);
        var keep = Reduce(poly, tolerance);
        if (keep.Count < 3) return poly.ToList();
        // one point set per side, then merge neighbouring sides that lie on the same line (within the tolerance) and refit them together
        var sets = new List<List<PointPx>>();
        for (var i = 0; i < keep.Count; i++)
        {
            var from = keep[i]; var to = keep[(i + 1) % keep.Count]; var pts = new List<PointPx>();
            for (var k = from; ; k = (k + 1) % poly.Count) { pts.Add(poly[k]); if (k == to) break; }
            sets.Add(pts);
        }
        var fits = sets.Select(p => Fit(p, axis, angleDeg, referenceAnglesDeg)).ToList();
        for (var merged = true; merged && sets.Count > 3;)
        {
            merged = false;
            for (var i = 0; i < sets.Count && sets.Count > 3; i++)
            {
                var j = (i + 1) % sets.Count; var l1 = fits[i]; var l2 = fits[j];
                var parallel = Math.Abs(l1.Dx * l2.Dy - l1.Dy * l2.Dx) < 0.02;
                var offset = Math.Max(Math.Abs((l2.Cx - l1.Cx) * -l1.Dy + (l2.Cy - l1.Cy) * l1.Dx), Math.Abs((l1.Cx - l2.Cx) * -l2.Dy + (l1.Cy - l2.Cy) * l2.Dx));
                if (!parallel || offset > tolerance) continue;
                sets[i] = sets[i].Concat(sets[j]).ToList(); fits[i] = Fit(sets[i], axis, angleDeg, referenceAnglesDeg); sets.RemoveAt(j); fits.RemoveAt(j); merged = true; break;
            }
        }
        var n = sets.Count; var corners = new List<PointPx>(n);
        for (var i = 0; i < n; i++) corners.Add(Intersect(fits[(i + n - 1) % n], fits[i]) ?? sets[i][0]);
        return corners;
    }

    private readonly record struct Line(double Cx, double Cy, double Dx, double Dy);

    /// <summary>Least-squares line (total least squares); snapped to a main axis when within the angle tolerance.</summary>
    private static Line Fit(List<PointPx> pts, double axisDeg, double tolDeg, IReadOnlyList<double>? reference = null)
    {
        double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
        double sxx = 0, sxy = 0, syy = 0; foreach (var p in pts) { sxx += (p.X - cx) * (p.X - cx); sxy += (p.X - cx) * (p.Y - cy); syy += (p.Y - cy) * (p.Y - cy); }
        var ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);                       // principal direction
        if (pts.Count == 2 || sxx + syy < 1e-9) { var d = pts[^1]; ang = Math.Atan2(d.Y - pts[0].Y, d.X - pts[0].X); }
        var deg = ang * 180 / Math.PI; var rel = ((deg - axisDeg) % 90 + 90) % 90; var off = rel < 45 ? rel : rel - 90;
        // reference directions (e.g. the cadastral footprint's real angles) win over the 90° axes when one is close enough
        var bestRef = reference?.Select(r => (Ref: r, D: ((deg - r) % 180 + 270) % 180 - 90)).Where(x => Math.Abs(x.D) <= tolDeg).OrderBy(x => Math.Abs(x.D)).Cast<(double Ref, double D)?>().FirstOrDefault();
        if (bestRef is { } br) ang = (deg - br.D) * Math.PI / 180;
        else if (Math.Abs(off) <= tolDeg) ang = (deg - off) * Math.PI / 180;
        return new Line(cx, cy, Math.Cos(ang), Math.Sin(ang));
    }

    private static PointPx? Intersect(Line a, Line b)
    {
        var det = a.Dx * b.Dy - a.Dy * b.Dx; if (Math.Abs(det) < 1e-6) return null;
        var t = ((b.Cx - a.Cx) * b.Dy - (b.Cy - a.Cy) * b.Dx) / det;
        return new(a.Cx + t * a.Dx, a.Cy + t * a.Dy);
    }

    /// <summary>Douglas–Peucker on a closed polygon; returns the indices of the points kept.</summary>
    private static List<int> Reduce(IReadOnlyList<PointPx> poly, double tol)
    {
        var n = poly.Count; var keep = new bool[n];
        double cx = poly.Average(p => p.X), cy = poly.Average(p => p.Y);
        var a = Enumerable.Range(0, n).MaxBy(i => Math.Pow(poly[i].X - cx, 2) + Math.Pow(poly[i].Y - cy, 2));
        var b = Enumerable.Range(0, n).MaxBy(i => Math.Pow(poly[i].X - poly[a].X, 2) + Math.Pow(poly[i].Y - poly[a].Y, 2));
        keep[a] = keep[b] = true;
        void Rdp(int from, int to)    // indices on the closed ring, from → to going forward
        {
            var stack = new Stack<(int, int)>(); stack.Push((from, to));
            while (stack.Count > 0)
            {
                var (f, t) = stack.Pop(); var len = (t - f + n) % n; if (len < 2) continue;
                int worst = -1; double wd = tol;
                for (var k = 1; k < len; k++) { var idx = (f + k) % n; var d = Seg(poly[idx], poly[f], poly[t]); if (d > wd) { wd = d; worst = idx; } }
                if (worst < 0) continue; keep[worst] = true; stack.Push((f, worst)); stack.Push((worst, t));
            }
        }
        Rdp(a, b); Rdp(b, a);
        return Enumerable.Range(0, n).Where(i => keep[i]).ToList();
    }

    private static double Seg(PointPx p, PointPx a, PointPx b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y; var l2 = dx * dx + dy * dy; if (l2 < 1e-12) return Dist(p, a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1); return Dist(p, new(a.X + t * dx, a.Y + t * dy));
    }

    /// <summary>
    /// The building's main axis in degrees (0 ≤ axis &lt; 90; the perpendicular is implied): length-weighted circular mean of the
    /// edge directions mod 90°, computed on 4θ so that an axis and its perpendicular reinforce each other. Continuous, not binned.
    /// </summary>
    public static double DominantAxis(IReadOnlyList<PointPx> poly)
    {
        double sx = 0, sy = 0;
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count];
            var len = Dist(a, b); if (len < 1e-9) continue;
            var th = Math.Atan2(b.Y - a.Y, b.X - a.X) * 4; var w = len * len;      // long edges dominate: they carry the true direction, short noisy ones do not
            sx += Math.Cos(th) * w; sy += Math.Sin(th) * w;
        }
        var axis = Math.Atan2(sy, sx) / 4 * 180 / Math.PI;
        return ((axis % 90) + 90) % 90;
    }

    private static double Dist(PointPx a, PointPx b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
