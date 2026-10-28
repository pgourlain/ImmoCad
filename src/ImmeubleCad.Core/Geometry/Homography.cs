namespace ImmeubleCad.Core.Geometry;

/// <summary>Perspective transform from 4 point pairs (phone photos taken at an angle).</summary>
public readonly record struct Homography(double[] H)
{
    public (double X, double Y) Apply(double x, double y)
    {
        var w = H[6] * x + H[7] * y + 1;
        return ((H[0] * x + H[1] * y + H[2]) / w, (H[3] * x + H[4] * y + H[5]) / w);
    }

    public static Homography? Fit(IReadOnlyList<((double X, double Y) From, (double X, double Y) To)> p)
    {
        if (p.Count != 4) return null;
        var m = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = p[i].From; var (u, v) = p[i].To;
            double[] r1 = [x, y, 1, 0, 0, 0, -u * x, -u * y, u], r2 = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
            for (var j = 0; j < 9; j++) { m[2 * i, j] = r1[j]; m[2 * i + 1, j] = r2[j]; }
        }
        for (var c = 0; c < 8; c++)
        {
            var piv = c; for (var i = c + 1; i < 8; i++) if (Math.Abs(m[i, c]) > Math.Abs(m[piv, c])) piv = i;
            if (Math.Abs(m[piv, c]) < 1e-9) return null; // degenerate (collinear points)
            for (var j = 0; j < 9; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            for (var i = 0; i < 8; i++) if (i != c) { var f = m[i, c] / m[c, c]; for (var j = c; j < 9; j++) m[i, j] -= f * m[c, j]; }
        }
        return new Homography(Enumerable.Range(0, 8).Select(i => m[i, 8] / m[i, i]).ToArray());
    }
}
