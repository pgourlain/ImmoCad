namespace ImmoCad.Plan;

public static class Deskew
{
    /// <summary>Tilt of the drawing in degrees (rotate the picture by the opposite angle to straighten it): dominant lines are made horizontal/vertical. Projection-profile search in ±maxDeg.</summary>
    public static double EstimateAngle(GrayImage img, int threshold = 0, double maxDeg = 10, double stepDeg = 0.1)
    {
        var level = threshold > 0 ? threshold : LineExtraction.OtsuThreshold(img.Pixels);
        // sample dark pixels (cap the count for speed)
        var pts = new List<(int X, int Y)>(); var stride = Math.Max(1, img.Pixels.Length / 200_000);
        for (var i = 0; i < img.Pixels.Length; i += stride) if (img.Pixels[i] < level) pts.Add((i % img.Width, i / img.Width));
        if (pts.Count < 50) return 0;
        double best = 0, bestScore = -1;
        var diag = (int)Math.Ceiling(Math.Sqrt(img.Width * img.Width + img.Height * img.Height)) + 2;
        for (var a = -maxDeg; a <= maxDeg + 1e-9; a += stepDeg)
        {
            var rad = a * Math.PI / 180; double sn = Math.Sin(rad), cs = Math.Cos(rad);
            var hRows = new int[2 * diag]; var vCols = new int[2 * diag];
            foreach (var (x, y) in pts)
            {
                hRows[(int)Math.Round(-x * sn + y * cs) + diag]++;   // row profile after rotating by a
                vCols[(int)Math.Round(x * cs + y * sn) + diag]++;     // column profile
            }
            var score = SumSquares(hRows) + SumSquares(vCols);       // sharp peaks = aligned lines
            if (score > bestScore) { bestScore = score; best = a; }
        }
        return Math.Round(best, 1);
    }
    private static double SumSquares(int[] h) { double s = 0; foreach (var v in h) s += (double)v * v; return s; }
}
