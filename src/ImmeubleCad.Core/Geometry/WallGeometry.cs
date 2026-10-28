using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.Geometry;

/// <summary>Centre line and thickness of a wall footprint (exact for rectangles, best effort otherwise: the longest edge and the distance to the opposite one).</summary>
public readonly record struct WallAxis(PointMm A, PointMm B, double Length, int ThicknessMm)
{
    public PointMm PointAt(double alongMm)
    {
        var t = Length < 1 ? 0 : alongMm / Length;
        return new((int)Math.Round(A.X + (B.X - A.X) * t), (int)Math.Round(A.Y + (B.Y - A.Y) * t));
    }
}

public static class WallGeometry
{
    public static WallAxis? Axis(Wall w)
    {
        var f = w.Footprint; if (f.Count < 3) return null;
        var best = 0; double bestLen = -1;
        for (var i = 0; i < f.Count; i++) { var l = Dist(f[i], f[(i + 1) % f.Count]); if (l > bestLen) { bestLen = l; best = i; } }
        var a = f[best]; var b = f[(best + 1) % f.Count]; double dx = b.X - a.X, dy = b.Y - a.Y; var len = Math.Sqrt(dx * dx + dy * dy); if (len < 1) return null;
        double nx = -dy / len, ny = dx / len;
        // thickness = extent of the footprint across the long edge
        var across = f.Select(p => (p.X - a.X) * nx + (p.Y - a.Y) * ny).ToList(); var min = across.Min(); var max = across.Max();
        // centre line: shift the long edge to the middle of the footprint
        var mid = (min + max) / 2; PointMm Shift(PointMm p) => new((int)Math.Round(p.X + nx * mid), (int)Math.Round(p.Y + ny * mid));
        // extent along the edge (a rectangle's two long edges coincide in extent)
        var along = f.Select(p => ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len).ToList(); var s0 = along.Min(); var s1 = along.Max();
        PointMm At(double s) => new((int)Math.Round(a.X + dx / len * s + nx * mid), (int)Math.Round(a.Y + dy / len * s + ny * mid));
        return new WallAxis(At(s0), At(s1), s1 - s0, (int)Math.Round(max - min));
    }

    /// <summary>Position (mm from the axis start) of the point projected on the wall's centre line, or null when it is further than <paramref name="reachMm"/> from the wall.</summary>
    public static double? PositionAlong(Wall w, PointMm p, int reachMm = 400)
    {
        if (Axis(w) is not { } ax) return null;
        double dx = ax.B.X - ax.A.X, dy = ax.B.Y - ax.A.Y; var len = ax.Length; if (len < 1) return null;
        var t = ((p.X - ax.A.X) * dx + (p.Y - ax.A.Y) * dy) / len; var off = Math.Abs((p.X - ax.A.X) * -dy + (p.Y - ax.A.Y) * dx) / len;
        return off <= reachMm + ax.ThicknessMm / 2.0 && t >= 0 && t <= len ? t : null;
    }

    public static IEnumerable<PointMm> Rectangle(PointMm a, PointMm b, int thicknessMm)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y; var len = Math.Sqrt(dx * dx + dy * dy); var h = thicknessMm / 2.0;
        int nx = (int)Math.Round(-dy / len * h), ny = (int)Math.Round(dx / len * h);
        return [new(a.X + nx, a.Y + ny), new(b.X + nx, b.Y + ny), new(b.X - nx, b.Y - ny), new(a.X - nx, a.Y - ny)];
    }

    private static double Dist(PointMm a, PointMm b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
