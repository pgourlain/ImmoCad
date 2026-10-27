namespace ImmeubleCad.Plan;

public static class WallStraightener
{
    /// <summary>Remove jogs smaller than tolerance, snap near-axis edges to the dominant axis, merge collinear points.</summary>
    public static List<PointPx> Straighten(IReadOnlyList<PointPx> poly, double tolerance = 30, double angleDeg = 5)
    {
        if (poly.Count < 3 || tolerance <= 0) return poly.ToList();
        var axis = DominantAxis(poly);
        var pts = poly.ToList();
        pts = DropShort(pts, tolerance);
        pts = MergeCollinear(pts, Math.Max(1e-6, tolerance / 2));
        pts = SnapAngles(pts, axis, angleDeg);
        return MergeCollinear(pts, 1e-6);
    }

    public static double DominantAxis(IReadOnlyList<PointPx> poly)
    {
        var hist = new double[180];
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count];
            var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            var ang = (Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI + 360) % 180;
            hist[(int)ang % 180] += len;
        }
        // fold onto 0..90 (axis and its perpendicular)
        var folded = new double[90];
        for (var i = 0; i < 180; i++) folded[i % 90] += hist[i];
        return Array.IndexOf(folded, folded.Max());
    }

    private static List<PointPx> DropShort(List<PointPx> p, double tol)
    {
        var r = p.ToList();
        for (var changed = true; changed && r.Count > 3;)
        {
            changed = false;
            for (var i = 0; i < r.Count && r.Count > 3; i++)
            {
                var a = r[i]; var b = r[(i + 1) % r.Count];
                if (Dist(a, b) < tol) { r[i] = new((a.X + b.X) / 2, (a.Y + b.Y) / 2); r.RemoveAt((i + 1) % r.Count); changed = true; break; }
            }
        }
        return r;
    }

    private static List<PointPx> SnapAngles(List<PointPx> p, double axisDeg, double tolDeg)
    {
        var n = p.Count;
        // each edge becomes a line (point + direction); near-axis edges are snapped
        var lines = new (PointPx O, double Dx, double Dy)[n];
        for (var i = 0; i < n; i++)
        {
            var a = p[i]; var b = p[(i + 1) % n];
            var ang = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
            var rel = ((ang - axisDeg) % 90 + 90) % 90;
            var off = rel < 45 ? rel : rel - 90;
            if (Math.Abs(off) <= tolDeg) ang -= off;
            var r = ang * Math.PI / 180;
            lines[i] = (new((a.X + b.X) / 2, (a.Y + b.Y) / 2), Math.Cos(r), Math.Sin(r));
        }
        var res = new List<PointPx>(n);
        for (var i = 0; i < n; i++)
        {
            var l1 = lines[(i + n - 1) % n]; var l2 = lines[i];
            var det = l1.Dx * l2.Dy - l1.Dy * l2.Dx;
            if (Math.Abs(det) < 1e-9) { res.Add(p[i]); continue; }
            var t = ((l2.O.X - l1.O.X) * l2.Dy - (l2.O.Y - l1.O.Y) * l2.Dx) / det;
            res.Add(new(l1.O.X + t * l1.Dx, l1.O.Y + t * l1.Dy));
        }
        return res;
    }

    private static List<PointPx> MergeCollinear(List<PointPx> p, double tol)
    {
        var r = p.ToList();
        for (var i = 0; i < r.Count && r.Count > 3;)
        {
            var a = r[(i + r.Count - 1) % r.Count]; var b = r[i]; var c = r[(i + 1) % r.Count];
            var len = Dist(a, c);
            var d = len < 1e-9 ? Dist(a, b) : Math.Abs((c.X - a.X) * (a.Y - b.Y) - (a.X - b.X) * (c.Y - a.Y)) / len;
            if (d <= tol) r.RemoveAt(i); else i++;
        }
        return r;
    }

    private static double Dist(PointPx a, PointPx b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
