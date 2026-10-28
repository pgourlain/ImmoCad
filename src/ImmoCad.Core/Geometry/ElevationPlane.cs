using ImmoCad.Core.Model;

namespace ImmoCad.Core.Geometry;

/// <summary>
/// Vertical plane along a wall face seen from the front: u runs along A→B (mm), z is altitude. Pipes are placed at an offset
/// from the face (default 50 mm) on the chosen side, so the elevation view and the plan agree.
/// </summary>
/// <summary>The four horizontal directions a building can be viewed from (never from below).</summary>
public enum ViewSide { South, East, North, West }

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

    /// <summary>Whole-building elevation seen from one side, u growing to the viewer's right. Pipes placed on it sit <paramref name="depthMm"/> inside the outer bounding line.</summary>
    public static ElevationPlane ForSide(Building b, ViewSide side, int depthMm = 0)
    {
        var pts = b.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).ToList();
        int x0 = pts.Count > 0 ? pts.Min(p => p.X) : 0, x1 = pts.Count > 0 ? pts.Max(p => p.X) : 10000, y0 = pts.Count > 0 ? pts.Min(p => p.Y) : 0, y1 = pts.Count > 0 ? pts.Max(p => p.Y) : 10000;
        if (x1 - x0 < 100) x1 = x0 + 100; if (y1 - y0 < 100) y1 = y0 + 100;
        var (a, c) = side switch
        {
            ViewSide.South => (new PointMm(x0, y0), new PointMm(x1, y0)),   // viewer south of the building looking north: east on the right
            ViewSide.East => (new PointMm(x1, y0), new PointMm(x1, y1)),    // looking west: north on the right
            ViewSide.North => (new PointMm(x1, y1), new PointMm(x0, y1)),   // looking south: west on the right
            _ => (new PointMm(x0, y1), new PointMm(x0, y0)),                // looking east: south on the right
        };
        return new ElevationPlane(a, c, -Math.Max(0, depthMm), -1);
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
