using ImmoCad.Core.Model;
using ImmoCad.Plan;

namespace ImmoCad.Core.Geometry;

/// <summary>A filled shape: an outline and the holes in it (even-odd), in millimetres.</summary>
public sealed record Shape(List<PointMm> Outline, List<List<PointMm>> Holes);

/// <summary>
/// Polygon editing for walls detected from a plan: merging fragments into one wall (small gaps between them are closed) and
/// cutting walls along a line. Merging works on a raster of the shapes (closing = dilate then erode), so it survives the
/// overlapping, touching or self-intersecting outlines a detection produces; the result is traced back to straight sides.
/// A cut is exact (the outline is clipped against the line) and only falls back to the raster when that would be ambiguous.
/// </summary>
public static class PolygonOps
{
    private const int MaxCells = 4_000_000;

    /// <summary>Union of the shapes; gaps narrower than <paramref name="closeMm"/> between fragments are filled. Returns one shape per connected piece.</summary>
    public static List<Shape> Merge(IReadOnlyList<Shape> shapes, int closeMm = 20)
    {
        if (shapes.Count == 0) return new();
        var (x0, y0, x1, y1) = Bounds(shapes);
        var (cell, w, h, ox, oy) = Grid(x0, y0, x1, y1, closeMm);
        var mask = new byte[w * h];
        foreach (var s in shapes) Fill(mask, w, h, ox, oy, cell, s);
        var r = (int)Math.Ceiling(closeMm / 2.0 / cell);
        if (r > 0) { mask = Dilate(mask, w, h, r); mask = Erode(mask, w, h, r); }
        return Trace(mask, w, h, ox, oy, cell);
    }

    /// <summary>Keeps the part of the shape on one side of the line through <paramref name="a"/> and <paramref name="b"/> (left when <paramref name="keepLeft"/>, looking from a to b). Empty when nothing is left; several shapes when the cut leaves separate pieces.</summary>
    public static List<Shape> KeepSide(Shape shape, PointMm a, PointMm b, bool keepLeft)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy); if (len < 1e-9 || shape.Outline.Count < 3) return [shape];
        double D(PointMm p) => (keepLeft ? 1 : -1) * (dx * (p.Y - a.Y) - dy * (p.X - a.X)) / len;           // > 0 on the side we keep
        if (shape.Outline.All(p => D(p) >= -0.5) && shape.Holes.All(hl => hl.All(p => D(p) >= -0.5))) return [shape];
        if (shape.Outline.All(p => D(p) <= 0.5)) return new();

        var outline = Clip(shape.Outline, D);
        if (outline.Count >= 3 && outline.Count(p => Math.Abs(D(p)) < 0.6) <= 2 && shape.Holes.All(hl => hl.All(p => D(p) >= 0.5) || hl.All(p => D(p) <= -0.5)))
        {   // one clean piece and no hole across the line: exact
            var holes = shape.Holes.Where(hl => hl.All(p => D(p) >= 0.5)).ToList();
            return [new Shape(outline, holes)];
        }
        return Rasterized(shape, D);
    }

    /// <summary>True when the segment ab touches the shape's outline or lies inside it.</summary>
    public static bool Crosses(Shape s, PointMm a, PointMm b)
    {
        var o = s.Outline; if (o.Count < 3) return false;
        for (var i = 0; i < o.Count; i++) if (SegmentsIntersect(o[i], o[(i + 1) % o.Count], a, b)) return true;
        return Contains(o, a) || Contains(o, b);
    }

    /// <summary>
    /// What is left of the shape once the zone is cut out of it. Exact (Greiner–Hormann on the outline, the zone nudged by a hair so no
    /// corner or side coincides with the shape's), so a wall that runs all the way round the plan only changes where the zone touches it.
    /// Falls back to the raster when a hole is crossed or the outline is too degenerate for the exact path.
    /// </summary>
    public static List<Shape> Subtract(Shape shape, IReadOnlyList<PointMm> zone)
    {
        var crossing = shape.Holes.Where(hl => RingsCross(hl, zone)).ToList();
        if (crossing.Count == 0) return SubtractRooms(shape, zone);
        // rooms the zone cuts into: first take the zone out of the bare outline, then each such room (the wall opens onto the zone), then put the other rooms back
        var pieces = SubtractRooms(new Shape(shape.Outline, new()), zone);
        foreach (var room in crossing) pieces = pieces.SelectMany(pc => SubtractRooms(pc, room)).ToList();
        foreach (var room in shape.Holes.Except(crossing).Where(r => !r.All(q => Contains(zone, q))))
            if (pieces.FirstOrDefault(pc => Contains(pc.Outline, room[0]) && !pc.Holes.Any(hl => Contains(hl, room[0]))) is { } owner) owner.Holes.Add(room);
        return pieces;
    }

    private static List<Shape> SubtractRooms(Shape shape, IReadOnlyList<PointMm> zone)
    {
        if (zone.Count < 3 || shape.Outline.Count < 3) return [shape];
        bool InZone(PointMm p) => Contains(zone, p);
        if (!Touches(shape, zone)) return [shape];
        var sa = Math.Abs(Area(shape.Outline));

        var res = Exact(shape.Outline, zone);
        var crossesHole = shape.Holes.Any(hl => RingsCross(hl, zone));
        if (res != null && !crossesHole)
        {
            if (res.Count == 1 && res[0].Count == 0) return new();                                   // the shape lies inside the zone
            if (shape.Holes.Any(hl => zone.All(q => Contains(hl, q)))) return [shape];                    // the zone lies in a room: nothing to remove
            var kept = shape.Holes.Where(hl => !hl.All(InZone)).ToList();                                // rooms under the zone vanish
            if (zone.All(q => Contains(shape.Outline, q)) && res.Count == 1 && ReferenceEquals(res[0], shape.Outline) && !kept.Any(hl => zone.Any(q => Contains(hl, q))))
                return [new Shape(shape.Outline, kept.Append(zone.ToList()).ToList())];              // the zone sits inside the shape: it becomes a hole
            var rings = res.Select(r => r.ToList()).ToList(); var shapes = Nest(rings);
            foreach (var sh in shapes) sh.Holes.AddRange(kept.Where(hl => Contains(sh.Outline, hl[0])));
            var left = shapes.Sum(x => Math.Abs(Area(x.Outline)) - x.Holes.Sum(hl => Math.Abs(Area(hl))));
            if (shapes.Count > 0 && left > 0 && left <= sa + 1) return shapes;
        }
        return Rasterized(shape, p => InZone(p) ? -1 : 1);
    }

    /// <summary>Outline rings of outline minus zone; one empty ring when nothing is left, the outline itself (same reference) when the zone does not cut it; null when the exact path gave up.</summary>
    private static List<List<PointMm>>? Exact(List<PointMm> outline, IReadOnlyList<PointMm> zone)
    {
        const double jx = 0.0137, jy = 0.0059;
        var subj = outline.Select(p => (X: (double)p.X, Y: (double)p.Y)).ToList(); var clip = zone.Select(p => (X: p.X + jx, Y: p.Y + jy)).ToList();
        var hits = new List<(int I, double A, int J, double B, double X, double Y)>();
        for (var i = 0; i < subj.Count; i++)
            for (var j = 0; j < clip.Count; j++)
            {
                var (p, q, r, t) = (subj[i], subj[(i + 1) % subj.Count], clip[j], clip[(j + 1) % clip.Count]);
                double d = (q.X - p.X) * (t.Y - r.Y) - (q.Y - p.Y) * (t.X - r.X); if (Math.Abs(d) < 1e-12) continue;
                double a = ((r.X - p.X) * (t.Y - r.Y) - (r.Y - p.Y) * (t.X - r.X)) / d, b = ((r.X - p.X) * (q.Y - p.Y) - (r.Y - p.Y) * (q.X - p.X)) / d;
                if (a > 0 && a < 1 && b > 0 && b < 1) hits.Add((i, a, j, b, p.X + a * (q.X - p.X), p.Y + a * (q.Y - p.Y)));
            }
        bool In(List<(double X, double Y)> poly, (double X, double Y) pt)
        {
            var inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].Y > pt.Y) != (poly[j].Y > pt.Y) && pt.X < (poly[j].X - poly[i].X) * (pt.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
            return inside;
        }
        if (hits.Count == 0)
        {
            if (In(clip, subj[0])) return [new List<PointMm>()];                                     // the outline is inside the zone
            return [outline];                                                                          // disjoint, or the zone is inside the outline (the caller tells which)
        }
        if (hits.Count % 2 != 0) return null;

        var sl = new List<GhVert>(); var cl = new List<GhVert>(); var pairs = new List<(GhVert S, GhVert C)>();
        foreach (var h in hits) { var sv = new GhVert(h.X, h.Y, true) { Alpha = h.A }; var cv = new GhVert(h.X, h.Y, true) { Alpha = h.B }; sv.Neighbor = cv; cv.Neighbor = sv; pairs.Add((sv, cv)); }
        for (var i = 0; i < subj.Count; i++) { sl.Add(new GhVert(subj[i].X, subj[i].Y, false)); sl.AddRange(pairs.Where((_, k) => hits[k].I == i).OrderBy(x => x.S.Alpha).Select(x => x.S)); }
        for (var j = 0; j < clip.Count; j++) { cl.Add(new GhVert(clip[j].X, clip[j].Y, false)); cl.AddRange(pairs.Where((_, k) => hits[k].J == j).OrderBy(x => x.C.Alpha).Select(x => x.C)); }
        foreach (var l in new[] { sl, cl }) for (var i = 0; i < l.Count; i++) { l[i].Next = l[(i + 1) % l.Count]; l[i].Prev = l[(i + l.Count - 1) % l.Count]; }

        // difference = (forwards = false for the outline, true for the zone), the starting direction flipped by whether the first vertex is inside the other polygon
        var sForward = false ^ In(clip, subj[0]); var cForward = true ^ In(subj, clip[0]);
        foreach (var v in sl.Where(v => v.Intersect)) { v.Entry = sForward; sForward = !sForward; }
        foreach (var v in cl.Where(v => v.Intersect)) { v.Entry = cForward; cForward = !cForward; }

        var rings = new List<List<PointMm>>(); var guard = 0;
        while (sl.FirstOrDefault(v => v.Intersect && !v.Processed) is { } start)
        {
            var ring = new List<PointMm>(); var v = start; ring.Add(Pt(v));
            do
            {
                v.Processed = true; v.Neighbor!.Processed = true;
                if (v.Entry) { do { v = v.Next!; ring.Add(Pt(v)); if (++guard > 200000) return null; } while (!v.Intersect); }
                else { do { v = v.Prev!; ring.Add(Pt(v)); if (++guard > 200000) return null; } while (!v.Intersect); }
                v = v.Neighbor!;
            } while (!v.Processed);
            ring = Dedup(ring); if (ring.Count >= 3) rings.Add(ring);
        }
        return rings.Count == 0 ? null : rings;
        static PointMm Pt(GhVert v) => new((int)Math.Round(v.X), (int)Math.Round(v.Y));
    }

    private sealed class GhVert(double x, double y, bool intersect)
    {
        public double X = x, Y = y, Alpha; public bool Intersect = intersect, Entry, Processed; public GhVert? Next, Prev, Neighbor;
    }

    /// <summary>Groups rings into outlines and holes by nesting depth.</summary>
    private static List<Shape> Nest(List<List<PointMm>> rings)
    {
        var depth = rings.Select((r, i) => rings.Where((o, k) => k != i && Contains(o, r[0])).Count()).ToList(); var outs = new List<Shape>(); var map = new Dictionary<int, Shape>();
        for (var i = 0; i < rings.Count; i++) if (depth[i] % 2 == 0) { var sh = new Shape(rings[i], new()); outs.Add(sh); map[i] = sh; }
        for (var i = 0; i < rings.Count; i++)
            if (depth[i] % 2 == 1) { var owner = map.Where(k => Contains(rings[k.Key], rings[i][0])).OrderBy(k => Math.Abs(Area(rings[k.Key]))).FirstOrDefault(); if (owner.Value != null) owner.Value.Holes.Add(rings[i]); }
        return outs;
    }

    private static bool RingsCross(IReadOnlyList<PointMm> a, IReadOnlyList<PointMm> b)
    {
        for (var i = 0; i < a.Count; i++) for (var j = 0; j < b.Count; j++) if (SegmentsIntersect(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
        return false;
    }

    private static double Area(IReadOnlyList<PointMm> r) { double a = 0; for (var i = 0; i < r.Count; i++) { var (p, q) = (r[i], r[(i + 1) % r.Count]); a += (double)p.X * q.Y - (double)q.X * p.Y; } return a / 2; }

    /// <summary>True when the shape reaches into the region: one of its corners is inside it, or the region's outline touches the shape.</summary>
    public static bool Touches(Shape s, IReadOnlyList<PointMm> region)
    {
        if (region.Count < 3 || s.Outline.Count < 3) return false;
        if (s.Outline.Any(p => Contains(region, p))) return true;
        for (var i = 0; i < region.Count; i++) if (Crosses(s, region[i], region[(i + 1) % region.Count])) return true;
        return false;
    }

    public static bool Contains(IReadOnlyList<PointMm> poly, PointMm p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (double)(poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    // ---- exact clipping (Sutherland–Hodgman against one half-plane) ----
    private static List<PointMm> Clip(IReadOnlyList<PointMm> poly, Func<PointMm, double> d)
    {
        var res = new List<PointMm>();
        for (var i = 0; i < poly.Count; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % poly.Count]; double dp = d(p), dq = d(q);
            if (dp >= 0) res.Add(p);
            if ((dp >= 0) != (dq >= 0)) { var t = dp / (dp - dq); res.Add(new PointMm((int)Math.Round(p.X + (q.X - p.X) * t), (int)Math.Round(p.Y + (q.Y - p.Y) * t))); }
        }
        return Dedup(res);
    }
    private static List<PointMm> Dedup(List<PointMm> pts)
    {
        var r = new List<PointMm>(); foreach (var p in pts) if (r.Count == 0 || r[^1] != p) r.Add(p);
        if (r.Count > 1 && r[0] == r[^1]) r.RemoveAt(r.Count - 1); return r;
    }

    /// <summary>Segments intersect, touching and overlapping included.</summary>
    private static bool SegmentsIntersect(PointMm p1, PointMm p2, PointMm p3, PointMm p4)
    {
        static long Cr(PointMm o, PointMm a, PointMm b) => (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);
        static bool On(PointMm a, PointMm b, PointMm p) => Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);
        long d1 = Cr(p3, p4, p1), d2 = Cr(p3, p4, p2), d3 = Cr(p1, p2, p3), d4 = Cr(p1, p2, p4);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        return (d1 == 0 && On(p3, p4, p1)) || (d2 == 0 && On(p3, p4, p2)) || (d3 == 0 && On(p1, p2, p3)) || (d4 == 0 && On(p1, p2, p4));
    }

    // ---- raster path ----
    private static (int X0, int Y0, int X1, int Y1) Bounds(IEnumerable<Shape> shapes)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        foreach (var p in shapes.SelectMany(s => s.Outline)) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return (x0, y0, x1, y1);
    }

    private static (int Cell, int W, int H, int Ox, int Oy) Grid(int x0, int y0, int x1, int y1, int closeMm)
    {
        var cell = 2; double ext = Math.Max(1, (long)(x1 - x0 + 2 * (closeMm + 20))) * Math.Max(1, (long)(y1 - y0 + 2 * (closeMm + 20)));
        while (ext / ((double)cell * cell) > MaxCells) cell *= 2;
        var pad = (int)Math.Ceiling(closeMm / 2.0 / cell) + 3;
        var ox = x0 - pad * cell; var oy = y0 - pad * cell;
        return (cell, (x1 - x0) / cell + 2 * pad + 2, (y1 - y0) / cell + 2 * pad + 2, ox, oy);
    }

    /// <summary>Even-odd scanline fill of one shape (outline and holes together) at the cell centres.</summary>
    private static void Fill(byte[] mask, int w, int h, int ox, int oy, int cell, Shape s)
    {
        var rings = new[] { s.Outline }.Concat(s.Holes).Where(r => r.Count >= 3).ToList(); if (rings.Count == 0) return;
        var xs = new List<double>();
        for (var row = 0; row < h; row++)
        {
            double y = oy + (row + 0.5) * cell; xs.Clear();
            foreach (var ring in rings)
                for (var i = 0; i < ring.Count; i++)
                {
                    var p = ring[i]; var q = ring[(i + 1) % ring.Count];
                    if ((p.Y > y) == (q.Y > y)) continue;
                    xs.Add(p.X + (y - p.Y) * (q.X - p.X) / (q.Y - p.Y));
                }
            xs.Sort();
            for (var k = 0; k + 1 < xs.Count; k += 2)
            {
                int c0 = (int)Math.Ceiling((xs[k] - ox) / cell - 0.5), c1 = (int)Math.Floor((xs[k + 1] - ox) / cell - 0.5);
                for (var c = Math.Max(0, c0); c <= Math.Min(w - 1, c1); c++) mask[row * w + c] = 1;
            }
        }
    }

    private static byte[] Dilate(byte[] m, int w, int h, int r) => Box(Box(m, w, h, r, true, true), w, h, r, false, true);
    private static byte[] Erode(byte[] m, int w, int h, int r) => Box(Box(m, w, h, r, true, false), w, h, r, false, false);

    /// <summary>Square structuring element in two 1D passes (running count over a window of 2r+1 cells).</summary>
    private static byte[] Box(byte[] src, int w, int h, int r, bool horizontal, bool dilate)
    {
        var dst = new byte[src.Length]; int n = horizontal ? w : h, lines = horizontal ? h : w, win = 2 * r + 1;
        for (var line = 0; line < lines; line++)
        {
            int At(int i) => horizontal ? line * w + i : i * w + line;
            var sum = 0; for (var i = 0; i <= Math.Min(r, n - 1); i++) sum += src[At(i)];
            for (var i = 0; i < n; i++)
            {
                dst[At(i)] = (byte)(dilate ? (sum > 0 ? 1 : 0) : (sum == win ? 1 : 0));   // erosion: the whole window (outside the grid counts as empty) must be set
                var add = i + r + 1; if (add < n) sum += src[At(add)];
                var drop = i - r; if (drop >= 0) sum -= src[At(drop)];
            }
        }
        return dst;
    }

    private static List<Shape> Trace(byte[] mask, int w, int h, int ox, int oy, int cell)
    {
        var res = new List<Shape>();
        foreach (var o in MaskTracer.Trace(mask, w, h, new TraceOptions(Tolerance: 1, SnapDegrees: 4, MinArea: 16)))
        {
            PointMm P(PointPx p) => new((int)Math.Round(ox + p.X * cell), (int)Math.Round(oy + p.Y * cell));
            res.Add(new Shape(o.Outline.Select(P).ToList(), o.Holes.Select(hl => hl.Select(P).ToList()).ToList()));
        }
        return res;
    }

    /// <summary>Fallback for cuts that leave several pieces or cross a hole: draw the shape, erase the removed side, trace what is left.</summary>
    private static List<Shape> Rasterized(Shape shape, Func<PointMm, double> d)
    {
        var (x0, y0, x1, y1) = Bounds([shape]); var (cell, w, h, ox, oy) = Grid(x0, y0, x1, y1, 0);
        var mask = new byte[w * h]; Fill(mask, w, h, ox, oy, cell, shape);
        for (var row = 0; row < h; row++) for (var c = 0; c < w; c++)
            if (mask[row * w + c] == 1 && d(new PointMm(ox + c * cell + cell / 2, oy + row * cell + cell / 2)) < 0) mask[row * w + c] = 0;
        return Trace(mask, w, h, ox, oy, cell);
    }
}
