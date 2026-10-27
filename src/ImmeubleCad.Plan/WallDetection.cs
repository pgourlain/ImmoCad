namespace ImmeubleCad.Plan;

public readonly record struct PointPx(double X, double Y);
public sealed record GrayImage(byte[] Pixels, int Width, int Height);
public sealed record WallDetectionOptions(byte Threshold = 100, int MinArea = 20);

public static class LineExtraction
{
    /// <summary>Dark pixels (below threshold) = wall mask.</summary>
    public static bool[] Mask(GrayImage img, byte threshold)
    {
        var m = new bool[img.Pixels.Length];
        for (var i = 0; i < m.Length; i++) m[i] = img.Pixels[i] < threshold;
        return m;
    }

    /// <summary>Median run length of dark pixels along rows: estimate of wall thickness in px.</summary>
    public static int SuggestThickness(GrayImage img, byte threshold)
    {
        var runs = new List<int>();
        for (var y = 0; y < img.Height; y++)
        {
            var run = 0;
            for (var x = 0; x <= img.Width; x++)
            {
                if (x < img.Width && img.Pixels[y * img.Width + x] < threshold) run++;
                else { if (run > 0) runs.Add(run); run = 0; }
            }
        }
        if (runs.Count == 0) return 0;
        runs.Sort();
        return runs[runs.Count / 2];
    }
}

public static class MaskTracer
{
    /// <summary>Connected components (4-neighbour) → bounding-box outlines of at least minArea pixels.</summary>
    public static List<List<PointPx>> Trace(bool[] mask, int w, int h, int minArea)
    {
        var seen = new bool[mask.Length];
        var result = new List<List<PointPx>>();
        var stack = new Stack<int>();
        for (var s = 0; s < mask.Length; s++)
        {
            if (!mask[s] || seen[s]) continue;
            int minX = w, minY = h, maxX = -1, maxY = -1, area = 0;
            stack.Push(s); seen[s] = true;
            while (stack.Count > 0)
            {
                var i = stack.Pop(); var x = i % w; var y = i / w; area++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                void N(int nx, int ny) { if (nx < 0 || ny < 0 || nx >= w || ny >= h) return; var j = ny * w + nx; if (mask[j] && !seen[j]) { seen[j] = true; stack.Push(j); } }
                N(x - 1, y); N(x + 1, y); N(x, y - 1); N(x, y + 1);
            }
            if (area < minArea) continue;
            result.Add(new() { new(minX, minY), new(maxX + 1, minY), new(maxX + 1, maxY + 1), new(minX, maxY + 1) });
        }
        return result;
    }
}

public static class PlanWallDetector
{
    public static List<List<PointPx>> Detect(GrayImage img, WallDetectionOptions o, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return MaskTracer.Trace(LineExtraction.Mask(img, o.Threshold), img.Width, img.Height, o.MinArea);
    }
}
