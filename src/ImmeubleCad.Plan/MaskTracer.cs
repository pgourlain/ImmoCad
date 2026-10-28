// Adapted from CinnabarSharp (https://github.com/pgourlain/CinnabarSharp), see PROVENANCE.md.
namespace ImmeubleCad.Plan;

/// <summary>
/// How <see cref="MaskTracer"/> turns a mask into outlines. <paramref name="Tolerance"/> is the largest distance in pixels
/// an outline may move when it is simplified; sides within <paramref name="SnapDegrees"/> of horizontal or vertical (and of
/// 45° with <paramref name="Diagonals"/>) are straightened; <paramref name="MinArea"/> drops outlines and holes smaller
/// than that many square pixels; <paramref name="Scale"/> multiplies the coordinates (drawing units per pixel).
/// </summary>
public sealed record TraceOptions(double Tolerance = 1.5, double SnapDegrees = 6, bool Diagonals = false,
    double MinArea = 4, double Scale = 1);

/// <summary>
/// Vectorizes a binary mask into filled outlines (not center lines): the boundary between mask and background is followed
/// along the pixel edges, simplified (Douglas–Peucker), and the sides that are nearly horizontal or vertical are made
/// exactly so, with the corners recomputed where the straightened sides meet. Pure integer tracing plus plain geometry:
/// the result is the same on every OS.
/// </summary>
public static class MaskTracer
{
    // Directions of the boundary edges, with the mask on the right-hand side: right, down, left, up (y grows downward).
    private static readonly (int Dx, int Dy)[] Directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    private static readonly int[] Turns = [1, 0, 3];

    private sealed class Loop(List<VPoint> points, double area)
    {
        public List<VPoint> Points { get; set; } = points;
        public double Area { get; } = area;
        public (double Left, double Top, double Right, double Bottom) Box { get; set; }
    }

    /// <summary>
    /// One path per connected piece of the mask: its outline and the holes in it, to be filled with the even-odd rule.
    /// Coordinates are in pixels times <see cref="TraceOptions.Scale"/>; pixel (x, y) covers x..x+1, y..y+1.
    /// </summary>
    public static IReadOnlyList<WallOutline> Trace(byte[] mask, int width, int height, TraceOptions? options = null,
        CancellationToken cancellation = default)
    {
        options ??= new TraceOptions();
        if (mask.Length != width * height)
            throw new ArgumentException("The mask must be width × height.", nameof(mask));

        var loops = FollowBoundaries(mask, width, height, cancellation);
        var outers = loops.Where(l => l.Area > 0).OrderBy(l => l.Area).ToList();
        var holes = loops.Where(l => l.Area < 0).ToList();

        // A hole belongs to the smallest outline that contains the mask pixel next to the hole's first side.
        var holesOf = new Dictionary<Loop, List<Loop>>();
        foreach (var hole in holes)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = hole.Points[0];
            var b = hole.Points[1 % hole.Points.Count];
            // The mask is on the right of the travel direction: step a quarter pixel that way from the side's middle.
            var (dx, dy) = (Math.Sign(b.X - a.X), Math.Sign(b.Y - a.Y));
            var probe = new VPoint((a.X + b.X) / 2 - dy * 0.25, (a.Y + b.Y) / 2 + dx * 0.25);
            var owner = outers.FirstOrDefault(o => Contains(o, probe));
            if (owner is not null)
            {
                if (!holesOf.TryGetValue(owner, out var list))
                    holesOf[owner] = list = [];
                list.Add(hole);
            }
        }

        var result = new List<WallOutline>();
        foreach (var outer in outers.OrderBy(o => o.Box.Top).ThenBy(o => o.Box.Left))
        {
            cancellation.ThrowIfCancellationRequested();
            if (outer.Area < options.MinArea)
                continue;
            var outline = Figure(outer.Points, options);
            if (outline == null)
                continue;
            var shape = new WallOutline(outline, []);
            if (holesOf.TryGetValue(outer, out var inside))
                foreach (var hole in inside.Where(h => -h.Area >= options.MinArea).OrderBy(h => h.Box.Top).ThenBy(h => h.Box.Left))
                    if (Figure(hole.Points, options) is { } h)
                        shape.Holes.Add(h);
            result.Add(shape);
        }
        return result;
    }

    private static List<PointPx>? Figure(List<VPoint> corners, TraceOptions options)
    {
        var points = Simplify(corners, options);
        if (points.Count < 3)
            return null;
        return points.Select(p => new PointPx(p.X * options.Scale, p.Y * options.Scale)).ToList();
    }

    // ---- Boundary following ----

    /// <summary>All closed boundaries of the mask, as the corner points of their staircase, with their signed areas.</summary>
    private static List<Loop> FollowBoundaries(byte[] mask, int width, int height, CancellationToken cancellation)
    {
        // Outgoing edges per grid corner, as a bit set of Directions.
        var edges = new Dictionary<int, byte>();
        var stride = width + 1;
        bool Ink(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x] != 0;
        void Add(int x, int y, int direction)
        {
            var key = y * stride + x;
            edges[key] = (byte)((edges.TryGetValue(key, out var bits) ? bits : 0) | (1 << direction));
        }
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                if (mask[y * width + x] == 0)
                    continue;
                if (!Ink(x, y - 1))
                    Add(x, y, 0); // top side, going right
                if (!Ink(x + 1, y))
                    Add(x + 1, y, 1); // right side, going down
                if (!Ink(x, y + 1))
                    Add(x + 1, y + 1, 2); // bottom side, going left
                if (!Ink(x - 1, y))
                    Add(x, y + 1, 3); // left side, going up
            }
        }

        var loops = new List<Loop>();
        var starts = edges.Keys.ToList();
        starts.Sort();
        foreach (var start in starts)
        {
            while (edges.TryGetValue(start, out var bits) && bits != 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var first = LowestBit(bits);
                var points = new List<VPoint>();
                var (key, direction, previous) = (start, first, -1);
                // The right-turn rule maps each incoming edge to one outgoing edge, so the walk comes back to (start, first)
                // and nowhere else. That first edge stays in the set until then: at a corner shared by two boundaries it
                // is a candidate for the choice, and removing it early would send the walk into the other boundary.
                while (true)
                {
                    int x = key % stride, y = key / stride;
                    if (direction != previous)
                        points.Add(new VPoint(x, y));
                    if (previous >= 0)
                        RemoveEdge(edges, key, direction);
                    previous = direction;
                    var (dx, dy) = Directions[direction];
                    key = (y + dy) * stride + x + dx;
                    direction = PickNext(edges, key, direction);
                    if (key == start && direction == first)
                        break;
                }
                RemoveEdge(edges, start, first);
                // The start is a corner only if the walk turned there.
                if (points.Count > 1 && previous == first)
                    points.RemoveAt(0);
                if (points.Count >= 3)
                    loops.Add(MakeLoop(points));
            }
        }
        return loops;
    }

    private static int LowestBit(byte bits)
    {
        for (var i = 0; i < 4; i++)
            if ((bits & (1 << i)) != 0)
                return i;
        return 0;
    }

    private static void RemoveEdge(Dictionary<int, byte> edges, int key, int direction) =>
        edges[key] = (byte)(edges[key] & ~(1 << direction));

    /// <summary>The direction to leave a corner by: straight on or a turn; at a corner shared by two boundaries, the right turn.</summary>
    private static int PickNext(Dictionary<int, byte> edges, int key, int incoming)
    {
        if (!edges.TryGetValue(key, out var bits) || bits == 0)
            return incoming;
        // Prefer turning right (the mask is on the right), then straight, then left: keeps diagonal pieces separate.
        foreach (var turn in Turns)
        {
            var candidate = (incoming + turn) & 3;
            if ((bits & (1 << candidate)) != 0)
                return candidate;
        }
        return LowestBit(bits);
    }

    private static Loop MakeLoop(List<VPoint> points)
    {
        double area = 0, left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            area += a.X * b.Y - b.X * a.Y;
            left = Math.Min(left, a.X);
            top = Math.Min(top, a.Y);
            right = Math.Max(right, a.X);
            bottom = Math.Max(bottom, a.Y);
        }
        return new Loop(points, area / 2) { Box = (left, top, right, bottom) };
    }

    private static bool Contains(Loop loop, VPoint p)
    {
        var (l, t, r, b) = loop.Box;
        if (p.X < l || p.X > r || p.Y < t || p.Y > b)
            return false;
        var inside = false;
        var points = loop.Points;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var (a, c) = (points[i], points[j]);
            if ((a.Y > p.Y) != (c.Y > p.Y) && p.X < (c.X - a.X) * (p.Y - a.Y) / (c.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    // ---- Simplification and straightening ----

    /// <summary>Douglas–Peucker on a closed polygon, then straightening of the nearly axis-aligned sides.</summary>
    internal static List<VPoint> Simplify(List<VPoint> corners, TraceOptions options)
    {
        var points = options.Tolerance > 0 ? DouglasPeucker(corners, options.Tolerance) : [.. corners];
        if (points.Count < 3)
            return points;
        if (options.SnapDegrees > 0)
            points = Straighten(points, options);
        return RemoveRedundant(points);
    }

    private static List<VPoint> DouglasPeucker(List<VPoint> polygon, double tolerance)
    {
        if (polygon.Count < 4)
            return [.. polygon];
        // Split the closed polygon at a point far from its center (a real corner, not a bump of the edge: those stay
        // close to the outline) and the point farthest from that one, and simplify both halves.
        double cx = polygon.Average(p => p.X), cy = polygon.Average(p => p.Y);
        var anchor = 0;
        for (var i = 1; i < polygon.Count; i++)
            if (polygon[i].DistanceSquaredTo(new VPoint(cx, cy)) > polygon[anchor].DistanceSquaredTo(new VPoint(cx, cy)))
                anchor = i;
        polygon = [.. polygon.Skip(anchor), .. polygon.Take(anchor)];
        var far = 0;
        double farDistance = -1;
        for (var i = 1; i < polygon.Count; i++)
        {
            var d = polygon[0].DistanceSquaredTo(polygon[i]);
            if (d > farDistance)
                (far, farDistance) = (i, d);
        }
        var keep = new bool[polygon.Count];
        keep[0] = keep[far] = true;
        Reduce(polygon, 0, far, tolerance, keep);
        Reduce(polygon, far, polygon.Count, tolerance, keep);
        var result = new List<VPoint>();
        for (var i = 0; i < polygon.Count; i++)
            if (keep[i])
                result.Add(polygon[i]);
        return result;
    }

    /// <summary>Marks the points between <paramref name="from"/> and <paramref name="to"/> (exclusive; to wraps to 0) worth keeping.</summary>
    private static void Reduce(List<VPoint> polygon, int from, int to, double tolerance, bool[] keep)
    {
        var stack = new Stack<(int From, int To)>();
        stack.Push((from, to));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2)
                continue;
            var (p, q) = (polygon[a], polygon[b % polygon.Count]);
            var (worst, worstDistance) = (-1, tolerance);
            for (var i = a + 1; i < b; i++)
            {
                var d = DistanceToSegment(polygon[i], p, q);
                if (d > worstDistance)
                    (worst, worstDistance) = (i, d);
            }
            if (worst < 0)
                continue;
            keep[worst] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
    }

    private static double DistanceToSegment(VPoint p, VPoint a, VPoint b)
    {
        var ab = b - a;
        var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSquared < 1e-12)
            return p.DistanceTo(a);
        var t = Math.Clamp(((p - a).X * ab.X + (p - a).Y * ab.Y) / lengthSquared, 0, 1);
        return p.DistanceTo(new VPoint(a.X + ab.X * t, a.Y + ab.Y * t));
    }

    private enum Axis { None, Horizontal, Vertical, Rising, Falling }

    /// <summary>
    /// Makes the sides near horizontal or vertical exact (at the position of their middle) and puts the corners where the
    /// straightened sides meet; a corner next to a free side only moves onto the straightened one.
    /// </summary>
    private static List<VPoint> Straighten(List<VPoint> points, TraceOptions options)
    {
        var n = points.Count;
        var axes = new Axis[n];
        var tolerance = options.SnapDegrees * Math.PI / 180;
        for (var i = 0; i < n; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % n]);
            var angle = Math.Atan2(b.Y - a.Y, b.X - a.X); // -π..π
            double Off(double target) => Math.Abs(Math.IEEERemainder(angle - target, Math.PI));
            axes[i] = Off(0) <= tolerance ? Axis.Horizontal
                : Off(Math.PI / 2) <= tolerance ? Axis.Vertical
                : options.Diagonals && Off(Math.PI / 4) <= tolerance ? Axis.Falling
                : options.Diagonals && Off(-Math.PI / 4) <= tolerance ? Axis.Rising
                : Axis.None;
        }

        // Each straightened side is a line: y = c, x = c, or x ± y = c, through the middle of the original side.
        var offsets = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % n]);
            var (mx, my) = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            offsets[i] = axes[i] switch
            {
                Axis.Horizontal => my,
                Axis.Vertical => mx,
                Axis.Falling => mx - my, // direction (1, 1): x - y is constant
                Axis.Rising => mx + my, // direction (1, -1): x + y is constant
                _ => 0,
            };
        }

        var result = new List<VPoint>(n);
        for (var i = 0; i < n; i++)
        {
            var previous = (i + n - 1) % n; // side arriving at point i; side i leaves it
            var (ax, bx) = (axes[previous], axes[i]);
            var p = points[i];
            if (ax != Axis.None && bx != Axis.None && ax != bx && Intersect(ax, offsets[previous], bx, offsets[i]) is { } corner)
                p = corner;
            else if (ax != Axis.None && bx == Axis.None)
                p = Project(p, ax, offsets[previous]);
            else if (bx != Axis.None && ax == Axis.None)
                p = Project(p, bx, offsets[i]);
            else if (ax != Axis.None && ax == bx)
                p = Project(p, ax, (offsets[previous] + offsets[i]) / 2);
            result.Add(p);
        }
        return result;
    }

    private static VPoint Project(VPoint p, Axis axis, double c) => axis switch
    {
        Axis.Horizontal => new VPoint(p.X, c),
        Axis.Vertical => new VPoint(c, p.Y),
        Axis.Falling => new VPoint(((p.X + p.Y) + c) / 2, ((p.X + p.Y) - c) / 2), // keep x + y, put x - y = c
        _ => new VPoint(((p.X - p.Y) + c) / 2, (c - (p.X - p.Y)) / 2), // keep x - y, put x + y = c
    };

    private static VPoint? Intersect(Axis a, double ca, Axis b, double cb)
    {
        // Every line is α·x + β·y = c.
        static (double A, double B) Coefficients(Axis axis) => axis switch
        {
            Axis.Horizontal => (0, 1),
            Axis.Vertical => (1, 0),
            Axis.Falling => (1, -1),
            _ => (1, 1),
        };
        var ((a1, b1), (a2, b2)) = (Coefficients(a), Coefficients(b));
        var det = a1 * b2 - a2 * b1;
        if (Math.Abs(det) < 1e-9)
            return null;
        return new VPoint((ca * b2 - cb * b1) / det, (a1 * cb - a2 * ca) / det);
    }

    /// <summary>Removes repeated points and points in the middle of a straight run.</summary>
    private static List<VPoint> RemoveRedundant(List<VPoint> points)
    {
        var list = new List<VPoint>(points);
        var changed = true;
        while (changed && list.Count >= 3)
        {
            changed = false;
            for (var i = 0; i < list.Count && list.Count >= 3; i++)
            {
                var (previous, current, next) = (list[(i + list.Count - 1) % list.Count], list[i], list[(i + 1) % list.Count]);
                var duplicate = current.DistanceSquaredTo(previous) < 1e-9;
                var straight = Math.Abs((current - previous).Cross(next - current)) < 1e-9;
                if (duplicate || straight)
                {
                    list.RemoveAt(i--);
                    changed = true;
                }
            }
        }
        return list;
    }
}

/// <summary>A point of the tracer (double precision, pixel units).</summary>
internal readonly record struct VPoint(double X, double Y)
{
    public static VPoint operator -(VPoint a, VPoint b) => new(a.X - b.X, a.Y - b.Y);
    public double Cross(VPoint o) => X * o.Y - Y * o.X;
    public double DistanceSquaredTo(VPoint o) => (X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y);
    public double DistanceTo(VPoint o) => Math.Sqrt(DistanceSquaredTo(o));
}
