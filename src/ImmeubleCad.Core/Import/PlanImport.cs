using ImageMagick;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Plan;

namespace ImmeubleCad.Core.Import;

public interface IWallDetector { List<WallOutline> Detect(GrayImage image, WallDetectionOptions options, CancellationToken ct); }

public sealed class PlanWallDetectorAdapter : IWallDetector
{
    public List<WallOutline> Detect(GrayImage image, WallDetectionOptions options, CancellationToken ct) => Plan.PlanWallDetector.Detect(image, options, ct);
}

/// <summary>Calibration: pixels per mm, separately on X and Y (scans are often stretched).</summary>
public readonly record struct Calibration(double PxPerMmX, double PxPerMmY)
{
    public static Calibration FromMeasure(double pixelsX, double realMmX, double? pixelsY = null, double? realMmY = null) =>
        new(pixelsX / realMmX, pixelsY is { } py && realMmY is { } ry ? py / ry : pixelsX / realMmX);
}

public static class PlanImport
{
    /// <summary>Core decodes the file with Magick.NET; Plan never sees a file.</summary>
    public static GrayImage Decode(byte[] data)
    {
        using var img = new MagickImage(data);
        img.ColorSpace = ColorSpace.Gray;
        var w = (int)img.Width; var h = (int)img.Height;
        using var px = img.GetPixels();
        var raw = px.ToByteArray("I") ?? throw new InvalidDataException("cannot read pixels");
        return new GrayImage(raw, w, h);
    }

    /// <summary>Pixel outline → mm using calibration. Image Y goes down, plan Y goes up.</summary>
    public static List<PointMm> ToMm(IEnumerable<PointPx> outline, Calibration c, int imageHeightPx) =>
        outline.Select(p => new PointMm((int)Math.Round(p.X / c.PxPerMmX), (int)Math.Round((imageHeightPx - p.Y) / c.PxPerMmY))).ToList();

    /// <summary>Whole import = one history step (BuildingActions.ImportWalls).</summary>
    public static int Import(BuildingActions actions, Guid levelId, byte[] imageBytes, Calibration cal, IWallDetector detector,
        WallDetectionOptions? options = null, bool straighten = false, CancellationToken ct = default)
    {
        var img = Decode(imageBytes);
        var found = detector.Detect(img, options ?? new WallDetectionOptions(), ct);
        List<PointPx> S(List<PointPx> o) => straighten ? WallStraightener.Straighten(o, 3) : o;
        var shapes = found.Select(w => ((IEnumerable<PointMm>)ToMm(S(w.Outline), cal, img.Height),
            (IEnumerable<IEnumerable<PointMm>>)w.Holes.Select(h => (IEnumerable<PointMm>)ToMm(S(h), cal, img.Height)).ToList())).ToList();
        actions.ImportWallShapes(levelId, shapes);
        return shapes.Count;
    }
}
