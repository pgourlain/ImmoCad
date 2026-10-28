namespace ImmoCad.Core.Site;

/// <summary>WGS84 ↔ Lambert 93 (EPSG:2154). Pure math (IGN ALG0003/ALG0004), metres.</summary>
public static class Lambert93
{
    private const double A = 6378137.0, E = 0.0818191910428158;
    private const double Lambda0 = 3.0 * Math.PI / 180, Phi0 = 46.5 * Math.PI / 180, Phi1 = 44.0 * Math.PI / 180, Phi2 = 49.0 * Math.PI / 180;
    private const double X0 = 700000.0, Y0 = 6600000.0;

    private static double M(double phi) => Math.Cos(phi) / Math.Sqrt(1 - E * E * Math.Sin(phi) * Math.Sin(phi));
    private static double T(double phi) => Math.Tan(Math.PI / 4 - phi / 2) / Math.Pow((1 - E * Math.Sin(phi)) / (1 + E * Math.Sin(phi)), E / 2);
    private static readonly double N = Math.Log(M(Phi1) / M(Phi2)) / Math.Log(T(Phi1) / T(Phi2));
    private static readonly double F = M(Phi1) / (N * Math.Pow(T(Phi1), N));
    private static readonly double Rho0 = A * F * Math.Pow(T(Phi0), N);

    public static (double E, double N) FromWgs84(double latDeg, double lonDeg)
    {
        var phi = latDeg * Math.PI / 180; var lam = lonDeg * Math.PI / 180;
        var rho = A * F * Math.Pow(T(phi), N); var th = N * (lam - Lambda0);
        return (X0 + rho * Math.Sin(th), Y0 + Rho0 - rho * Math.Cos(th));
    }

    public static (double Lat, double Lon) ToWgs84(double e, double n)
    {
        double dx = e - X0, dy = Rho0 - (n - Y0);
        var rho = Math.Sign(N) * Math.Sqrt(dx * dx + dy * dy); var th = Math.Atan2(dx, dy);
        var t = Math.Pow(rho / (A * F), 1 / N);
        var phi = Math.PI / 2 - 2 * Math.Atan(t);
        for (var i = 0; i < 12; i++)
            phi = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - E * Math.Sin(phi)) / (1 + E * Math.Sin(phi)), E / 2));
        return (phi * 180 / Math.PI, (th / N + Lambda0) * 180 / Math.PI);
    }
}

/// <summary>Building anchor in Lambert 93 (metres) with rotation to true north (degrees). Model stays in mm relative to the anchor.</summary>
public sealed record SiteAnchor(double EastM, double NorthM, double RotationDeg = 0, double AltitudeM = 0)
{
    public (double E, double N) ToLambert(int xMm, int yMm)
    {
        var r = RotationDeg * Math.PI / 180; double x = xMm / 1000.0, y = yMm / 1000.0;
        return (EastM + x * Math.Cos(r) - y * Math.Sin(r), NorthM + x * Math.Sin(r) + y * Math.Cos(r));
    }
    public (int X, int Y) FromLambert(double e, double n)
    {
        var r = -RotationDeg * Math.PI / 180; double dx = e - EastM, dy = n - NorthM;
        return ((int)Math.Round((dx * Math.Cos(r) - dy * Math.Sin(r)) * 1000), (int)Math.Round((dx * Math.Sin(r) + dy * Math.Cos(r)) * 1000));
    }
}

public interface IGeocoder { Task<(double Lat, double Lon)?> GeocodeAsync(string address, CancellationToken ct = default); }
public interface ICadastreService { Task<IReadOnlyList<IReadOnlyList<(double E, double N)>>> BuildingFootprintsAsync(double lat, double lon, int radiusM, CancellationToken ct = default); }
public interface IMapTileSource { Task<byte[]?> GetTileAsync(string layer, int z, int x, int y, CancellationToken ct = default); }
