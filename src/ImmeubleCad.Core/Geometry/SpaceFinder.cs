using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.Geometry;

public static class SpaceFinder
{
    /// <summary>Flood fill on a raster of the level's walls from a click point; returns the bounding outline of the enclosed area (mm), or null if open to outside.</summary>
    public static List<PointMm>? Find(Level level, PointMm click, int cellMm = 50)
    {
        var pts = level.Walls.SelectMany(w => w.Footprint).ToList();
        if (pts.Count == 0) return null;
        int x0 = pts.Min(p => p.X) - cellMm, y0 = pts.Min(p => p.Y) - cellMm;
        var w = (pts.Max(p => p.X) - x0) / cellMm + 2; var h = (pts.Max(p => p.Y) - y0) / cellMm + 2;
        var wall = new bool[w * h];
        for (var cy = 0; cy < h; cy++)
            for (var cx = 0; cx < w; cx++)
            {
                var p = new PointMm(x0 + cx * cellMm + cellMm / 2, y0 + cy * cellMm + cellMm / 2);
                wall[cy * w + cx] = level.Walls.Any(wl => Contains(wl.Footprint, p));
            }
        var sx = (click.X - x0) / cellMm; var sy = (click.Y - y0) / cellMm;
        if (sx < 0 || sy < 0 || sx >= w || sy >= h || wall[sy * w + sx]) return null;
        var seen = new bool[w * h]; var q = new Queue<int>(); q.Enqueue(sy * w + sx); seen[sy * w + sx] = true;
        int minX = sx, maxX = sx, minY = sy, maxY = sy;
        while (q.Count > 0)
        {
            var i = q.Dequeue(); var x = i % w; var y = i / w;
            if (x == 0 || y == 0 || x == w - 1 || y == h - 1) return null; // leaks to outside
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            { var j = (y + dy) * w + x + dx; if (!wall[j] && !seen[j]) { seen[j] = true; q.Enqueue(j); } }
        }
        int X(int c) => x0 + c * cellMm;
        int Y(int c) => y0 + c * cellMm;
        return [new(X(minX), Y(minY)), new(X(maxX + 1), Y(minY)), new(X(maxX + 1), Y(maxY + 1)), new(X(minX), Y(maxY + 1))];
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
