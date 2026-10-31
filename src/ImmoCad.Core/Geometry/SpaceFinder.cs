using ImmoCad.Core.Model;

namespace ImmoCad.Core.Geometry;

/// <summary>What the room tool finds under a point: the room outline (null when it stays open), the openings it had to bridge
/// (doors, scan gaps: one centre per opening) and the widest opening bridged.</summary>
public sealed record SpaceProbe(List<PointMm>? Outline, List<SpaceLeak> Leaks, int GapMm);

/// <summary>One closed opening: its centre and a radius covering it.</summary>
public sealed record SpaceLeak(PointMm At, int RadiusMm);

/// <summary>The level's walls rasterised once (wall cells and the distance of every cell to the nearest wall), reused by every probe.</summary>
public sealed class SpaceGrid
{
    public int CellMm { get; }
    internal readonly int X0, Y0, W, H;
    internal readonly ushort[] Dist;   // 0 on a wall, else Chebyshev distance in cells (capped)
    internal const ushort Far = ushort.MaxValue;

    public SpaceGrid(IReadOnlyList<Wall> walls, int cellMm = 50, int marginMm = 1500)
    {
        CellMm = cellMm;
        var pts = walls.SelectMany(w => w.Footprint).ToList();
        if (pts.Count == 0) { W = H = 0; Dist = []; return; }
        var m = marginMm / cellMm + 2;
        X0 = pts.Min(p => p.X) - m * cellMm; Y0 = pts.Min(p => p.Y) - m * cellMm;
        W = (pts.Max(p => p.X) - X0) / cellMm + m + 1; H = (pts.Max(p => p.Y) - Y0) / cellMm + m + 1;
        Dist = new ushort[W * H]; Array.Fill(Dist, Far);
        var q = new Queue<int>();
        foreach (var wl in walls) Fill(wl, q);
        while (q.Count > 0)
        {   // multi-source BFS, 8-connected: Chebyshev distance to the nearest wall cell
            var i = q.Dequeue(); int x = i % W, y = i / W; var d = (ushort)(Dist[i] + 1);
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy; if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    var j = ny * W + nx; if (Dist[j] > d) { Dist[j] = d; q.Enqueue(j); }
                }
        }
    }

    /// <summary>Scanline fill of one wall (footprint and its holes, even-odd) at the cell centres.</summary>
    private void Fill(Wall wall, Queue<int> q)
    {
        var rings = new List<IReadOnlyList<PointMm>> { wall.Footprint }; rings.AddRange(wall.Holes);
        if (wall.Footprint.Count < 3) return;
        int minY = wall.Footprint.Min(p => p.Y), maxY = wall.Footprint.Max(p => p.Y);
        var xs = new List<double>();
        for (int cy = Math.Max(0, (minY - Y0) / CellMm); cy <= Math.Min(H - 1, (maxY - Y0) / CellMm); cy++)
        {
            var y = Y0 + cy * CellMm + CellMm / 2.0; xs.Clear();
            foreach (var r in rings)
                for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
                    if ((r[i].Y > y) != (r[j].Y > y)) xs.Add(r[i].X + (y - r[i].Y) * (r[j].X - r[i].X) / (double)(r[j].Y - r[i].Y));
            xs.Sort();
            for (var k = 0; k + 1 < xs.Count; k += 2)
            {   // cells whose centre lies in [xs[k], xs[k+1]]
                var a = (int)Math.Ceiling((xs[k] - X0 - CellMm / 2.0) / CellMm); var b = (int)Math.Floor((xs[k + 1] - X0 - CellMm / 2.0) / CellMm);
                for (var cx = Math.Max(0, a); cx <= Math.Min(W - 1, b); cx++) { var i = cy * W + cx; if (Dist[i] != 0) { Dist[i] = 0; q.Enqueue(i); } }
            }
        }
    }

    internal bool Inside(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
}

public static class SpaceFinder
{
    /// <summary>Room outline under a click (mm), or null when it is open to the outside; small openings are not bridged.</summary>
    public static List<PointMm>? Find(Level level, PointMm click, int cellMm = 50) => Probe(new SpaceGrid(level.Walls, cellMm), click, 0).Outline;

    /// <summary>Flood fill from <paramref name="at"/> with openings up to <paramref name="maxGapMm"/> wide closed (walls thickened by half
    /// the gap, the room grown back by as much afterwards), so a door does not merge two rooms; the closed openings are reported as leaks.
    /// The widest closing that still leaves the click in a closed area wins, narrower ones are tried in a corridor or a closet.</summary>
    public static SpaceProbe Probe(SpaceGrid g, PointMm at, int maxGapMm = 1000, CancellationToken ct = default)
    {
        if (g.W == 0) return new(null, [], 0);
        int sx = (at.X - g.X0) / g.CellMm, sy = (at.Y - g.Y0) / g.CellMm;
        if (!g.Inside(sx, sy) || g.Dist[sy * g.W + sx] == 0) return new(null, [], 0);
        var maxR = maxGapMm / g.CellMm / 2;
        for (var r = maxR; r >= 0; r--)
        {
            ct.ThrowIfCancellationRequested();
            var seed = sy * g.W + sx; if (g.Dist[seed] <= r) continue;   // the click is too close to a wall for openings that wide
            var region = Flood(g, seed, r); if (region == null) continue;
            if (r > 0) Grow(g, region, r);
            var leaks = r > 0 ? Leaks(g, region) : [];
            return new(Outline(g, region), leaks, 2 * r * g.CellMm);
        }
        return new(null, [], 0);
    }

    /// <summary>
    /// <see cref="Probe"/> with a second chance: when no room is found closing openings up to <paramref name="firstMm"/> (1 m: doors, windows), openings up to
    /// <paramref name="lastMm"/> (2 m: double doors, a wide bay) are closed instead. Only tried when the first fails, so a room that closes with ordinary doors is never grown.
    /// </summary>
    public static SpaceProbe ProbeWide(SpaceGrid g, PointMm at, CancellationToken ct = default, int firstMm = 1000, int lastMm = 2000)
    {
        var narrow = Probe(g, at, firstMm, ct); if (narrow.Outline != null) return narrow;
        var wide = Probe(g, at, lastMm, ct); return wide.Outline != null ? wide : narrow;
    }

    /// <summary>4-connected fill through cells farther than r from any wall; null when it reaches the border (open).</summary>
    private static bool[]? Flood(SpaceGrid g, int seed, int r)
    {
        var seen = new bool[g.W * g.H]; var q = new Queue<int>(); q.Enqueue(seed); seen[seed] = true;
        while (q.Count > 0)
        {
            var i = q.Dequeue(); int x = i % g.W, y = i / g.W;
            if (x == 0 || y == 0 || x == g.W - 1 || y == g.H - 1) return null;
            foreach (var j in new[] { i + 1, i - 1, i + g.W, i - g.W }) if (!seen[j] && g.Dist[j] > r) { seen[j] = true; q.Enqueue(j); }
        }
        return seen;
    }

    /// <summary>Grows the region back by r cells through free cells (it stops at the line of a bridged opening).</summary>
    private static void Grow(SpaceGrid g, bool[] region, int r)
    {
        var front = new List<int>(); for (var i = 0; i < region.Length; i++) if (region[i]) front.Add(i);
        for (var step = 0; step < r && front.Count > 0; step++)
        {
            var next = new List<int>();
            foreach (var i in front)
            {
                int x = i % g.W, y = i / g.W;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy; if (!g.Inside(nx, ny)) continue;
                        var j = ny * g.W + nx; if (!region[j] && g.Dist[j] > 0) { region[j] = true; next.Add(j); }
                    }
            }
            front = next;
        }
    }

    /// <summary>Free cells touching the room that are not part of it, grouped: a group is a bridged opening when what lies behind it is
    /// big (another room, the outside); a small dead end (a notch in a scanned wall, a narrow niche) is not a leak.</summary>
    private static List<SpaceLeak> Leaks(SpaceGrid g, bool[] region, int behindMm2 = 1_000_000)
    {
        var leak = new bool[region.Length]; var cells = new List<int>();
        for (var i = 0; i < region.Length; i++)
        {
            if (region[i] || g.Dist[i] == 0) continue;
            int x = i % g.W, y = i / g.W;
            if (x > 0 && region[i - 1] || x < g.W - 1 && region[i + 1] || y > 0 && region[i - g.W] || y < g.H - 1 && region[i + g.W]) { leak[i] = true; cells.Add(i); }
        }
        var res = new List<SpaceLeak>(); var done = new bool[region.Length]; var behindCells = behindMm2 / (g.CellMm * g.CellMm);
        foreach (var c in cells)
        {
            if (done[c]) continue;
            long sxm = 0, sym = 0; var n = 0; var q = new Queue<int>(); q.Enqueue(c); done[c] = true; var group = new List<int>();
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            while (q.Count > 0)
            {
                var i = q.Dequeue(); int x = i % g.W, y = i / g.W; sxm += x; sym += y; n++; group.Add(i);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                    { int nx = x + dx, ny = y + dy; if (!g.Inside(nx, ny)) continue; var j = ny * g.W + nx; if (leak[j] && !done[j]) { done[j] = true; q.Enqueue(j); } }
            }
            if (!OpensOnto(g, region, group, behindCells)) continue;
            var at = new PointMm((int)(g.X0 + (sxm / (double)n + 0.5) * g.CellMm), (int)(g.Y0 + (sym / (double)n + 0.5) * g.CellMm));
            res.Add(new(at, Math.Max(150, (Math.Max(maxX - minX, maxY - minY) + 1) * g.CellMm / 2)));
        }
        return res;
    }

    /// <summary>True when the free space behind these cells (outside the room) reaches the border or holds more than <paramref name="limit"/> cells.</summary>
    private static bool OpensOnto(SpaceGrid g, bool[] region, List<int> from, int limit)
    {
        var seen = new HashSet<int>(from); var q = new Queue<int>(from);
        while (q.Count > 0)
        {
            var i = q.Dequeue(); int x = i % g.W, y = i / g.W;
            if (x == 0 || y == 0 || x == g.W - 1 || y == g.H - 1 || seen.Count > limit) return true;
            foreach (var j in new[] { i + 1, i - 1, i + g.W, i - g.W }) if (!region[j] && g.Dist[j] > 0 && seen.Add(j)) q.Enqueue(j);
        }
        return false;
    }

    /// <summary>Outer boundary of the region along cell edges, staircases simplified to straight (or slanted) sides.</summary>
    private static List<PointMm>? Outline(SpaceGrid g, bool[] region)
    {
        bool In(int x, int y) => g.Inside(x, y) && region[y * g.W + x];
        // directed boundary edges with the region on the left, keyed by their start vertex (vertex id = y*(W+1)+x)
        var next = new Dictionary<int, List<int>>(); var vw = g.W + 1;
        void Add(int ax, int ay, int bx, int by) { var k = ay * vw + ax; if (!next.TryGetValue(k, out var l)) next[k] = l = new(2); l.Add(by * vw + bx); }
        for (var y = 0; y < g.H; y++)
            for (var x = 0; x < g.W; x++)
            {
                if (!region[y * g.W + x]) continue;
                if (!In(x, y - 1)) Add(x, y, x + 1, y);
                if (!In(x + 1, y)) Add(x + 1, y, x + 1, y + 1);
                if (!In(x, y + 1)) Add(x + 1, y + 1, x, y + 1);
                if (!In(x - 1, y)) Add(x, y + 1, x, y);
            }
        List<(int X, int Y)>? best = null; long bestArea = 0;
        while (next.Count > 0)
        {
            var start = next.Keys.First(); var loop = new List<(int, int)>(); var v = start; var prev = -1;
            while (next.TryGetValue(v, out var outs))
            {
                // at a pinch (two cells touching by a corner) take the left-most turn so loops stay simple
                var pick = outs[0];
                if (outs.Count > 1 && prev >= 0)
                {
                    int dx0 = v % vw - prev % vw, dy0 = v / vw - prev / vw;
                    pick = outs.OrderByDescending(o => dx0 * (o / vw - v / vw) - dy0 * (o % vw - v % vw)).First();
                }
                outs.Remove(pick); if (outs.Count == 0) next.Remove(v);
                loop.Add((v % vw, v / vw)); prev = v; v = pick;
                if (v == start) break;
            }
            long a = 0; for (var i = 0; i < loop.Count; i++) { var p = loop[i]; var q = loop[(i + 1) % loop.Count]; a += (long)p.Item1 * q.Item2 - (long)q.Item1 * p.Item2; }
            if (a > bestArea) { bestArea = a; best = loop; }
        }
        if (best == null) return null;
        var mm = best.Select(p => new PointMm(g.X0 + p.X * g.CellMm, g.Y0 + p.Y * g.CellMm)).ToList();
        return Simplify(mm, g.CellMm * 0.75);
    }

    /// <summary>Douglas–Peucker on a closed ring, split at its two farthest-apart vertices.</summary>
    private static List<PointMm> Simplify(List<PointMm> ring, double tol)
    {
        if (ring.Count < 4) return ring;
        var a = 0; var b = 0; long far = -1;
        for (var i = 0; i < ring.Count; i++) { long d = (long)(ring[i].X - ring[0].X) * (ring[i].X - ring[0].X) + (long)(ring[i].Y - ring[0].Y) * (ring[i].Y - ring[0].Y); if (d > far) { far = d; b = i; } }
        var keep = new bool[ring.Count]; keep[a] = keep[b] = true;
        void Dp(int i, int j)   // j may wrap past the end
        {
            var n = ring.Count; if ((j - i + n) % n < 2) return;
            var p = ring[i % n]; var q = ring[j % n]; double dx = q.X - p.X, dy = q.Y - p.Y, len = Math.Sqrt(dx * dx + dy * dy);
            int idx = -1; double max = tol;
            for (var k = i + 1; k < (j < i ? j + n : j); k++)
            {
                var c = ring[k % n];
                var d = len < 1e-9 ? Math.Sqrt((c.X - p.X) * (double)(c.X - p.X) + (c.Y - p.Y) * (double)(c.Y - p.Y)) : Math.Abs(dx * (c.Y - p.Y) - dy * (c.X - p.X)) / len;
                if (d > max) { max = d; idx = k; }
            }
            if (idx < 0) return;
            keep[idx % n] = true; Dp(i, idx); Dp(idx, j < i ? j + n : j);
        }
        Dp(a, b); Dp(b, a + ring.Count);
        return ring.Where((_, i) => keep[i]).ToList();
    }

    public static bool Contains(IReadOnlyList<PointMm> poly, PointMm p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (long)(poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (double)(poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    public static long AreaMm2(IReadOnlyList<PointMm> poly)
    {
        long a = 0; for (var i = 0; i < poly.Count; i++) { var p = poly[i]; var q = poly[(i + 1) % poly.Count]; a += (long)p.X * q.Y - (long)q.X * p.Y; }
        return Math.Abs(a) / 2;
    }
}
