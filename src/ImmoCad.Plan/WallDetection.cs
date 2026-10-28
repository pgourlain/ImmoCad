namespace ImmoCad.Plan;

public readonly record struct PointPx(double X, double Y);
public sealed record GrayImage(byte[] Pixels, int Width, int Height);

/// <summary>Outline of a connected group of walls and the holes (rooms) inside it, in pixels.</summary>
public sealed record WallOutline(List<PointPx> Outline, List<List<PointPx>> Holes);

/// <summary>Threshold 0 = automatic (Otsu). MinThickness = pixels from which a stroke counts as a wall.</summary>
public sealed record WallDetectionOptions(int Threshold = 0, int MinThickness = 5, int BridgeGap = 0, int MinArea = 20, double Tolerance = 1.5);

public static class PlanWallDetector
{
    public static List<WallOutline> Detect(GrayImage img, WallDetectionOptions o, CancellationToken ct = default)
    {
        var mask = LineExtraction.Extract(img.Pixels, img.Width, img.Height,
            new LineExtractionOptions(LineMode.Walls, o.Threshold, o.MinThickness, o.BridgeGap, o.MinArea), ct);
        return MaskTracer.Trace(mask, img.Width, img.Height, new TraceOptions(Tolerance: o.Tolerance, MinArea: o.MinArea), ct).ToList();
    }

    public static int SuggestThickness(GrayImage img, int threshold = 0) =>
        LineExtraction.SuggestThickness(img.Pixels, img.Width, img.Height, new LineExtractionOptions(Threshold: threshold));
}
