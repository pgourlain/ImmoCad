using ImmoCad.Core.Geometry;
using ImmoCad.Core.Model;

namespace ImmoCad.Core.Walk;

/// <summary>A position on the plan in millimetres. The walk is view state, not model: doubles are fine here (the model itself stays in integer millimetres).</summary>
public readonly record struct WalkPoint(double X, double Y);

/// <summary>Where the walk may go: the world limit, in millimetres, and the heights the eye may reach.</summary>
public sealed record WorldBounds(double MinX, double MinY, double MaxX, double MaxY, double MinZ, double MaxZ)
{
    public bool Contains(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
    public WalkPoint Clamp(WalkPoint p) => new(Math.Clamp(p.X, MinX, MaxX), Math.Clamp(p.Y, MinY, MaxY));

    /// <summary>
    /// The frame set on the building, otherwise the bounding box of everything modelled (walls, pipes, equipment of every level) plus one metre,
    /// so a pipe modelled out to the street can be followed with nothing to set up. Heights: from the lowest level's floor to the highest level's ceiling.
    /// </summary>
    public static WorldBounds For(Building b)
    {
        double minZ = b.Levels.Count == 0 ? 0 : b.Levels.Min(l => l.AltitudeMm), maxZ = b.Levels.Count == 0 ? 2700 : b.Levels.Max(l => l.AltitudeMm + l.HeightMm);
        if (b.WorldFrame is { } f) return new(f.X0, f.Y0, f.X1, f.Y1, minZ, maxZ);
        var xs = new List<int>(); var ys = new List<int>();
        foreach (var l in b.Levels) foreach (var w in l.Walls) foreach (var p in w.Footprint) { xs.Add(p.X); ys.Add(p.Y); }
        foreach (var n in b.Networks) foreach (var nd in n.Nodes) { xs.Add(nd.Position.X); ys.Add(nd.Position.Y); }
        foreach (var e in b.Equipment) { xs.Add(e.Position.X); ys.Add(e.Position.Y); }
        if (xs.Count == 0) return new(-1000, -1000, 1000, 1000, minZ, maxZ);
        return new(xs.Min() - 1000, ys.Min() - 1000, xs.Max() + 1000, ys.Max() + 1000, minZ, maxZ);
    }
}

/// <summary>
/// What blocks the walk on one level, at the height of the eye: wall material only. An opening of any kind (door, window, simple opening) is a passage while the eye
/// is between its sill and its top; the wall above a lintel or below a sill blocks. A wall lower than the eye does not block. Holes between walls are passages too.
/// </summary>
public sealed class WalkArea
{
    /// <summary>Collision radius: 200 mm, so that a 700 mm cellar door can be crossed.</summary>
    public const double Radius = 200;

    private sealed class Solid
    {
        public double MinX, MinY, MaxX, MaxY; public int HeightMm;
        public List<PointMm> Outline = new(); public List<List<PointMm>> Holes = new();
        public List<(double Ax, double Ay, double Bx, double By)> Edges = new();
        public List<OpeningSpan> Gates = new();
    }

    private readonly List<Solid> _solids = new();
    public WorldBounds Bounds { get; }
    public Level Level { get; }
    public int WallCount => _solids.Count;

    public WalkArea(Building b, Level level) : this(level, WorldBounds.For(b)) { }
    public WalkArea(Level level, WorldBounds bounds)
    {
        Level = level; Bounds = bounds;
        foreach (var w in level.Walls)
        {
            if (w.Footprint.Count < 3) continue;
            var s = new Solid { HeightMm = w.HeightMm ?? level.HeightMm, Outline = w.Footprint, Holes = w.Holes };
            AddEdges(s, w.Footprint); foreach (var h in w.Holes) AddEdges(s, h);
            s.MinX = w.Footprint.Min(p => p.X); s.MaxX = w.Footprint.Max(p => p.X); s.MinY = w.Footprint.Min(p => p.Y); s.MaxY = w.Footprint.Max(p => p.Y);
            foreach (var o in w.Openings) if (WallGeometry.SpanOf(w, o) is { } span) s.Gates.Add(span);
            _solids.Add(s);
        }
    }

    private static void AddEdges(Solid s, List<PointMm> ring)
    {
        for (var i = 0; i < ring.Count; i++) { var a = ring[i]; var b = ring[(i + 1) % ring.Count]; if (a != b) s.Edges.Add((a.X, a.Y, b.X, b.Y)); }
    }

    private static bool InSolid(Solid s, double x, double y)
    {
        var p = new PointMm((int)Math.Round(x), (int)Math.Round(y));
        return PolygonOps.Contains(s.Outline, p) && !s.Holes.Any(h => PolygonOps.Contains(h, p));
    }

    private static bool InOpenGate(Solid s, double x, double y, double eyeMm) { foreach (var g in s.Gates) if (g.OpenAt(eyeMm) && g.Contains(x, y)) return true; return false; }

    /// <summary>The point of segment A→B closest to (x, y).</summary>
    private static (double X, double Y) Closest(double ax, double ay, double bx, double by, double x, double y)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy; if (l2 < 1e-9) return (ax, ay);
        var t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1); return (ax + dx * t, ay + dy * t);
    }

    /// <summary>Pushes the point out of the walls that block at this height (<paramref name="eyeMm"/> above the floor). Returns true when it moved.</summary>
    private bool PushOut(ref double x, ref double y, double eyeMm)
    {
        var moved = false;
        for (var iter = 0; iter < 4; iter++)
        {
            var again = false;
            foreach (var s in _solids)
            {
                if (eyeMm > s.HeightMm || x < s.MinX - Radius || x > s.MaxX + Radius || y < s.MinY - Radius || y > s.MaxY + Radius) continue;
                if (InOpenGate(s, x, y, eyeMm)) continue;                                                                  // standing in a passage: this wall does not hold
                var inside = InSolid(s, x, y);
                foreach (var (ax, ay, bx, by) in s.Edges)
                {
                    var (qx, qy) = Closest(ax, ay, bx, by, x, y); double dx = x - qx, dy = y - qy; var d = Math.Sqrt(dx * dx + dy * dy);
                    if (!inside && d >= Radius - 1e-6) continue;
                    if (s.Gates.Any(g => g.OpenAt(eyeMm) && g.Contains(qx, qy))) continue;                                // this part of the wall is a passage
                    double ux, uy;
                    if (d < 1e-6) { var el = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)); ux = -(by - ay) / el; uy = (bx - ax) / el; }
                    else { ux = dx / d; uy = dy / d; }
                    if (inside) { ux = -ux; uy = -uy; }                                                                 // inside the material: out through the nearest face
                    x = qx + ux * Radius; y = qy + uy * Radius; moved = again = true; inside = InSolid(s, x, y);
                }
            }
            if (!again) break;
        }
        return moved;
    }

    /// <summary>True when the point is inside the material or closer than the radius to it, at this eye height.</summary>
    public bool Collides(WalkPoint p, double eyeMm)
    {
        double x = p.X, y = p.Y; var before = (x, y); PushOut(ref x, ref y, eyeMm);
        return Math.Abs(x - before.x) > 0.5 || Math.Abs(y - before.y) > 0.5;
    }

    /// <summary>
    /// Moves by a delta, sliding along walls. The move is cut into steps shorter than the radius, so a fast move never tunnels through a thin wall.
    /// With <paramref name="ghost"/> only the world limit stops the move.
    /// </summary>
    public WalkPoint Move(WalkPoint from, double dx, double dy, double eyeMm, bool ghost)
    {
        double x = from.X, y = from.Y; var len = Math.Sqrt(dx * dx + dy * dy);
        if (ghost) return Bounds.Clamp(new(x + dx, y + dy));
        var n = Math.Max(1, (int)Math.Ceiling(len / (Radius / 2)));
        for (var i = 0; i < n; i++) { x += dx / n; y += dy / n; PushOut(ref x, ref y, eyeMm); var c = Bounds.Clamp(new(x, y)); x = c.X; y = c.Y; }
        return new(x, y);
    }

    /// <summary>
    /// The closest point where the user can stand, searching outwards in rings of 50 mm. Among the points of the first free ring, the one closest to
    /// <paramref name="prefer"/> wins (the side the user came from); without a preference, the one closest to the requested point.
    /// </summary>
    public WalkPoint NearestWalkable(WalkPoint at, double eyeMm, WalkPoint? prefer = null)
    {
        at = Bounds.Clamp(at); if (!Collides(at, eyeMm)) return at;
        var target = prefer ?? at;
        for (double r = 50; r <= 30000; r += 50)
        {
            var steps = Math.Max(16, (int)(2 * Math.PI * r / 100)); WalkPoint? best = null; var bestD = double.MaxValue;
            for (var k = 0; k < steps; k++)
            {
                var a = 2 * Math.PI * k / steps; var c = new WalkPoint(at.X + Math.Cos(a) * r, at.Y + Math.Sin(a) * r);
                if (!Bounds.Contains(c.X, c.Y) || Collides(c, eyeMm)) continue;
                var d = (c.X - target.X) * (c.X - target.X) + (c.Y - target.Y) * (c.Y - target.Y); if (d < bestD) { bestD = d; best = c; }
            }
            if (best is { } found) return found;
        }
        return at;
    }

    /// <summary>The direction (yaw, radians, as in <see cref="WalkState.Yaw"/>) with the longest free line from a point: where a first look shows the most corridor.</summary>
    public double LongestFreeYaw(WalkPoint from, double eyeMm)
    {
        double bestYaw = 0, best = -1;
        for (var k = 0; k < 16; k++)
        {
            var yaw = k * Math.PI / 8; double dx = -Math.Sin(yaw) * 100, dy = Math.Cos(yaw) * 100; var p = from; double run = 0;
            for (; run < 40000; run += 100) { var q = Move(p, dx, dy, eyeMm, false); if (Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y)) < 98) break; p = q; }    // sliding along a wall is not a free line
            if (run > best + 1) { best = run; bestYaw = yaw; }
        }
        return bestYaw;
    }
}
