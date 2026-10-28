namespace ImmeubleCad.Core.Geometry;

/// <summary>Affine 2x3 matrix [a b tx; c d ty] mapping (x,y) -> (a x + b y + tx, c x + d y + ty).</summary>
public readonly record struct Affine(double A, double B, double Tx, double C, double D, double Ty)
{
    public static Affine Identity => new(1, 0, 0, 0, 1, 0);
    public (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);
    public double[] ToArray() => [A, B, Tx, C, D, Ty];
}

public static class ImageFit
{
    /// <summary>Least-squares fit from point pairs. 2 pairs: similarity (translation+rotation+uniform scale). 3+: full affine.</summary>
    public static Affine? Fit(IReadOnlyList<((double X, double Y) From, (double X, double Y) To)> pairs)
    {
        if (pairs.Count < 2) return null;
        if (pairs.Count == 2) return Similarity(pairs[0], pairs[1]);
        // solve normal equations for [a b tx] and [c d ty] independently
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, n = pairs.Count;
        double bx_x = 0, bx_y = 0, bx = 0, by_x = 0, by_y = 0, by = 0;
        foreach (var (f, t) in pairs)
        {
            sxx += f.X * f.X; sxy += f.X * f.Y; sx += f.X; syy += f.Y * f.Y; sy += f.Y;
            bx_x += f.X * t.X; bx_y += f.Y * t.X; bx += t.X;
            by_x += f.X * t.Y; by_y += f.Y * t.Y; by += t.Y;
        }
        double[,] m = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        var u = Solve3(m, [bx_x, bx_y, bx]); var v = Solve3(m, [by_x, by_y, by]);
        if (u == null || v == null) return null; // degenerate (collinear)
        return new(u[0], u[1], u[2], v[0], v[1], v[2]);
    }

    public static double[] Residuals(Affine t, IReadOnlyList<((double X, double Y) From, (double X, double Y) To)> pairs) =>
        pairs.Select(p => { var (x, y) = t.Apply(p.From.X, p.From.Y); return Math.Sqrt((x - p.To.X) * (x - p.To.X) + (y - p.To.Y) * (y - p.To.Y)); }).ToArray();

    private static Affine? Similarity(((double X, double Y) From, (double X, double Y) To) p, ((double X, double Y) From, (double X, double Y) To) q)
    {
        double fx = q.From.X - p.From.X, fy = q.From.Y - p.From.Y, tx = q.To.X - p.To.X, ty = q.To.Y - p.To.Y;
        var den = fx * fx + fy * fy; if (den < 1e-12) return null;
        var a = (fx * tx + fy * ty) / den; var b = (fx * ty - fy * tx) / den; // rotation*scale = a + ib
        var t = (p.To.X - (a * p.From.X - b * p.From.Y), p.To.Y - (b * p.From.X + a * p.From.Y));
        return new(a, -b, t.Item1, b, a, t.Item2);
    }

    private static double[]? Solve3(double[,] m, double[] r)
    {
        var a = new double[3, 4];
        for (var i = 0; i < 3; i++) { for (var j = 0; j < 3; j++) a[i, j] = m[i, j]; a[i, 3] = r[i]; }
        for (var c = 0; c < 3; c++)
        {
            var piv = c; for (var i = c + 1; i < 3; i++) if (Math.Abs(a[i, c]) > Math.Abs(a[piv, c])) piv = i;
            if (Math.Abs(a[piv, c]) < 1e-9) return null;
            for (var j = 0; j < 4; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
            for (var i = 0; i < 3; i++) if (i != c) { var f = a[i, c] / a[c, c]; for (var j = c; j < 4; j++) a[i, j] -= f * a[c, j]; }
        }
        return [a[0, 3] / a[0, 0], a[1, 3] / a[1, 1], a[2, 3] / a[2, 2]];
    }
}
