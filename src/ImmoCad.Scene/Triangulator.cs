namespace ImmoCad.Scene;

/// <summary>
/// Triangulates a simple polygon with holes (ear clipping after bridging each hole to the outline). Plan-detected walls are concave and full of holes,
/// so a fan from the first vertex (fine for rectangles) fills rooms and spills outside the outline.
/// </summary>
public static class Triangulator
{
    /// <summary>Returns the points (outline CCW, then each hole CW; a bridge repeats two of them) and triangles as index triples (counter-clockwise).</summary>
    public static (List<(int X, int Y)> Points, List<int> Triangles) Triangulate(IReadOnlyList<(int X, int Y)> outline, IReadOnlyList<IReadOnlyList<(int X, int Y)>>? holes = null)
    {
        var pts = new List<(int X, int Y)>();
        var ring = Oriented(outline, ccw: true).ToList();
        var rings = new List<List<(int X, int Y)>>();
        foreach (var h in holes ?? []) if (h.Count >= 3) rings.Add(Oriented(h, ccw: false).ToList());
        rings.Sort((a, b) => b.Max(p => p.X).CompareTo(a.Max(p => p.X)));   // rightmost hole first, as in the classic bridging scheme

        var poly = ring;                                                       // current simple polygon (with bridges), as points
        var pending = rings.ToList();
        foreach (var hole in rings)
        {
            pending.Remove(hole);
            var hi = Enumerable.Range(0, hole.Count).MaxBy(i => hole[i].X);
            var m = hole[hi];
            // nearest vertex of the polygon that sees the hole's rightmost vertex (segment crosses no edge of the polygon or of the holes still to bridge)
            var order = Enumerable.Range(0, poly.Count).OrderBy(i => Dist2(poly[i], m)).ToList(); var bi = -1;
            foreach (var i in order) if (Visible(m, poly[i], poly, pending, hole)) { bi = i; break; }
            if (bi < 0) continue;                                              // cannot bridge: the hole is dropped rather than corrupting the cap
            var merged = new List<(int X, int Y)>(poly.Count + hole.Count + 2);
            for (var i = 0; i <= bi; i++) merged.Add(poly[i]);
            for (var k = 0; k <= hole.Count; k++) merged.Add(hole[(hi + k) % hole.Count]);
            merged.Add(poly[bi]);
            for (var i = bi + 1; i < poly.Count; i++) merged.Add(poly[i]);
            poly = merged;
        }
        pts.AddRange(poly);
        var tris = EarClip(pts);
        // sanity check: detected outlines can cross themselves, and ear clipping then spills triangles over the plan.
        // The triangles must add up to outline minus holes, otherwise no cap is drawn (the sides still show the wall).
        double expected = Math.Abs(Area2(outline)) / 2.0 - (holes ?? []).Where(h => h.Count >= 3).Sum(h => Math.Abs(Area2(h)) / 2.0), got = 0;
        for (var i = 0; i + 2 < tris.Count; i += 3) got += Math.Abs(Cross(pts[tris[i]], pts[tris[i + 1]], pts[tris[i + 2]])) / 2.0;
        if (Math.Abs(got - expected) > 0.02 * Math.Abs(expected) + 1000) tris.Clear();
        return (pts, tris);
    }

    private static IEnumerable<(int X, int Y)> Oriented(IReadOnlyList<(int X, int Y)> r, bool ccw) => (Area2(r) >= 0) == ccw ? r : r.Reverse();
    public static long Area2(IReadOnlyList<(int X, int Y)> r) { long a = 0; for (var i = 0; i < r.Count; i++) { var (p, q) = (r[i], r[(i + 1) % r.Count]); a += (long)p.X * q.Y - (long)q.X * p.Y; } return a; }
    private static double Dist2((int X, int Y) a, (int X, int Y) b) { double dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }
    private static long Cross((int X, int Y) o, (int X, int Y) a, (int X, int Y) b) => (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);

    private static bool Visible((int X, int Y) m, (int X, int Y) v, List<(int X, int Y)> poly, List<List<(int X, int Y)>> others, List<(int X, int Y)> own)
    {
        bool Hits(IReadOnlyList<(int X, int Y)> r)
        {
            for (var i = 0; i < r.Count; i++)
            {
                var (a, b) = (r[i], r[(i + 1) % r.Count]);
                if (a == v || b == v || a == m || b == m) continue;
                if (Intersects(m, v, a, b)) return true;
            }
            return false;
        }
        if (Hits(poly) || Hits(own)) return false;
        foreach (var o in others) if (Hits(o)) return false;
        return true;
    }

    private static bool Intersects((int X, int Y) p1, (int X, int Y) p2, (int X, int Y) p3, (int X, int Y) p4)
    {
        var d1 = Cross(p3, p4, p1); var d2 = Cross(p3, p4, p2); var d3 = Cross(p1, p2, p3); var d4 = Cross(p1, p2, p4);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        return d1 == 0 && OnSeg(p3, p4, p1) || d2 == 0 && OnSeg(p3, p4, p2) || d3 == 0 && OnSeg(p1, p2, p3) || d4 == 0 && OnSeg(p1, p2, p4);
    }
    private static bool OnSeg((int X, int Y) a, (int X, int Y) b, (int X, int Y) p) => Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);

    /// <summary>Ear clipping on a counter-clockwise polygon (indices into <paramref name="p"/>; repeated bridge points are allowed).</summary>
    private static List<int> EarClip(List<(int X, int Y)> p)
    {
        var tris = new List<int>(); var n = p.Count; if (n < 3) return tris;
        var prev = new int[n]; var next = new int[n]; for (var i = 0; i < n; i++) { prev[i] = (i + n - 1) % n; next[i] = (i + 1) % n; }
        var left = n; var cur = 0; var guard = 0;
        while (left > 3)
        {
            var a = prev[cur]; var c = next[cur]; var found = false;
            if (Cross(p[a], p[cur], p[c]) <= 0)   // reflex or flat: not an ear; a flat vertex is simply dropped when nothing else can be clipped
            { }
            else
            {
                found = true;
                for (var k = next[c]; k != a; k = next[k])
                {
                    var q = p[k]; if (q == p[a] || q == p[cur] || q == p[c]) continue;
                    if (Cross(p[a], p[cur], q) >= 0 && Cross(p[cur], p[c], q) >= 0 && Cross(p[c], p[a], q) >= 0) { found = false; break; }
                }
            }
            if (found) { tris.AddRange([a, cur, c]); next[a] = c; prev[c] = a; left--; cur = c; guard = 0; continue; }
            cur = next[cur];
            if (++guard > left)
            {   // no ear left (self-touching input): drop a flat vertex, else clip anyway so that the loop ends
                var f = cur; var drop = -1;
                for (var t = 0; t < left; t++, f = next[f]) if (Cross(p[prev[f]], p[f], p[next[f]]) == 0) { drop = f; break; }
                if (drop < 0) { drop = cur; tris.AddRange([prev[drop], drop, next[drop]]); }
                next[prev[drop]] = next[drop]; prev[next[drop]] = prev[drop]; left--; cur = next[drop]; guard = 0;
            }
        }
        if (left == 3 && Cross(p[prev[cur]], p[cur], p[next[cur]]) != 0) tris.AddRange([prev[cur], cur, next[cur]]);
        return tris;
    }
}
