namespace ImmeubleCad.Scene;

public sealed class Mesh
{
    public List<float> Positions { get; } = new(); // xyz metres
    public List<int> Indices { get; } = new();
    public int VertexCount => Positions.Count / 3;
    public int Add(float x, float y, float z) { Positions.Add(x); Positions.Add(y); Positions.Add(z); return VertexCount - 1; }
    public void Quad(int a, int b, int c, int d) { Indices.AddRange([a, b, c, a, c, d]); }

    /// <summary>Deterministic checksum for pinned tests.</summary>
    public long Checksum()
    {
        long h = 1469598103934665603;
        foreach (var f in Positions) h = (h ^ (long)Math.Round(f * 1000)) * 1099511628211;
        foreach (var i in Indices) h = (h ^ i) * 1099511628211;
        return h;
    }
}

public static class MeshBuilder
{
    /// <summary>Extrude a closed footprint (mm) from z0 to z0+height (mm): sides plus fan caps (convex-safe for wall rectangles).</summary>
    public static Mesh ExtrudeWall(IReadOnlyList<(int X, int Y)> footprint, int z0Mm, int heightMm, Mesh? into = null)
    {
        var m = into ?? new Mesh(); const float k = 0.001f;
        var n = footprint.Count; var bottom = new int[n]; var top = new int[n];
        for (var i = 0; i < n; i++)
        {
            bottom[i] = m.Add(footprint[i].X * k, footprint[i].Y * k, z0Mm * k);
            top[i] = m.Add(footprint[i].X * k, footprint[i].Y * k, (z0Mm + heightMm) * k);
        }
        for (var i = 0; i < n; i++) { var j = (i + 1) % n; m.Quad(bottom[i], bottom[j], top[j], top[i]); }
        for (var i = 1; i < n - 1; i++) { m.Indices.AddRange([top[0], top[i], top[i + 1]]); m.Indices.AddRange([bottom[0], bottom[i + 1], bottom[i]]); }
        return m;
    }

    /// <summary>Box between two points along an axis (pipe approximated by a square section), mm.</summary>
    public static Mesh Pipe((int X, int Y, int Z) a, (int X, int Y, int Z) b, int diameterMm, Mesh? into = null)
    {
        var r = diameterMm / 2;
        var (x0, x1) = (Math.Min(a.X, b.X) - r, Math.Max(a.X, b.X) + r);
        var (y0, y1) = (Math.Min(a.Y, b.Y) - r, Math.Max(a.Y, b.Y) + r);
        var (z0, z1) = (Math.Min(a.Z, b.Z) - r, Math.Max(a.Z, b.Z) + r);
        return Box(x0, y0, z0, x1, y1, z1, into);
    }

    public static Mesh Box(int x0, int y0, int z0, int x1, int y1, int z1, Mesh? into = null)
    {
        var m = into ?? new Mesh(); const float k = 0.001f;
        var v = new int[8];
        for (var i = 0; i < 8; i++) v[i] = m.Add(((i & 1) == 0 ? x0 : x1) * k, ((i & 2) == 0 ? y0 : y1) * k, ((i & 4) == 0 ? z0 : z1) * k);
        m.Quad(v[0], v[1], v[3], v[2]); m.Quad(v[4], v[6], v[7], v[5]);
        m.Quad(v[0], v[4], v[5], v[1]); m.Quad(v[2], v[3], v[7], v[6]);
        m.Quad(v[0], v[2], v[6], v[4]); m.Quad(v[1], v[5], v[7], v[3]);
        return m;
    }

    /// <summary>Ray vs mesh (Möller–Trumbore). Returns nearest hit distance or null.</summary>
    public static float? Pick(Mesh m, (float X, float Y, float Z) o, (float X, float Y, float Z) d)
    {
        float? best = null; var p = m.Positions;
        for (var t = 0; t + 2 < m.Indices.Count; t += 3)
        {
            (float, float, float) V(int idx) => (p[m.Indices[idx] * 3], p[m.Indices[idx] * 3 + 1], p[m.Indices[idx] * 3 + 2]);
            var (v0, v1, v2) = (V(t), V(t + 1), V(t + 2));
            var e1 = (v1.Item1 - v0.Item1, v1.Item2 - v0.Item2, v1.Item3 - v0.Item3);
            var e2 = (v2.Item1 - v0.Item1, v2.Item2 - v0.Item2, v2.Item3 - v0.Item3);
            var h = (d.Y * e2.Item3 - d.Z * e2.Item2, d.Z * e2.Item1 - d.X * e2.Item3, d.X * e2.Item2 - d.Y * e2.Item1);
            var a = e1.Item1 * h.Item1 + e1.Item2 * h.Item2 + e1.Item3 * h.Item3;
            if (Math.Abs(a) < 1e-9f) continue;
            var f = 1 / a; var s = (o.X - v0.Item1, o.Y - v0.Item2, o.Z - v0.Item3);
            var u = f * (s.Item1 * h.Item1 + s.Item2 * h.Item2 + s.Item3 * h.Item3);
            if (u < 0 || u > 1) continue;
            var q = (s.Item2 * e1.Item3 - s.Item3 * e1.Item2, s.Item3 * e1.Item1 - s.Item1 * e1.Item3, s.Item1 * e1.Item2 - s.Item2 * e1.Item1);
            var v = f * (d.X * q.Item1 + d.Y * q.Item2 + d.Z * q.Item3);
            if (v < 0 || u + v > 1) continue;
            var dist = f * (e2.Item1 * q.Item1 + e2.Item2 * q.Item2 + e2.Item3 * q.Item3);
            if (dist > 1e-6f && (best == null || dist < best)) best = dist;
        }
        return best;
    }
}

public static class WireEdges
{
    /// <summary>Unique triangle edges of a mesh as index pairs (for wireframe).</summary>
    public static List<(int A, int B)> Of(Mesh m)
    {
        var set = new HashSet<(int, int)>();
        for (var i = 0; i + 2 < m.Indices.Count; i += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = m.Indices[i + k], b = m.Indices[i + (k + 1) % 3];
                set.Add(a < b ? (a, b) : (b, a));
            }
        return set.ToList();
    }
}
