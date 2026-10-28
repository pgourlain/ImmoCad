using ImmoCad.Core.Model;

namespace ImmoCad.Core.Geometry;

/// <summary>
/// Vertical plane along a wall face seen from the front: u runs along A→B (mm), z is altitude. Pipes are placed at an offset
/// from the face (default 50 mm) on the chosen side, so the elevation view and the plan agree.
/// </summary>
public sealed class ElevationPlane
{
    public PointMm A { get; }
    public PointMm B { get; }
    public int OffsetMm { get; }
    /// <summary>+1 = left of A→B, -1 = right (the side the viewer stands on).</summary>
    public int Side { get; }
    public double Length { get; }
    private readonly double _ux, _uy;

    public ElevationPlane(PointMm a, PointMm b, int offsetMm = 50, int side = 1)
    {
        A = a; B = b; OffsetMm = offsetMm; Side = side >= 0 ? 1 : -1;
        double dx = b.X - a.X, dy = b.Y - a.Y; Length = Math.Sqrt(dx * dx + dy * dy);
        if (Length < 1) throw new ArgumentException("Degenerate face");
        _ux = dx / Length; _uy = dy / Length;
    }

    /// <summary>Plan position at distance <paramref name="u"/> along the face, offset from it.</summary>
    public PointMm ToPlan(double u, int? offsetMm = null)
    {
        var o = (offsetMm ?? OffsetMm) * Side;
        return new((int)Math.Round(A.X + _ux * u - _uy * o), (int)Math.Round(A.Y + _uy * u + _ux * o));
    }

    public double ProjectU(PointMm p) => (p.X - A.X) * _ux + (p.Y - A.Y) * _uy;
    public double Distance(PointMm p) => ((p.X - A.X) * -_uy + (p.Y - A.Y) * _ux) * Side;   // signed, + on the viewer's side

    /// <summary>Snaps an altitude to the nearest level line (floor or ceiling of any level) within the reach.</summary>
    public static int SnapZ(Building b, int z, int reachMm = 150)
    {
        var lines = b.Levels.SelectMany(l => new[] { l.AltitudeMm, l.AltitudeMm + l.HeightMm }).Distinct().OrderBy(v => Math.Abs(v - z));
        var best = lines.FirstOrDefault(); return b.Levels.Count > 0 && Math.Abs(best - z) <= reachMm ? best : z;
    }

    /// <summary>Segments visible from this plane: within <paramref name="reachMm"/> of it and over the face length.</summary>
    public IEnumerable<(Networks.SegmentRef Seg, double U0, double U1)> Visible(Building b, int reachMm = 600) =>
        Networks.NetworkQueries.All(b).Select(s =>
        {
            var pa = new PointMm(s.A.X, s.A.Y); var pb = new PointMm(s.B.X, s.B.Y);
            return (Seg: s, U0: ProjectU(pa), U1: ProjectU(pb), D0: Distance(pa), D1: Distance(pb));
        }).Where(x => Math.Abs(x.D0) <= reachMm && Math.Abs(x.D1) <= reachMm && Math.Max(x.U0, x.U1) >= 0 && Math.Min(x.U0, x.U1) <= Length)
          .Select(x => (x.Seg, x.U0, x.U1));
}
