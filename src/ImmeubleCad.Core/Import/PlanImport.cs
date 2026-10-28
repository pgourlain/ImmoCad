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

    /// <summary>Rotates the picture by <paramref name="degrees"/> (white fill) and returns PNG bytes; used by the straighten step.</summary>
    public static byte[] Rotate(byte[] data, double degrees)
    {
        using var img = new MagickImage(data);
        if (Math.Abs(degrees) > 1e-6) { img.BackgroundColor = MagickColors.White; img.Rotate(degrees); }
        return img.ToByteArray(MagickFormat.Png);
    }

    public static (int Width, int Height) Size(byte[] data) { var i = new MagickImageInfo(data); return ((int)i.Width, (int)i.Height); }

    /// <summary>pixels per mm from two clicked points and the real distance in metres.</summary>
    public static double PxPerMm((double X, double Y) a, (double X, double Y) b, double realMetres) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) / (realMetres * 1000);

    /// <summary>Per-axis scale from a horizontal and a vertical measure.</summary>
    public static Calibration TwoAxis((double X, double Y) h1, (double X, double Y) h2, double realHMetres, (double X, double Y) v1, (double X, double Y) v2, double realVMetres) =>
        new(Math.Abs(h1.X - h2.X) / (realHMetres * 1000), Math.Abs(v1.Y - v2.Y) / (realVMetres * 1000));

    /// <summary>Image transform (pixels → mm, Y flipped) for a calibrated picture of the given height, as a 2×3 matrix.</summary>
    public static double[] TransformFor(Calibration c, int imageHeightPx) => [1 / c.PxPerMmX, 0, 0, 0, -1 / c.PxPerMmY, imageHeightPx / c.PxPerMmY];

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
