using ImmoCad.Core.Model;
using ImmoCad.Core.Site;

namespace ImmoCad.Core.Geometry;

public sealed record WallShift(Guid WallId, Dictionary<PointMm, PointMm> Moves, double MaxMoveMm);
public sealed record ScaleSuggestion(double WidthPercent, double HeightPercent, double RotationDeg, string Message);
public sealed record CornerMismatch(int Index, PointMm At, double PlanAngleDeg, double FootprintAngleDeg, double DiffDeg);

/// <summary>Everything that compares the plan with the cadastral footprint. The cadastre is a guide, not the truth: these functions only suggest; nothing here edits the model.</summary>
public static class FootprintFit
{
    /// <summary>The cadastral footprint in the model's millimetre frame (through the site anchor).</summary>
    public static List<PointMm> ToModel(SiteInfo site, IEnumerable<double[]> ringLambert)
    {
        var a = new SiteAnchor(site.EastM, site.NorthM, site.RotationDeg, site.AltitudeM);
        return ringLambert.Select(p => { var (x, y) = a.FromLambert(p[0], p[1]); return new PointMm(x, y); }).ToList();
    }

    /// <summary>Anchor from two corners of the building and the matching corners of the footprint (Lambert 93): rotation + translation (the scale is the cadastre's, 1:1).</summary>
    public static SiteInfo? Anchor(PointMm planA, PointMm planB, (double E, double N) lamA, (double E, double N) lamB, List<double[]>? footprint = null)
    {
        double px = (planB.X - planA.X) / 1000.0, py = (planB.Y - planA.Y) / 1000.0, lx = lamB.E - lamA.E, ly = lamB.N - lamA.N;
        if (Math.Sqrt(px * px + py * py) < 0.05 || Math.Sqrt(lx * lx + ly * ly) < 0.05) return null;
        var rot = Math.Atan2(ly, lx) - Math.Atan2(py, px); double c = Math.Cos(rot), s = Math.Sin(rot);
        double ax = planA.X / 1000.0, ay = planA.Y / 1000.0;
        return new SiteInfo { RotationDeg = rot * 180 / Math.PI, EastM = lamA.E - (ax * c - ay * s), NorthM = lamA.N - (ax * s + ay * c), Footprint = footprint ?? new() };
    }

    /// <summary>
    /// Facade walls (the ones with a face within <paramref name="toleranceMm"/> of a footprint edge and roughly parallel to it) are moved so that the outer face lies on the edge.
    /// Returns the suggested shifts for a preview; null entries are skipped.
    /// </summary>
    public static List<WallShift> Suggest(Level level, IReadOnlyList<PointMm> footprint, int toleranceMm = 300, double angleDeg = 8)
    {
        var res = new List<WallShift>(); if (footprint.Count < 3) return res;
        double fcx = footprint.Average(p => (double)p.X), fcy = footprint.Average(p => (double)p.Y);
        foreach (var w in level.Walls.Where(w => w.Footprint.Count >= 3))
        {
            var moves = new Dictionary<PointMm, PointMm>(); var fp = w.Footprint; double wcx = fp.Average(p => (double)p.X), wcy = fp.Average(p => (double)p.Y);
            for (var i = 0; i < fp.Count; i++)
            {
                var a = fp[i]; var b = fp[(i + 1) % fp.Count]; if (Dist(a, b) < 300) continue;
                for (var j = 0; j < footprint.Count; j++)
                {
                    var c = footprint[j]; var d = footprint[(j + 1) % footprint.Count];
                    if (Math.Abs(AngleDiff(Ang(a, b), Ang(c, d))) > angleDeg) continue;
                    var da = Signed(c, d, a); var db = Signed(c, d, b); if (Math.Abs(da) > toleranceMm || Math.Abs(db) > toleranceMm) continue;
                    // only the OUTER face: it lies on the outward side of the wall's centre (the side away from the footprint's centre)
                    var outward = Signed(c, d, new PointMm((int)fcx, (int)fcy)) > 0 ? -1 : 1;                // sign of "away from the inside" along the edge normal
                    var faceMid = (da + db) / 2; var centre = Signed(c, d, new PointMm((int)wcx, (int)wcy));
                    if ((faceMid - centre) * outward <= 0) continue;
                    PointMm Project(PointMm p) { double dx = d.X - c.X, dy = d.Y - c.Y; var l2 = dx * dx + dy * dy; var t = ((p.X - c.X) * dx + (p.Y - c.Y) * dy) / l2; return new((int)Math.Round(c.X + t * dx), (int)Math.Round(c.Y + t * dy)); }
                    if (Math.Abs(da) > 0.5) moves[a] = Project(a); if (Math.Abs(db) > 0.5) moves[b] = Project(b);
                }
            }
            if (moves.Count > 0) res.Add(new WallShift(w.Id, moves, moves.Max(m => Dist(m.Key, m.Value))));
        }
        return res;
    }

    /// <summary>"Your plan is 2.8 % wider than the cadastre": compares the oriented extents of the outer outline and the footprint.</summary>
    public static ScaleSuggestion? CheckScale(IReadOnlyList<PointMm> planOutline, IReadOnlyList<PointMm> footprint)
    {
        if (planOutline.Count < 3 || footprint.Count < 3) return null;
        var fa = Axis(footprint); var (fw, fh, _) = Extents(footprint, fa); var pa = Axis(planOutline); var (pw, ph, _) = Extents(planOutline, pa);
        if (fw < 1 || fh < 1) return null;
        var rot = AngleDiff(pa, fa); var swap = Math.Abs(rot) > 45;           // the two main axes may be exchanged
        if (swap) { (pw, ph) = (ph, pw); rot = AngleDiff(pa + 90, fa); }
        var wp = (pw / fw - 1) * 100; var hp = (ph / fh - 1) * 100;
        string Dir(double v, string more, string less) => Math.Abs(v) < 0.5 ? "" : Translations.T(v > 0 ? more : less, Math.Abs(v).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        var parts = new[] { Dir(wp, "scale.wider", "scale.narrower"), Dir(hp, "scale.longer", "scale.shorter") }.Where(x => x.Length > 0).ToList();
        return new ScaleSuggestion(wp, hp, rot, parts.Count == 0 ? Translations.T("scale.ok") : Translations.T("scale.msg", string.Join(Translations.T("scale.and"), parts)));
    }

    /// <summary>Corners of the plan outline whose angle differs from the footprint's matching corner by more than the threshold.</summary>
    public static List<CornerMismatch> AngleCheck(IReadOnlyList<PointMm> plan, IReadOnlyList<PointMm> footprint, double thresholdDeg = 2)
    {
        var res = new List<CornerMismatch>();
        for (var i = 0; i < plan.Count; i++)
        {
            var prev = plan[(i + plan.Count - 1) % plan.Count]; var cur = plan[i]; var next = plan[(i + 1) % plan.Count];
            var pAngle = Interior(prev, cur, next);
            // nearest footprint corner
            var j = Enumerable.Range(0, footprint.Count).MinBy(k => Dist(footprint[k], cur));
            if (Dist(footprint[j], cur) > 1500) continue;
            var fAngle = Interior(footprint[(j + footprint.Count - 1) % footprint.Count], footprint[j], footprint[(j + 1) % footprint.Count]);
            if (Math.Abs(pAngle - fAngle) > thresholdDeg) res.Add(new CornerMismatch(i, cur, pAngle, fAngle, pAngle - fAngle));
        }
        return res;
    }

    /// <summary>Footprint edges that coincide with an edge of a neighbouring building: party walls (mitoyenneté). Returns the edge indices.</summary>
    public static List<int> SharedEdges(IReadOnlyList<PointMm> footprint, IEnumerable<IReadOnlyList<PointMm>> neighbours, int toleranceMm = 400)
    {
        var res = new List<int>();
        for (var i = 0; i < footprint.Count; i++)
        {
            var a = footprint[i]; var b = footprint[(i + 1) % footprint.Count];
            if (neighbours.Any(n => Enumerable.Range(0, n.Count).Any(j => { var c = n[j]; var d = n[(j + 1) % n.Count]; return Math.Abs(AngleDiff(Ang(a, b), Ang(c, d))) < 3 && Math.Abs(Signed(c, d, a)) < toleranceMm && Math.Abs(Signed(c, d, b)) < toleranceMm && Overlap(a, b, c, d) > 0.3 * Dist(a, b); })))
                res.Add(i);
        }
        return res;
    }

    private static double Overlap(PointMm a, PointMm b, PointMm c, PointMm d)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y; var l = Math.Sqrt(ux * ux + uy * uy); ux /= l; uy /= l;
        double t0 = (c.X - a.X) * ux + (c.Y - a.Y) * uy, t1 = (d.X - a.X) * ux + (d.Y - a.Y) * uy; if (t0 > t1) (t0, t1) = (t1, t0); return Math.Min(l, t1) - Math.Max(0, t0);
    }
    private static double Axis(IReadOnlyList<PointMm> poly) => Plan.WallStraightener.DominantAxis(poly.Select(p => new Plan.PointPx(p.X, p.Y)).ToList());
    private static (double W, double H, double Area) Extents(IReadOnlyList<PointMm> poly, double axisDeg)
    {
        var r = axisDeg * Math.PI / 180; double c = Math.Cos(r), s = Math.Sin(r); var u = poly.Select(p => p.X * c + p.Y * s).ToList(); var v = poly.Select(p => -p.X * s + p.Y * c).ToList();
        return (u.Max() - u.Min(), v.Max() - v.Min(), 0);
    }
    private static double Interior(PointMm p, PointMm c, PointMm n)
    {
        double ax = p.X - c.X, ay = p.Y - c.Y, bx = n.X - c.X, by = n.Y - c.Y; return Math.Acos(Math.Clamp((ax * bx + ay * by) / (Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by)), -1, 1)) * 180 / Math.PI;
    }
    private static double Signed(PointMm c, PointMm d, PointMm p) { double dx = d.X - c.X, dy = d.Y - c.Y; var l = Math.Sqrt(dx * dx + dy * dy); return ((p.X - c.X) * -dy + (p.Y - c.Y) * dx) / l; }
    private static double Ang(PointMm a, PointMm b) => Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
    private static double AngleDiff(double a, double b) => ((a - b) % 180 + 270) % 180 - 90;
    private static double Dist(PointMm a, PointMm b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
