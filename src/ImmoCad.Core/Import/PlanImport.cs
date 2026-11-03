using ImageMagick;
using ImmoCad.Core.History;
using ImmoCad.Core.Model;
using ImmoCad.Plan;

namespace ImmoCad.Core.Import;

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

    /// <summary>A picture decoded once, to rotate again and again (straighten step) without decoding the file each time.</summary>
    public sealed class RotationSource
    {
        private readonly MagickImage _src;
        private readonly Dictionary<int, (byte[] Rgb, int W, int H)> _quarters = new();
        public RotationSource(byte[] data) { _src = new MagickImage(data); }

        /// <summary>PNG bytes (fast compression) and gray pixels of the picture rotated by <paramref name="degrees"/> (clockwise, white fill), in one pass.
        /// Quarter turns go through ImageMagick (exact and quick); any other angle is resampled here on every core, because ImageMagick's
        /// rotation is single-threaded in this package and takes seconds on a scanned plan.</summary>
        public (byte[] Png, GrayImage Gray) Rotate(double degrees)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var quarter = (int)Math.Round(degrees / 90.0); var rest = degrees - 90.0 * quarter; var q = (quarter % 4 + 4) % 4;
            MagickImage? img = null; long t0, t1, t2;
            try
            {
                if (Math.Abs(rest) < 1e-6)
                {
                    lock (_src) img = (MagickImage)_src.Clone();
                    t0 = sw.ElapsedMilliseconds;
                    if (q != 0) { img.BackgroundColor = MagickColors.White; img.Rotate(90.0 * q); }
                }
                else
                {
                    var (rgb, w0, h0) = Quarter(q); t0 = sw.ElapsedMilliseconds;
                    var (rot, w, h) = ResampleRgb(rgb, w0, h0, rest);
                    img = new MagickImage(rot, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB));
                }
                t1 = sw.ElapsedMilliseconds;
                img.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
                img.Settings.SetDefine(MagickFormat.Png, "compression-filter", "1");
                var png = img.ToByteArray(MagickFormat.Png);
                t2 = sw.ElapsedMilliseconds;
                img.ColorSpace = ColorSpace.Gray;
                var wd = (int)img.Width; var ht = (int)img.Height;
                using var px = img.GetPixels();
                var gray = new GrayImage(px.ToByteArray("I") ?? throw new InvalidDataException("cannot read pixels"), wd, ht);
                var t3 = sw.ElapsedMilliseconds;
                LastTiming = new RotationTiming(wd, ht, png.Length, t0, t1 - t0, t2 - t1, t3 - t2, t3);
                return (png, gray);
            }
            finally { img?.Dispose(); }
        }

        /// <summary>The picture turned by <paramref name="q"/> quarter turns, as RGB bytes (transparency flattened on white), kept for the next rotations.</summary>
        private (byte[] Rgb, int W, int H) Quarter(int q)
        {
            lock (_quarters)
            {
                if (_quarters.TryGetValue(q, out var cached)) return cached;
                MagickImage img; lock (_src) img = (MagickImage)_src.Clone();
                using (img)
                {
                    img.BackgroundColor = MagickColors.White; if (img.HasAlpha) img.ColorAlpha(MagickColors.White);
                    if (q != 0) img.Rotate(90.0 * q);
                    img.ColorSpace = ColorSpace.sRGB;
                    using var px = img.GetPixels();
                    var rgb = px.ToByteArray("RGB") ?? throw new InvalidDataException("cannot read pixels");
                    return _quarters[q] = (rgb, (int)img.Width, (int)img.Height);
                }
            }
        }

        /// <summary>Bilinear rotation of RGB bytes by <paramref name="degrees"/> clockwise; the canvas grows to hold the whole picture, the corners are white.</summary>
        public static (byte[] Rgb, int W, int H) ResampleRgb(byte[] src, int w, int h, double degrees)
        {
            var a = degrees * Math.PI / 180; double cos = Math.Cos(a), sin = Math.Sin(a);
            var nw = (int)Math.Ceiling(w * Math.Abs(cos) + h * Math.Abs(sin)); var nh = (int)Math.Ceiling(w * Math.Abs(sin) + h * Math.Abs(cos));
            var dst = new byte[nw * nh * 3]; double cx = w / 2.0, cy = h / 2.0, ox = nw / 2.0, oy = nh / 2.0;
            Parallel.For(0, nh, y =>
            {
                var dy = y + 0.5 - oy; var row = y * nw * 3;
                for (var x = 0; x < nw; x++)
                {
                    var dx = x + 0.5 - ox; var sx = cx + dx * cos + dy * sin - 0.5; var sy = cy - dx * sin + dy * cos - 0.5; var o = row + x * 3;
                    if (sx < -1 || sy < -1 || sx > w || sy > h) { dst[o] = dst[o + 1] = dst[o + 2] = 255; continue; }
                    var x0 = (int)Math.Floor(sx); var y0 = (int)Math.Floor(sy); var fx = sx - x0; var fy = sy - y0;
                    int xa = Math.Clamp(x0, 0, w - 1), xb = Math.Clamp(x0 + 1, 0, w - 1), ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
                    int p00 = (ya * w + xa) * 3, p10 = (ya * w + xb) * 3, p01 = (yb * w + xa) * 3, p11 = (yb * w + xb) * 3;
                    for (var c = 0; c < 3; c++)
                    {
                        var top = src[p00 + c] + (src[p10 + c] - src[p00 + c]) * fx; var bot = src[p01 + c] + (src[p11 + c] - src[p01 + c]) * fx;
                        dst[o + c] = (byte)(top + (bot - top) * fy + 0.5);
                    }
                }
            });
            return (dst, nw, nh);
        }

        /// <summary>Durations of the last <see cref="Rotate"/> call, to find what is slow on a given machine.</summary>
        public RotationTiming? LastTiming { get; private set; }
    }

    /// <summary>Where the time of one rotation went (milliseconds): clone, rotate, PNG encode, gray pixels, total.</summary>
    public sealed record RotationTiming(int Width, int Height, int PngBytes, long CloneMs, long RotateMs, long PngMs, long GrayMs, long TotalMs)
    {
        public override string ToString() => $"{Width}x{Height}px, png {PngBytes / 1024} KiB: clone {CloneMs} ms, rotate {RotateMs} ms, png {PngMs} ms, gray {GrayMs} ms, total {TotalMs} ms";
    }

    public static (int Width, int Height) Size(byte[] data) { var i = new MagickImageInfo(data); return ((int)i.Width, (int)i.Height); }

    /// <summary>Resolution stored in the picture (dots per inch), 0 when it carries none.</summary>
    public static double DensityDpi(byte[] data)
    {
        try
        {
            var d = new MagickImageInfo(data).Density; if (d is null || d.X <= 1) return 0;
            return d.Units == DensityUnit.PixelsPerCentimeter ? d.X * 2.54 : d.Units == DensityUnit.PixelsPerInch ? d.X : 0;
        }
        catch { return 0; }
    }

    /// <summary>pixels per mm of a picture scanned at <paramref name="dpi"/> from a drawing printed at 1:<paramref name="denominator"/>.</summary>
    public static double PxPerMmFromScale(double dpi, double denominator) => dpi / (25.4 * denominator);

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
