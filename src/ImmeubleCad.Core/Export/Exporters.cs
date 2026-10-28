using System.Globalization;
using System.Text;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Scene;

namespace ImmeubleCad.Core.Export;

public interface IBuildingExporter
{
    string Id { get; }
    string Extension { get; }
    string Export(Building b);
}

internal static class F
{
    public static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    public static Node Node(Network n, Guid id) => n.Nodes.First(x => x.Id == id);
    public static int WallHeight(Level l, Wall w) => w.HeightMm ?? l.HeightMm;
}

public sealed class ObjExporter : IBuildingExporter
{
    public string Id => "obj"; public string Extension => ".obj";
    public string Export(Building b)
    {
        var sb = new StringBuilder("# ImmeubleCad\n"); var offset = 1;
        void Write(string name, Mesh m)
        {
            sb.Append("o ").AppendLine(name);
            for (var i = 0; i < m.Positions.Count; i += 3) sb.AppendLine($"v {F.N(m.Positions[i])} {F.N(m.Positions[i + 1])} {F.N(m.Positions[i + 2])}");
            for (var i = 0; i < m.Indices.Count; i += 3) sb.AppendLine($"f {m.Indices[i] + offset} {m.Indices[i + 1] + offset} {m.Indices[i + 2] + offset}");
            offset += m.VertexCount;
        }
        foreach (var l in b.Levels)
        {
            var m = new Mesh();
            foreach (var w in l.Walls) MeshBuilder.ExtrudeWall(w.Footprint.Select(p => (p.X, p.Y)).ToList(), l.AltitudeMm, F.WallHeight(l, w), m);
            if (m.VertexCount > 0) Write("level_" + l.Name, m);
        }
        foreach (var n in b.Networks)
        {
            var m = new Mesh();
            foreach (var s in n.Segments)
            {
                var a = F.Node(n, s.A).Position; var c = F.Node(n, s.B).Position;
                MeshBuilder.Pipe((a.X, a.Y, a.Z), (c.X, c.Y, c.Z), s.DiameterMm, m);
            }
            if (m.VertexCount > 0) Write("network_" + n.Name, m);
        }
        return sb.ToString();
    }
}

/// <summary>Minimal ASCII DXF (R12): one layer per level walls and per network, LINE entities, mm.</summary>
public sealed class DxfExporter : IBuildingExporter
{
    public string Id => "dxf"; public string Extension => ".dxf";
    public string Export(Building b)
    {
        var sb = new StringBuilder("0\nSECTION\n2\nENTITIES\n");
        void Line(string layer, double x1, double y1, double z1, double x2, double y2, double z2) =>
            sb.Append($"0\nLINE\n8\n{layer}\n10\n{F.N(x1)}\n20\n{F.N(y1)}\n30\n{F.N(z1)}\n11\n{F.N(x2)}\n21\n{F.N(y2)}\n31\n{F.N(z2)}\n");
        foreach (var l in b.Levels)
            foreach (var w in l.Walls)
                for (var i = 0; i < w.Footprint.Count; i++)
                {
                    var p = w.Footprint[i]; var q = w.Footprint[(i + 1) % w.Footprint.Count];
                    Line("WALLS_" + l.Name, p.X, p.Y, l.AltitudeMm, q.X, q.Y, l.AltitudeMm);
                }
        foreach (var n in b.Networks)
            foreach (var s in n.Segments)
            {
                var a = F.Node(n, s.A).Position; var c = F.Node(n, s.B).Position;
                Line("NET_" + n.Type, a.X, a.Y, a.Z, c.X, c.Y, c.Z);
            }
        return sb.Append("0\nENDSEC\n0\nEOF\n").ToString();
    }
}

/// <summary>FreeCAD macro rebuilding walls (extruded footprints) and pipes (cylinders), one group per network.</summary>
public sealed class FreeCadMacroExporter : IBuildingExporter
{
    public string Id => "freecad"; public string Extension => ".FCMacro";
    public string Export(Building b)
    {
        var sb = new StringBuilder("import FreeCAD, Part\nfrom FreeCAD import Vector as V\ndoc = FreeCAD.newDocument('Immeuble')\n");
        foreach (var l in b.Levels)
        {
            sb.AppendLine($"# level {l.Name}");
            foreach (var w in l.Walls)
            {
                var pts = string.Join(", ", w.Footprint.Select(p => $"V({p.X},{p.Y},{l.AltitudeMm})"));
                sb.AppendLine($"w = Part.Face(Part.makePolygon([{pts}, V({w.Footprint[0].X},{w.Footprint[0].Y},{l.AltitudeMm})])).extrude(V(0,0,{F.WallHeight(l, w)}))");
                sb.AppendLine($"doc.addObject('Part::Feature','Wall').Shape = w");
            }
        }
        foreach (var n in b.Networks)
        {
            sb.AppendLine($"g = doc.addObject('App::DocumentObjectGroup','{n.Name}_{n.Type}')");
            foreach (var s in n.Segments)
            {
                var a = F.Node(n, s.A).Position; var c = F.Node(n, s.B).Position;
                sb.AppendLine($"d = V({c.X - a.X},{c.Y - a.Y},{c.Z - a.Z}); o = doc.addObject('Part::Feature','Pipe'); o.Shape = Part.makeCylinder({s.DiameterMm / 2.0}, d.Length, V({a.X},{a.Y},{a.Z}), d); g.addObject(o)");
            }
        }
        return sb.AppendLine("doc.recompute()").ToString();
    }
}

/// <summary>Minimal IFC4 (STEP) skeleton: project, site, building, one storey per level, IfcWall / IfcPipeSegment entities.</summary>
public sealed class IfcExporter : IBuildingExporter
{
    public string Id => "ifc"; public string Extension => ".ifc";
    public string Export(Building b)
    {
        var sb = new StringBuilder();
        var id = 0;
        string E(string body) { id++; sb.AppendLine($"#{id}={body};"); return "#" + id; }
        sb.AppendLine("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\nFILE_NAME('building.ifc','',(''),(''),'ImmeubleCad','ImmeubleCad','');\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;");
        var proj = E("IFCPROJECT('" + G() + "',$,'" + Esc(b.Name) + "',$,$,$,$,$,$)");
        var site = E("IFCSITE('" + G() + "',$,'Site',$,$,$,$,$,.ELEMENT.,$,$,$,$,$)");
        var bld = E("IFCBUILDING('" + G() + "',$,'" + Esc(b.Name) + "',$,$,$,$,$,.ELEMENT.,$,$,$)");
        E($"IFCRELAGGREGATES('{G()}',$,$,$,{proj},({site}))");
        E($"IFCRELAGGREGATES('{G()}',$,$,$,{site},({bld}))");
        foreach (var l in b.Levels)
        {
            var st = E($"IFCBUILDINGSTOREY('{G()}',$,'{Esc(l.Name)}',$,$,$,$,$,.ELEMENT.,{F.N(l.AltitudeMm)})");
            E($"IFCRELAGGREGATES('{G()}',$,$,$,{bld},({st}))");
            var walls = l.Walls.Select(w => E($"IFCWALL('{G(w.Id)}',$,'{Esc(w.Label ?? "Wall")}',$,$,$,$,$,$)")).ToList();
            if (walls.Count > 0) E($"IFCRELCONTAINEDINSPATIALSTRUCTURE('{G()}',$,$,$,({string.Join(",", walls)}),{st})");
        }
        foreach (var n in b.Networks)
        {
            var segs = n.Segments.Select(s => E($"IFCPIPESEGMENT('{G(s.Id)}',$,'{n.Type} DN{s.DiameterMm}',$,$,$,$,$,.RIGIDSEGMENT.)")).ToList();
            var sys = E($"IFCDISTRIBUTIONSYSTEM('{G(n.Id)}',$,'{Esc(n.Name)}',$,$,.NOTDEFINED.)");
            if (segs.Count > 0) E($"IFCRELASSIGNSTOGROUP('{G()}',$,$,$,({string.Join(",", segs)}),$,{sys})");
        }
        return sb.AppendLine("ENDSEC;\nEND-ISO-10303-21;").ToString();
    }
    private static string Esc(string s) => s.Replace("'", "''");
    // IFC GlobalId: 22 chars base64-ish
    private static string G() => G(Guid.NewGuid());
    private static string G(Guid g)
    {
        const string c = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
        var bytes = g.ToByteArray(); var num = new System.Numerics.BigInteger(bytes, isUnsigned: true);
        var sb = new StringBuilder();
        for (var i = 0; i < 22; i++) { sb.Insert(0, c[(int)(num % 64)]); num /= 64; }
        return sb.ToString();
    }
}

/// <summary>Printable SVG plan of one level.</summary>
public static class SvgExporter
{
    public static string Export(Level l)
    {
        var pts = l.Walls.SelectMany(w => w.Footprint).ToList();
        if (pts.Count == 0) return "<svg xmlns=\"http://www.w3.org/2000/svg\"/>";
        int x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{x0} {-y1} {x1 - x0} {y1 - y0}\"><title>{System.Net.WebUtility.HtmlEncode(l.Name)}</title>");
        foreach (var w in l.Walls)
            sb.Append("<polygon fill=\"#444\" stroke=\"#000\" points=\"").Append(string.Join(" ", w.Footprint.Select(p => $"{p.X},{-p.Y}"))).Append("\"/>");
        return sb.Append("</svg>").ToString();
    }
}

public static class Exporters
{
    public static IReadOnlyList<IBuildingExporter> All { get; } = [new ObjExporter(), new DxfExporter(), new FreeCadMacroExporter(), new IfcExporter()];
}
