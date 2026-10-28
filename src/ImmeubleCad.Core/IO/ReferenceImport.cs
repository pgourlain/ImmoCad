using System.Globalization;
using System.Text.Json;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.IO;

/// <summary>Imports OBJ and glTF/GLB meshes as locked reference meshes (millimetres, Z up) and slices them for tracing.</summary>
public static class ReferenceImport
{
    /// <summary>OBJ: vertices are in <paramref name="unitToMm"/> units (1000 for metres, 1 for millimetres). Y-up files can be converted with <paramref name="yUp"/>.</summary>
    public static ReferenceMesh FromObj(string name, string text, double unitToMm = 1000, bool yUp = false)
    {
        var m = new ReferenceMesh { Name = name };
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("v ")) { var p = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); Add(m, p[0], p[1], p[2], unitToMm, yUp); }
            else if (line.StartsWith("f "))
            {
                var idx = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => { var i = int.Parse(t.Split('/')[0]); return i < 0 ? m.Positions.Count / 3 + i : i - 1; }).ToList();
                for (var k = 1; k + 1 < idx.Count; k++) { m.Indices.Add(idx[0]); m.Indices.Add(idx[k]); m.Indices.Add(idx[k + 1]); }          // fan-triangulate polygons
            }
        }
        return m;
    }

    private static void Add(ReferenceMesh m, double x, double y, double z, double k, bool yUp)
    {
        if (yUp) (y, z) = (-z, y);                                       // glTF/Y-up → Z-up
        m.Positions.Add((int)Math.Round(x * k)); m.Positions.Add((int)Math.Round(y * k)); m.Positions.Add((int)Math.Round(z * k));
    }

    /// <summary>Binary glTF 2.0: every mesh primitive with POSITION (float32 VEC3) and optional indices (uint8/16/32); node transforms are ignored except translation.</summary>
    public static ReferenceMesh FromGlb(string name, byte[] glb)
    {
        if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67u) throw new InvalidDataException("Not a GLB file");
        var jsonLen = (int)BitConverter.ToUInt32(glb, 12); using var doc = JsonDocument.Parse(glb.AsMemory(20, jsonLen)); var root = doc.RootElement;
        var binStart = 20 + jsonLen + 8; var m = new ReferenceMesh { Name = name };
        var views = root.GetProperty("bufferViews").EnumerateArray().ToList(); var accessors = root.GetProperty("accessors").EnumerateArray().ToList();
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                var pos = accessors[prim.GetProperty("attributes").GetProperty("POSITION").GetInt32()]; var pv = views[pos.GetProperty("bufferView").GetInt32()];
                var off = binStart + (pv.TryGetProperty("byteOffset", out var po) ? po.GetInt32() : 0) + (pos.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0); var count = pos.GetProperty("count").GetInt32(); var baseIndex = m.Positions.Count / 3;
                for (var i = 0; i < count; i++) Add(m, BitConverter.ToSingle(glb, off + i * 12), BitConverter.ToSingle(glb, off + i * 12 + 4), BitConverter.ToSingle(glb, off + i * 12 + 8), 1000, true);
                if (!prim.TryGetProperty("indices", out var ii)) { for (var i = 0; i + 2 < count; i += 3) { m.Indices.Add(baseIndex + i); m.Indices.Add(baseIndex + i + 1); m.Indices.Add(baseIndex + i + 2); } continue; }
                var ia = accessors[ii.GetInt32()]; var iv = views[ia.GetProperty("bufferView").GetInt32()]; var ioff = binStart + (iv.TryGetProperty("byteOffset", out var io) ? io.GetInt32() : 0) + (ia.TryGetProperty("byteOffset", out var iao) ? iao.GetInt32() : 0);
                var ic = ia.GetProperty("count").GetInt32(); var ct = ia.GetProperty("componentType").GetInt32();
                for (var i = 0; i < ic; i++) m.Indices.Add(baseIndex + (ct == 5125 ? (int)BitConverter.ToUInt32(glb, ioff + i * 4) : ct == 5123 ? BitConverter.ToUInt16(glb, ioff + i * 2) : glb[ioff + i]));
            }
        return m;
    }

    public static ReferenceMesh FromFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".obj" => FromObj(Path.GetFileName(path), File.ReadAllText(path), yUp: false),
        ".glb" => FromGlb(Path.GetFileName(path), File.ReadAllBytes(path)),
        _ => throw new NotSupportedException("Reference models: .obj or .glb"),
    };

    /// <summary>Section of the mesh at altitude <paramref name="zMm"/>: the line segments where triangles cross that horizontal plane.</summary>
    public static List<(PointMm A, PointMm B)> Slice(ReferenceMesh m, int zMm)
    {
        var res = new List<(PointMm, PointMm)>();
        for (var t = 0; t + 2 < m.Indices.Count; t += 3)
        {
            var v = new (double X, double Y, double Z)[3];
            for (var k = 0; k < 3; k++) { var i = m.Indices[t + k] * 3; v[k] = (m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]); }
            var pts = new List<PointMm>();
            for (var k = 0; k < 3; k++)
            {
                var a = v[k]; var b = v[(k + 1) % 3]; if ((a.Z - zMm) * (b.Z - zMm) > 0 || a.Z == b.Z) continue;
                var f = (zMm - a.Z) / (b.Z - a.Z); pts.Add(new((int)Math.Round(a.X + (b.X - a.X) * f), (int)Math.Round(a.Y + (b.Y - a.Y) * f)));
            }
            if (pts.Count >= 2 && pts[0] != pts[1]) res.Add((pts[0], pts[1]));
        }
        return res;
    }
}
