namespace ImmeubleCad.Core.Site;

/// <summary>Slippy-map tile maths (EPSG:3857, the "PM" tile matrix set of the IGN WMTS).</summary>
public static class WebMercator
{
    public static (int X, int Y) TileOf(double latDeg, double lonDeg, int z)
    {
        var n = Math.Pow(2, z); var x = (lonDeg + 180) / 360 * n; var lat = latDeg * Math.PI / 180; var y = (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * n;
        return ((int)Math.Floor(x), (int)Math.Floor(y));
    }

    /// <summary>Latitude/longitude of the top-left corner of a tile.</summary>
    public static (double Lat, double Lon) CornerOf(int x, int y, int z)
    {
        var n = Math.Pow(2, z); var lon = x / n * 360 - 180; var lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180 / Math.PI; return (lat, lon);
    }

    /// <summary>Corners of a tile in Lambert 93 metres: top-left, top-right, bottom-left (enough to place the image with an affine map).</summary>
    public static ((double E, double N) TL, (double E, double N) TR, (double E, double N) BL) LambertCorners(int x, int y, int z)
    {
        var (la, lo) = CornerOf(x, y, z); var (lb, lr) = CornerOf(x + 1, y, z); var (lc, ll) = CornerOf(x, y + 1, z);
        return (Lambert93.FromWgs84(la, lo), Lambert93.FromWgs84(lb, lr), Lambert93.FromWgs84(lc, ll));
    }
}
