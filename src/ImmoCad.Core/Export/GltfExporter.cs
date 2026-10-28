using System.Text;
using System.Text.Json;
using ImmoCad.Core.Model;
using ImmoCad.Scene;

namespace ImmoCad.Core.Export;

/// <summary>Binary glTF 2.0 (.glb): one node per level and per network so they can be hidden in the target tool.</summary>
public static class GltfExporter
{
    private static readonly Dictionary<NetworkType, float[]> Colors = new()
    {
        [NetworkType.HeatingSupply] = [0.85f, 0.1f, 0.1f, 1], [NetworkType.HeatingReturn] = [0.1f, 0.2f, 0.85f, 1],
        [NetworkType.DomesticHotWater] = [0.95f, 0.45f, 0.1f, 1], [NetworkType.ColdWater] = [0.1f, 0.6f, 0.95f, 1],
        [NetworkType.Drainage] = [0.4f, 0.4f, 0.4f, 1], [NetworkType.Gas] = [0.9f, 0.8f, 0.1f, 1],
    };

    public static byte[] ExportGlb(Building b)
    {
        var bin = new MemoryStream(); var meshes = new List<object>(); var nodes = new List<object>(); var accessors = new List<object>(); var views = new List<object>();
        var materials = new List<object> { new { name = "wall", pbrMetallicRoughness = new { baseColorFactor = new[] { 0.75f, 0.75f, 0.72f, 1 }, metallicFactor = 0, roughnessFactor = 0.9f } } };
        var matByType = new Dictionary<NetworkType, int>();
        int AddMesh(string name, Mesh m, int material)
        {
            // glTF is Y-up: map (x, y, z) metres Z-up → (x, z, -y)
            var pos = new float[m.VertexCount * 3];
            for (var i = 0; i < m.VertexCount; i++) { pos[i * 3] = m.Positions[i * 3]; pos[i * 3 + 1] = m.Positions[i * 3 + 2]; pos[i * 3 + 2] = -m.Positions[i * 3 + 1]; }
            Align(bin); var posOff = (int)bin.Position; foreach (var f in pos) bin.Write(BitConverter.GetBytes(f));
            Align(bin); var idxOff = (int)bin.Position; foreach (var i in m.Indices) bin.Write(BitConverter.GetBytes((uint)i));
            views.Add(new { buffer = 0, byteOffset = posOff, byteLength = pos.Length * 4, target = 34962 }); var vPos = views.Count - 1;
            views.Add(new { buffer = 0, byteOffset = idxOff, byteLength = m.Indices.Count * 4, target = 34963 }); var vIdx = views.Count - 1;
            float[] min = [float.MaxValue, float.MaxValue, float.MaxValue], max = [float.MinValue, float.MinValue, float.MinValue];
            for (var i = 0; i < pos.Length; i++) { min[i % 3] = Math.Min(min[i % 3], pos[i]); max[i % 3] = Math.Max(max[i % 3], pos[i]); }
            accessors.Add(new { bufferView = vPos, componentType = 5126, count = m.VertexCount, type = "VEC3", min, max }); var aPos = accessors.Count - 1;
            accessors.Add(new { bufferView = vIdx, componentType = 5125, count = m.Indices.Count, type = "SCALAR" }); var aIdx = accessors.Count - 1;
            meshes.Add(new { name, primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = aPos }, indices = aIdx, material } } });
            nodes.Add(new { name, mesh = meshes.Count - 1 }); return nodes.Count - 1;
        }
        var children = new List<int>();
        foreach (var l in b.Levels)
        {
            var m = new Mesh();
            foreach (var w in l.Walls)
            {
                MeshBuilder.ExtrudeWall(w.Footprint.Select(p => (p.X, p.Y)).ToList(), l.AltitudeMm, w.HeightMm ?? l.HeightMm, m);
                foreach (var h in w.Holes) MeshBuilder.ExtrudeSides(h.Select(p => (p.X, p.Y)).ToList(), l.AltitudeMm, w.HeightMm ?? l.HeightMm, m);
            }
            if (m.VertexCount > 0) children.Add(AddMesh("level_" + l.DisplayName, m, 0));
        }
        foreach (var n in b.Networks)
        {
            var m = new Mesh();
            foreach (var s in n.Segments)
            {
                var a = n.Nodes.First(x => x.Id == s.A).Position; var c = n.Nodes.First(x => x.Id == s.B).Position;
                MeshBuilder.Cylinder((a.X, a.Y, a.Z), (c.X, c.Y, c.Z), s.DiameterMm, 12, m);
            }
            if (m.VertexCount == 0) continue;
            if (!matByType.TryGetValue(n.Type, out var mi))
            { materials.Add(new { name = n.Type.ToString(), pbrMetallicRoughness = new { baseColorFactor = Colors[n.Type], metallicFactor = 0.3f, roughnessFactor = 0.5f } }); matByType[n.Type] = mi = materials.Count - 1; }
            children.Add(AddMesh("network_" + n.Name, m, mi));
        }
        Align(bin);
        nodes.Add(new { name = b.Name.Length > 0 ? b.DisplayName : "building", children }); var root = nodes.Count - 1;
        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0", generator = "ImmoCad" }, ["scene"] = 0, ["scenes"] = new[] { new { nodes = new[] { root } } },
            ["nodes"] = nodes, ["meshes"] = meshes, ["materials"] = materials, ["accessors"] = accessors, ["bufferViews"] = views,
            ["buffers"] = new[] { new { byteLength = (int)bin.Length } },
        };
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gltf));
        var jsonPad = (4 - json.Length % 4) % 4; var binBytes = bin.ToArray();
        var total = 12 + 8 + json.Length + jsonPad + 8 + binBytes.Length;
        using var o = new MemoryStream(); using var w2 = new BinaryWriter(o);
        w2.Write(0x46546C67u); w2.Write(2u); w2.Write((uint)total);
        w2.Write((uint)(json.Length + jsonPad)); w2.Write(0x4E4F534Au); w2.Write(json); for (var i = 0; i < jsonPad; i++) w2.Write((byte)0x20);
        w2.Write((uint)binBytes.Length); w2.Write(0x004E4942u); w2.Write(binBytes);
        return o.ToArray();
    }
    private static void Align(MemoryStream s) { while (s.Length % 4 != 0) s.WriteByte(0); }
}
