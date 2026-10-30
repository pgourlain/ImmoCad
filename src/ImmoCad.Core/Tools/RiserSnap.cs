using ImmoCad.Core.Model;

namespace ImmoCad.Core.Tools;

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

    /// <summary>Unit step of a row of tubes after <paramref name="quarterTurns"/> quarter turns counter-clockwise (plan view, Y north): east, north, west, south.</summary>
    public static (int X, int Y) RowStep(int quarterTurns) => (((quarterTurns % 4) + 4) % 4) switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };

    /// <summary>
    /// Snap of a row of tubes (<paramref name="count"/> tubes <paramref name="spacingMm"/> apart, the first at <paramref name="origin"/>, the row turned by <paramref name="quarterTurns"/> about it).
    /// The side of a wall face is decided by where the row's origin is; the tube of the row that is nearest to the face is put at its clearance and the whole row moves with it,
    /// so no tube ends up inside a wall. A tube keeps at least its radius + 10 mm from the face (50 mm for the usual small ones).
    /// </summary>
    public static PointMm SnapRow(PointMm origin, int spacingMm, int count, int diameterMm, IEnumerable<IReadOnlyList<PointMm>> walls, int reachMm = 300, int quarterTurns = 0)
    {
        var offset = Math.Max(50, diameterMm / 2 + 10); var (sx, sy) = RowStep(quarterTurns); var n = Math.Max(0, count - 1) * spacingMm;
        int x0 = Math.Min(origin.X, origin.X + sx * n), x1 = Math.Max(origin.X, origin.X + sx * n), y0 = Math.Min(origin.Y, origin.Y + sy * n), y1 = Math.Max(origin.Y, origin.Y + sy * n);
        int? shiftX = null, shiftY = null; var dx = int.MaxValue; var dy = int.MaxValue;
        foreach (var poly in walls)
            for (var i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                if (a.Y == b.Y && x1 >= Math.Min(a.X, b.X) - reachMm && x0 <= Math.Max(a.X, b.X) + reachMm)
                {
                    var below = origin.Y < a.Y; var tube = below ? y1 : y0;                                  // the tube facing the wall
                    var shift = below ? a.Y - offset - tube : a.Y + offset - tube;
                    if (Math.Abs(shift) <= reachMm && Math.Abs(tube - a.Y) < dy) { dy = Math.Abs(tube - a.Y); shiftY = shift; }
                }
                if (a.X == b.X && y1 >= Math.Min(a.Y, b.Y) - reachMm && y0 <= Math.Max(a.Y, b.Y) + reachMm)
                {
                    var left = origin.X < a.X; var tube = left ? x1 : x0;
                    var shift = left ? a.X - offset - tube : a.X + offset - tube;
                    if (Math.Abs(shift) <= reachMm && Math.Abs(tube - a.X) < dx) { dx = Math.Abs(tube - a.X); shiftX = shift; }
                }
            }
        return new PointMm(origin.X + (shiftX ?? 0), origin.Y + (shiftY ?? 0));
    }
}
