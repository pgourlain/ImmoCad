using ImmoCad.Core.Model;

namespace ImmoCad.Core.Geometry;

/// <summary>
/// Finds the parking bay under a point from the white lines painted on the floor (floor markings) and the walls: a bay is the strip between two parallel lines, closed by the line at its back
/// and open on the aisle side, where it stops at the end of the side lines. Works whatever the orientation of the row, and inside a room that is itself a parking.
/// </summary>
public sealed class ParkingBays
{
    public const int MinWidthMm = 1800, MaxWidthMm = 4500, MinDepthMm = 3000, MaxDepthMm = 7500, StepMm = 25, ReachMm = 9000;
    private readonly record struct Solid(int X0, int Y0, int X1, int Y1, List<PointMm> Outline, List<List<PointMm>> Holes, bool Paint);
    private readonly List<Solid> _solids = new();

    /// <summary>The level's lines and walls are copied, so <see cref="Find"/> can run on another thread.</summary>
    public ParkingBays(Level level)
    {
        foreach (var m in level.Markings.Where(m => m.Outline.Count >= 3)) Add(m.Outline, m.Holes, true);
        foreach (var w in level.Walls.Where(w => w.Footprint.Count >= 3)) Add(w.Footprint, w.Holes, false);
    }
    private void Add(List<PointMm> outline, List<List<PointMm>> holes, bool paint)
    {
        var o = outline.ToList(); _solids.Add(new(o.Min(p => p.X), o.Min(p => p.Y), o.Max(p => p.X), o.Max(p => p.Y), o, holes.Select(h => h.ToList()).ToList(), paint));
    }

    /// <summary>0 free, 1 painted line, 2 wall.</summary>
    private int Hit(double x, double y)
    {
        var p = new PointMm((int)Math.Round(x), (int)Math.Round(y)); var res = 0;
        foreach (var s in _solids)
            if (p.X >= s.X0 && p.X <= s.X1 && p.Y >= s.Y0 && p.Y <= s.Y1 && SpaceFinder.Contains(s.Outline, p) && !s.Holes.Any(h => SpaceFinder.Contains(h, p))) { if (s.Paint) return 1; res = 2; }
        return res;
    }
    private bool Blocked(double x, double y) => Hit(x, y) != 0;

    /// <summary>The bay (four corners, millimetres) around <paramref name="click"/>, or null when the point is not between lines that look like a parking bay.</summary>
    public List<PointMm>? Find(PointMm click)
    {
        if (!_solids.Any(s => s.Paint) || Blocked(click.X, click.Y)) return null;
        List<PointMm>? best = null; long bestArea = long.MaxValue;
        foreach (var theta in LineDirections(click))                                                 // the directions of the painted lines near the click
            foreach (var t in new[] { theta, theta + Math.PI / 2 })                                  // the long lines may be the sides of the bays or the line at their back: try both
                if ((Try(click, t) ?? Try(click, Refine(click, t))) is { } r && SpaceFinder.AreaMm2(r) is var a && a < bestArea) { best = r; bestArea = a; }
        return best;
    }

    /// <summary>
    /// Lines of a plan drawn from a photo are rarely exactly at the voted angle: the strip between the two side lines is measured 1.5 m on each side of the click, and the direction is turned
    /// by the angle at which the middle of the strip drifts (twice, so that 2-3° of error are absorbed). Unchanged when the strip cannot be measured.
    /// </summary>
    private double Refine(PointMm click, double theta)
    {
        const double Reach = 1500;
        for (var pass = 0; pass < 2; pass++)
        {
            double ux = Math.Cos(theta), uy = Math.Sin(theta), vx = -uy, vy = ux;
            double? Mid(double du)
            {
                double ox = click.X + ux * du, oy = click.Y + uy * du;
                double? Face(double sign) { for (double d = StepMm; d <= 4500; d += StepMm) if (Blocked(ox + vx * sign * d, oy + vy * sign * d)) return d; return null; }
                return Blocked(ox, oy) || Face(-1) is not { } l || Face(1) is not { } r || l + r < MinWidthMm || l + r > MaxWidthMm ? null : (r - l) / 2;
            }
            if (Mid(Reach) is not { } a || Mid(-Reach) is not { } b) return theta;
            var eps = Math.Atan((a - b) / (2 * Reach)); if (Math.Abs(eps) < 0.002 || Math.Abs(eps) > 0.35) return theta;
            theta += eps;
        }
        return theta;
    }

    /// <summary>
    /// The directions (radians) of the long painted lines within a few metres of the point, the most present first (at most three): the edges vote, weighted by their length, in bins of 2°
    /// (a direction counts the same turned half a turn). A plan drawn square stays exactly square.
    /// </summary>
    private List<double> LineDirections(PointMm c)
    {
        const int R = 6000, MinEdge = 800, Bins = 90; var votes = new double[Bins];
        foreach (var s in _solids.Where(s => s.Paint && s.X1 >= c.X - R && s.X0 <= c.X + R && s.Y1 >= c.Y - R && s.Y0 <= c.Y + R))
            for (var i = 0; i < s.Outline.Count; i++)
            {
                var a = s.Outline[i]; var b = s.Outline[(i + 1) % s.Outline.Count]; double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy); if (len < MinEdge) continue;
                var deg = Math.Atan2(dy, dx) * 180 / Math.PI; deg = ((deg % 180) + 180) % 180; votes[(int)(deg / 2) % Bins] += len;
            }
        var res = new List<double>(); var used = new bool[Bins];
        for (var k = 0; k < 3; k++)
        {
            var bi = -1; for (var i = 0; i < Bins; i++) if (!used[i] && votes[i] > 0 && (bi < 0 || votes[i] > votes[bi])) bi = i;
            if (bi < 0 || votes[bi] < MinEdge) break;
            for (var d = -2; d <= 2; d++) used[(bi + d + Bins) % Bins] = true;                       // the neighbouring bins are the same line
            double sx = 0, sy = 0; for (var d = -1; d <= 1; d++) { var w = votes[(bi + d + Bins) % Bins]; var ang = ((bi + d + Bins) % Bins * 2 + 1) * Math.PI / 180; sx += w * Math.Cos(2 * ang); sy += w * Math.Sin(2 * ang); }
            var theta = 0.5 * Math.Atan2(sy, sx); var quarter = Math.PI / 2; var snapped = Math.Round(theta / quarter) * quarter;
            res.Add(Math.Abs(theta - snapped) < 3 * Math.PI / 180 ? snapped : theta);
        }
        return res;
    }

    private List<PointMm>? Try(PointMm click, double theta)
    {
        double ux = Math.Cos(theta), uy = Math.Sin(theta), vx = -uy, vy = ux;
        if (Math.Abs(ux) < 1e-9) ux = 0; if (Math.Abs(uy) < 1e-9) uy = 0; if (Math.Abs(vx) < 1e-9) vx = 0; if (Math.Abs(vy) < 1e-9) vy = 0;
        (double X, double Y) At((double X, double Y) o, double du, double dv) => (o.X + ux * du + vx * dv, o.Y + uy * du + vy * dv);
        double? March((double X, double Y) o, double du, double dv, double max)
        {
            for (double d = StepMm; d <= max; d += StepMm)
            {
                var p = At(o, du * d, dv * d); if (!Blocked(p.X, p.Y)) continue;
                double lo = d - StepMm, hi = d;                                                      // the face lies between the last free sample and this one: narrow it down to the millimetre
                while (hi - lo > 1) { var mid = (lo + hi) / 2; var q = At(o, du * mid, dv * mid); if (Blocked(q.X, q.Y)) hi = mid; else lo = mid; }
                return hi;
            }
            return null;
        }
        double Thickness((double X, double Y) o, double du, double dv, double from)
        {   // how far the line goes on from its face (a wall is thicker than any line: capped)
            double d = from; while (d < from + 450 && Blocked(At(o, du * d, dv * d).X, At(o, du * d, dv * d).Y)) d += StepMm; return d - from;
        }
        var c0 = ((double)click.X, (double)click.Y);
        if (March(c0, 0, -1, 4500) is not { } dl || March(c0, 0, 1, 4500) is not { } dr) return null;
        var tl = Math.Min(Thickness(c0, 0, -1, dl), 200); var tr = Math.Min(Thickness(c0, 0, 1, dr), 200);
        var width = dl + dr; if (width < MinWidthMm || width > MaxWidthMm) return null;
        var c = At(c0, 0, (dr - dl) / 2);                                                          // the middle of the strip
        var back1 = March(c, 1, 0, ReachMm); var back2 = March(c, -1, 0, ReachMm);
        // the end of a side line, looking along the strip from the click: where the paint stops
        double? LineEnd(double side, double sign)
        {
            var centre = At(c, 0, side); if (!Blocked(centre.X, centre.Y)) return null;
            for (double d = StepMm; d <= ReachMm; d += StepMm)
            {
                var p = At(centre, sign * d, 0); if (Blocked(p.X, p.Y)) continue;
                double lo = d - StepMm, hi = d; while (hi - lo > 1) { var mid = (lo + hi) / 2; var q = At(centre, sign * mid, 0); if (Blocked(q.X, q.Y)) lo = mid; else hi = mid; }
                return lo;                                                                          // the last painted point
            }
            return null;
        }
        double? Open(double sign)
        {
            var l = LineEnd(-(width / 2 + tl / 2), sign); var r = LineEnd(width / 2 + tr / 2, sign);
            return l != null && r != null ? Math.Min(l.Value, r.Value) : l ?? r;
        }
        double fromU, toU; var backs = new List<double>();                                          // where the line at the back (or the two end lines) must be
        var closed = false;
        if (back1 != null && back2 != null && back1.Value + back2.Value <= MaxDepthMm) { fromU = -back2.Value; toU = back1.Value; backs.Add(toU); backs.Add(fromU); closed = true; }   // closed at both ends
        else
        {
            var useFirst = back1 != null && (back2 == null || back1 <= back2);
            if (useFirst) { if (Open(-1) is not { } e) return null; fromU = -e; toU = back1!.Value; backs.Add(toU); }
            else if (back2 != null) { if (Open(1) is not { } e) return null; fromU = -back2.Value; toU = e; backs.Add(fromU); }
            else return null;
        }
        var depth = toU - fromU; if (depth < MinDepthMm || depth > MaxDepthMm) return null;
        // the lines must really be there all along the bay: both sides and the back, mostly painted (a room between walls, or scattered scraps of paint, is not a bay)
        int total = 0, solid = 0, paint = 0;
        void Sample((double X, double Y) p) { total++; var h = Hit(p.X, p.Y); if (h != 0) solid++; if (h == 1) paint++; }
        // an end line need not be square to the sides (a bay drawn as a parallelogram): it is followed from one side to the other, from where each point of the strip first meets it
        double? EndAt(double v, double sign)
        {
            var o = At(c, 0, v); if (Blocked(o.X, o.Y)) return null;
            for (double d = StepMm; d <= MaxDepthMm; d += StepMm) { var p = At(o, sign * d, 0); if (Blocked(p.X, p.Y)) { double lo = d - StepMm, hi = d; while (hi - lo > 1) { var mid = (lo + hi) / 2; var q = At(o, sign * mid, 0); if (Blocked(q.X, q.Y)) hi = mid; else lo = mid; } return sign * hi; } }
            return null;
        }
        double fromLo = fromU, fromHi = fromU, toLo = toU, toHi = toU;                              // end positions on the -v side and on the +v side
        void Back(double b)
        {
            var sign = b > 0 ? 1.0 : -1.0; double? prev = null, first = null, last = null;
            for (var d = -width / 2 + 60; d <= width / 2 - 60; d += 50)
            {
                var hit = EndAt(d, sign);
                if (hit == null || prev != null && Math.Abs(hit.Value - prev.Value) > 150) { total++; prev = hit; continue; }             // no line here, or a jump: not a line
                prev = hit; first ??= hit; last = hit; Sample(At(c, hit.Value + sign * 30, d));
            }
            if (first == null || last == null) return;
            var slope = (last.Value - first.Value) / Math.Max(width - 120, 1);                         // per millimetre across the strip
            var atLo = first.Value - slope * 60; var atHi = last.Value + slope * 60;
            if (sign > 0) { toLo = atLo; toHi = atHi; } else { fromLo = atLo; fromHi = atHi; }
        }
        if (closed) foreach (var b in backs) Back(b);
        else foreach (var b in backs) for (var d = -width / 2 + 60; d <= width / 2 - 60; d += 50) Sample(At(c, b + (b > 0 ? 30 : -30), d));
        for (var k = 0; k < 2; k++)
        {
            var side = k == 0 ? -(width / 2 + Math.Min(tl, 200) / 2) : width / 2 + Math.Min(tr, 200) / 2;
            double f = k == 0 ? fromLo : fromHi, t = k == 0 ? toLo : toHi;
            for (var d = f + 60; d <= t - 60; d += 50) Sample(At(c, d, side));
        }
        if (total == 0 || solid < 0.88 * total || paint < 0.6 * total) return null;
        PointMm P((double X, double Y) q) => new((int)Math.Round(q.X), (int)Math.Round(q.Y));
        return [P(At(c, fromLo, -width / 2)), P(At(c, toLo, -width / 2)), P(At(c, toHi, width / 2)), P(At(c, fromHi, width / 2))];
    }
}
