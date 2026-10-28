using ImmoCad.Core.Model;

namespace ImmoCad.Core.Tools;

public enum SnapKind { None, Grid, Corner, Midpoint, Orthogonal }
public readonly record struct SnapResult(PointMm Point, SnapKind Kind);

public sealed class Snapper
{
    public int GridMm { get; set; } = 100;
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
            if (dy <= ToleranceMm && dx > dy) return new(new(p.X, f.Y), SnapKind.Orthogonal);
            if (dx <= ToleranceMm && dy > dx) return new(new(f.X, p.Y), SnapKind.Orthogonal);
        }
        if (GridMm > 0)
        {
            var g = new PointMm(Round(p.X), Round(p.Y));
            if (D2(p, g) <= (long)ToleranceMm * ToleranceMm) return new(g, SnapKind.Grid);
        }
        return new(p, SnapKind.None);
    }
    private int Round(int v) => (int)Math.Round((double)v / GridMm) * GridMm;
    private static long D2(PointMm a, PointMm b) { long dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }
}
