namespace ImmoCad.Scene;

public sealed class Mesh
{
    public List<float> Positions { get; } = new(); // xyz metres
    public List<int> Indices { get; } = new();
    public List<float> Normals { get; } = new();   // filled by ComputeNormals()
    public int VertexCount => Positions.Count / 3;

    /// <summary>Flat-ish normals: each vertex gets the average of the faces that share it.</summary>
    public void ComputeNormals()
    {
        Normals.Clear(); for (var i = 0; i < Positions.Count; i++) Normals.Add(0);
        for (var t = 0; t + 2 < Indices.Count; t += 3)
        {
            int a = Indices[t], b = Indices[t + 1], c = Indices[t + 2];
            float ux = Positions[b * 3] - Positions[a * 3], uy = Positions[b * 3 + 1] - Positions[a * 3 + 1], uz = Positions[b * 3 + 2] - Positions[a * 3 + 2];
            float vx = Positions[c * 3] - Positions[a * 3], vy = Positions[c * 3 + 1] - Positions[a * 3 + 1], vz = Positions[c * 3 + 2] - Positions[a * 3 + 2];
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (var i in new[] { a, b, c }) { Normals[i * 3] += nx; Normals[i * 3 + 1] += ny; Normals[i * 3 + 2] += nz; }
        }
        for (var i = 0; i < VertexCount; i++)
        {
            var l = MathF.Sqrt(Normals[i * 3] * Normals[i * 3] + Normals[i * 3 + 1] * Normals[i * 3 + 1] + Normals[i * 3 + 2] * Normals[i * 3 + 2]);
            if (l > 1e-12f) { Normals[i * 3] /= l; Normals[i * 3 + 1] /= l; Normals[i * 3 + 2] /= l; }
        }
    }

    public (float X, float Y, float Z) Min() => (Enumerable.Range(0, VertexCount).Min(i => Positions[i * 3]), Enumerable.Range(0, VertexCount).Min(i => Positions[i * 3 + 1]), Enumerable.Range(0, VertexCount).Min(i => Positions[i * 3 + 2]));
    public (float X, float Y, float Z) Max() => (Enumerable.Range(0, VertexCount).Max(i => Positions[i * 3]), Enumerable.Range(0, VertexCount).Max(i => Positions[i * 3 + 1]), Enumerable.Range(0, VertexCount).Max(i => Positions[i * 3 + 2]));
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
    /// <summary>Extrude a closed footprint (mm) from z0 to z0+height (mm): sides plus triangulated caps (concave outlines and holes are handled).</summary>
    public static Mesh ExtrudeWall(IReadOnlyList<(int X, int Y)> footprint, int z0Mm, int heightMm, Mesh? into = null, IReadOnlyList<IReadOnlyList<(int X, int Y)>>? holes = null)
    {
        var m = into ?? new Mesh(); const float k = 0.001f;
        if (footprint.Count < 3) return m;
        if (Triangulator.Area2(footprint) < 0) footprint = footprint.Reverse().ToList();   // counter-clockwise: sides and caps face outwards
        var n = footprint.Count; var bottom = new int[n]; var top = new int[n];
        for (var i = 0; i < n; i++)
        {
            bottom[i] = m.Add(footprint[i].X * k, footprint[i].Y * k, z0Mm * k);
            top[i] = m.Add(footprint[i].X * k, footprint[i].Y * k, (z0Mm + heightMm) * k);
        }
        for (var i = 0; i < n; i++) { var j = (i + 1) % n; m.Quad(bottom[i], bottom[j], top[j], top[i]); }
        var (pts, tris) = Triangulator.Triangulate(footprint, holes);   // caps get their own vertices (flat shading)
        var capTop = new int[pts.Count]; var capBottom = new int[pts.Count];
        for (var i = 0; i < pts.Count; i++) { capTop[i] = m.Add(pts[i].X * k, pts[i].Y * k, (z0Mm + heightMm) * k); capBottom[i] = m.Add(pts[i].X * k, pts[i].Y * k, z0Mm * k); }
        for (var t = 0; t + 2 < tris.Count; t += 3) { m.Indices.AddRange([capTop[tris[t]], capTop[tris[t + 1]], capTop[tris[t + 2]]]); m.Indices.AddRange([capBottom[tris[t]], capBottom[tris[t + 2]], capBottom[tris[t + 1]]]); }
        return m;
    }

    /// <summary>Inner faces of a hole (room) of a wall group: sides only, no caps; they face into the room.</summary>
    public static Mesh ExtrudeSides(IReadOnlyList<(int X, int Y)> ring, int z0Mm, int heightMm, Mesh? into = null)
    {
        var m = into ?? new Mesh(); const float k = 0.001f;
        if (Triangulator.Area2(ring) > 0) ring = ring.Reverse().ToList();   // clockwise: the sides look towards the inside of the hole
        var n = ring.Count; var b = new int[n]; var t = new int[n];
        for (var i = 0; i < n; i++) { b[i] = m.Add(ring[i].X * k, ring[i].Y * k, z0Mm * k); t[i] = m.Add(ring[i].X * k, ring[i].Y * k, (z0Mm + heightMm) * k); }
        for (var i = 0; i < n; i++) { var j = (i + 1) % n; m.Quad(b[i], b[j], t[j], t[i]); }
        return m;
    }

    /// <summary>Round pipe between two points (mm): <paramref name="segments"/>-sided prism with end caps.</summary>
    public static Mesh Cylinder((int X, int Y, int Z) a, (int X, int Y, int Z) b, int diameterMm, int segments = 12, Mesh? into = null)
    {
        var m = into ?? new Mesh(); const float k = 0.001f;
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z; var len = Math.Sqrt(dx * dx + dy * dy + dz * dz); if (len < 1e-9) return m;
        double ux = dx / len, uy = dy / len, uz = dz / len;
        // any vector not parallel to the axis → orthonormal basis (p, q)
        var (rx, ry, rz) = Math.Abs(uz) < 0.9 ? (0.0, 0.0, 1.0) : (1.0, 0.0, 0.0);
        double px = uy * rz - uz * ry, py = uz * rx - ux * rz, pz = ux * ry - uy * rx; var pl = Math.Sqrt(px * px + py * py + pz * pz); px /= pl; py /= pl; pz /= pl;
        double qx = uy * pz - uz * py, qy = uz * px - ux * pz, qz = ux * py - uy * px;
        var r = diameterMm / 2.0; var ring0 = new int[segments]; var ring1 = new int[segments];
        for (var i = 0; i < segments; i++)
        {
            var t = 2 * Math.PI * i / segments; double cx = Math.Cos(t) * r, cy = Math.Sin(t) * r;
            double ox = px * cx + qx * cy, oy = py * cx + qy * cy, oz = pz * cx + qz * cy;
            ring0[i] = m.Add((float)((a.X + ox) * k), (float)((a.Y + oy) * k), (float)((a.Z + oz) * k));
            ring1[i] = m.Add((float)((b.X + ox) * k), (float)((b.Y + oy) * k), (float)((b.Z + oz) * k));
        }
        for (var i = 0; i < segments; i++) { var j = (i + 1) % segments; m.Quad(ring0[i], ring0[j], ring1[j], ring1[i]); }
        var c0 = m.Add(a.X * k, a.Y * k, a.Z * k); var c1 = m.Add(b.X * k, b.Y * k, b.Z * k);
        for (var i = 0; i < segments; i++) { var j = (i + 1) % segments; m.Indices.AddRange([c0, ring0[j], ring0[i]]); m.Indices.AddRange([c1, ring1[i], ring1[j]]); }
        return m;
    }

    /// <summary>Elbow: a quarter-torus-like bend approximated by short cylinders around the corner (corner = node, arms along the two pipe axes).</summary>
    public static Mesh Elbow((int X, int Y, int Z) corner, (int X, int Y, int Z) armA, (int X, int Y, int Z) armB, int diameterMm, Mesh? into = null)
    {
        var m = into ?? new Mesh(); var r = Math.Max(diameterMm, 20);
        (int, int, int) Toward((int X, int Y, int Z) to) { double dx = to.X - corner.X, dy = to.Y - corner.Y, dz = to.Z - corner.Z; var l = Math.Sqrt(dx * dx + dy * dy + dz * dz); return l < 1 ? (0, 0, 0) : ((int)(dx / l * r), (int)(dy / l * r), (int)(dz / l * r)); }
        var (ax, ay, az) = Toward(armA); var (bx, by, bz) = Toward(armB);
        var pa = (corner.X + ax, corner.Y + ay, corner.Z + az); var pb = (corner.X + bx, corner.Y + by, corner.Z + bz);
        var bulge = ((pa.Item1 + pb.Item1) / 4 + corner.X / 2, (pa.Item2 + pb.Item2) / 4 + corner.Y / 2, (pa.Item3 + pb.Item3) / 4 + corner.Z / 2);   // halfway between the corner and the middle of the two arm ends
        Cylinder(pa, ((pa.Item1 + bulge.Item1) / 2, (pa.Item2 + bulge.Item2) / 2, (pa.Item3 + bulge.Item3) / 2), diameterMm, 10, m);
        Cylinder(((pa.Item1 + bulge.Item1) / 2, (pa.Item2 + bulge.Item2) / 2, (pa.Item3 + bulge.Item3) / 2), ((pb.Item1 + bulge.Item1) / 2, (pb.Item2 + bulge.Item2) / 2, (pb.Item3 + bulge.Item3) / 2), diameterMm, 10, m);
        Cylinder(((pb.Item1 + bulge.Item1) / 2, (pb.Item2 + bulge.Item2) / 2, (pb.Item3 + bulge.Item3) / 2), pb, diameterMm, 10, m);
        return m;
    }

    /// <summary>Floor slab: a thin box under the level over the bounding rectangle of a polygon (mm).</summary>
    public static Mesh Slab(IReadOnlyList<(int X, int Y)> outline, int topZMm, int thicknessMm = 200, Mesh? into = null) =>
        Box(outline.Min(p => p.X), outline.Min(p => p.Y), topZMm - thicknessMm, outline.Max(p => p.X), outline.Max(p => p.Y), topZMm, into);

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

    /// <summary>
    /// Edges worth drawing in wireframe: the ones with a single triangle (boundary of a face) or whose two triangles are not coplanar (a real crease).
    /// The diagonals a triangulation adds inside a flat face (fans across slabs and wall caps) are left out.
    /// </summary>
    public static List<(int A, int B)> Outline(Mesh m)
    {
        var faces = new Dictionary<(int, int), (int Count, (double X, double Y, double Z) N)>();
        for (var i = 0; i + 2 < m.Indices.Count; i += 3)
        {
            int i0 = m.Indices[i], i1 = m.Indices[i + 1], i2 = m.Indices[i + 2];
            double ux = m.Positions[i1 * 3] - m.Positions[i0 * 3], uy = m.Positions[i1 * 3 + 1] - m.Positions[i0 * 3 + 1], uz = m.Positions[i1 * 3 + 2] - m.Positions[i0 * 3 + 2];
            double vx = m.Positions[i2 * 3] - m.Positions[i0 * 3], vy = m.Positions[i2 * 3 + 1] - m.Positions[i0 * 3 + 1], vz = m.Positions[i2 * 3 + 2] - m.Positions[i0 * 3 + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            var n = l < 1e-12 ? (0.0, 0.0, 0.0) : (nx / l, ny / l, nz / l);
            for (var k = 0; k < 3; k++)
            {
                int a = m.Indices[i + k], b = m.Indices[i + (k + 1) % 3]; var key = a < b ? (a, b) : (b, a);
                faces[key] = faces.TryGetValue(key, out var f) ? (f.Count + 1, f.Count == 1 && Math.Abs(f.N.X * n.Item1 + f.N.Y * n.Item2 + f.N.Z * n.Item3) < 0.9999 ? (0, 0, 0) : f.N) : (1, n);
            }
        }
        return faces.Where(e => e.Value.Count == 1 || e.Value.Count > 2 || e.Value.N == (0.0, 0.0, 0.0)).Select(e => e.Key).ToList();
    }
}
