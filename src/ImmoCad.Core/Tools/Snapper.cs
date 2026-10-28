using ImmoCad.Core.Model;

namespace ImmoCad.Core.Tools;

public enum SnapKind { None, Grid, Corner, Midpoint, Orthogonal }
public readonly record struct SnapResult(PointMm Point, SnapKind Kind);

public sealed class Snapper
{
    public int GridMm { get; set; } = 100;
    /// <summary>True: points always land on the grid (after corner/midpoint/orthogonal snaps). False: free placement.</summary>
    public bool SnapToGrid { get; set; } = true;
    public int ToleranceMm { get; set; } = 150;

    public SnapResult Snap(PointMm p, IEnumerable<IReadOnlyList<PointMm>> polygons, PointMm? from = null)
    {
        SnapResult? best = null; long bestD = long.MaxValue;
        void Try(PointMm c, SnapKind k)
        {
            var d = D2(p, c);
            if (d <= (long)ToleranceMm * ToleranceMm && d < bestD) { bestD = d; best = new(c, k); }
        }
        foreach (var poly in polygons)
            for (var i = 0; i < poly.Count; i++)
            {
                Try(poly[i], SnapKind.Corner);
                var n = poly[(i + 1) % poly.Count];
                Try(new((poly[i].X + n.X) / 2, (poly[i].Y + n.Y) / 2), SnapKind.Midpoint);
            }
        if (best != null) return best.Value;
        if (from is { } f)
        {
            var dx = Math.Abs(p.X - f.X); var dy = Math.Abs(p.Y - f.Y);
            // along the straight line the free coordinate still lands on the grid
            if (dy <= ToleranceMm && dx > dy) return new(new(AlongGrid(p.X), f.Y), SnapKind.Orthogonal);
            if (dx <= ToleranceMm && dy > dx) return new(new(f.X, AlongGrid(p.Y)), SnapKind.Orthogonal);
        }
        if (SnapToGrid && GridMm > 0) return new(new PointMm(Round(p.X), Round(p.Y)), SnapKind.Grid);
        return new(p, SnapKind.None);
    }
    private int AlongGrid(int v) => SnapToGrid && GridMm > 0 ? Round(v) : v;
    private int Round(int v) => (int)Math.Round((double)v / GridMm) * GridMm;
    private static long D2(PointMm a, PointMm b) { long dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }
}
