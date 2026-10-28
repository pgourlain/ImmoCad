using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.Tools;

/// <summary>Snaps a riser next to wall faces with an offset (default 50 mm from each face), into corners when two faces are close.</summary>
public static class RiserSnap
{
    public static PointMm Snap(PointMm p, IEnumerable<IReadOnlyList<PointMm>> walls, int offsetMm = 50, int reachMm = 300)
    {
        // collect axis-aligned faces (horizontal: y = c, vertical: x = c) of wall footprints, with the side facing the point
        int? bestX = null, bestY = null; var dx = int.MaxValue; var dy = int.MaxValue;
        foreach (var poly in walls)
            for (var i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                if (a.Y == b.Y && p.X >= Math.Min(a.X, b.X) - reachMm && p.X <= Math.Max(a.X, b.X) + reachMm)
                {
                    var target = a.Y + (p.Y >= a.Y ? offsetMm : -offsetMm);
                    if (Math.Abs(p.Y - target) <= reachMm && Math.Abs(p.Y - a.Y) < dy) { dy = Math.Abs(p.Y - a.Y); bestY = target; }
                }
                if (a.X == b.X && p.Y >= Math.Min(a.Y, b.Y) - reachMm && p.Y <= Math.Max(a.Y, b.Y) + reachMm)
                {
                    var target = a.X + (p.X >= a.X ? offsetMm : -offsetMm);
                    if (Math.Abs(p.X - target) <= reachMm && Math.Abs(p.X - a.X) < dx) { dx = Math.Abs(p.X - a.X); bestX = target; }
                }
            }
        return new PointMm(bestX ?? p.X, bestY ?? p.Y);
    }
}
