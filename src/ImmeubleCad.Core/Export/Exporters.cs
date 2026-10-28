using System.Globalization;
using System.Text;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Scene;

namespace ImmeubleCad.Core.Export;

/// <summary>What to export: which levels and networks, and the unit of the written coordinates.</summary>
public sealed record ExportOptions(IReadOnlySet<Guid>? Levels = null, IReadOnlySet<Guid>? Networks = null, bool Metres = false)
{
    /// <summary>Multiplier from the model's millimetres to the written unit.</summary>
    public double Scale => Metres ? 0.001 : 1;

    /// <summary>A filtered copy: only the chosen levels (with their equipment) and networks.</summary>
    public Building Apply(Building source)
    {
        var copy = BuildingJson.Deserialize(BuildingJson.Serialize(source));
        if (Levels != null) { copy.Levels.RemoveAll(l => !Levels.Contains(l.Id)); copy.Equipment.RemoveAll(e => !Levels.Contains(e.LevelId)); }
        if (Networks != null) copy.Networks.RemoveAll(n => !Networks.Contains(n.Id));
        return copy;
    }
}

public interface IBuildingExporter
{
    string Id { get; }
    string Extension { get; }
    string Export(Building b);
    /// <summary>Filtered export with units; the default implementation filters and calls <see cref="Export(Building)"/>.</summary>
    string Export(Building b, ExportOptions options) => Export(options.Apply(b));
    /// <summary>A beginner-friendly line: which tool this format is for.</summary>
    string Purpose => "";
}

internal static class F
{
    public static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    public static Node Node(Network n, Guid id) => n.Nodes.First(x => x.Id == id);
    public static int WallHeight(Level l, Wall w) => w.HeightMm ?? l.HeightMm;
}

public sealed class ObjExporter : IBuildingExporter
{
    private double _k = 1;   // OBJ is written in metres unless millimetres are asked for
    public string Export(Building b, ExportOptions o) { _k = o.Metres ? 1 : 1000; try { return Export(o.Apply(b)); } finally { _k = 1; } }
    public string Id => "obj"; public string Extension => ".obj"; public string Purpose => "3D mesh for Blender, SketchUp, 3D printing (metres).";
    public string Export(Building b)
    {
        var sb = new StringBuilder("# ImmeubleCad\n"); var offset = 1;
        void Write(string name, Mesh m)
        {
            sb.Append("o ").AppendLine(name);
            for (var i = 0; i < m.Positions.Count; i += 3) sb.AppendLine($"v {F.N(m.Positions[i] * _k)} {F.N(m.Positions[i + 1] * _k)} {F.N(m.Positions[i + 2] * _k)}");
            for (var i = 0; i < m.Indices.Count; i += 3) sb.AppendLine($"f {m.Indices[i] + offset} {m.Indices[i + 1] + offset} {m.Indices[i + 2] + offset}");
            offset += m.VertexCount;
        }
        foreach (var l in b.Levels)
        {
            var m = new Mesh();
            foreach (var w in l.Walls)
            {
                MeshBuilder.ExtrudeWall(w.Footprint.Select(p => (p.X, p.Y)).ToList(), l.AltitudeMm, F.WallHeight(l, w), m);
                foreach (var h in w.Holes) MeshBuilder.ExtrudeSides(h.Select(p => (p.X, p.Y)).ToList(), l.AltitudeMm, F.WallHeight(l, w), m);
            }
            if (m.VertexCount > 0) Write("level_" + l.Name, m);
        }
        foreach (var n in b.Networks)
        {
            var m = new Mesh();
            foreach (var s in n.Segments)
            {
                var a = F.Node(n, s.A).Position; var c = F.Node(n, s.B).Position;
                MeshBuilder.Cylinder((a.X, a.Y, a.Z), (c.X, c.Y, c.Z), s.DiameterMm, 12, m);
            }
            if (m.VertexCount > 0) Write("network_" + n.Name, m);
        }
        return sb.ToString();
    }
}

/// <summary>Minimal ASCII DXF (R12): one layer per level walls and per network, LINE entities, mm.</summary>
public sealed class DxfExporter : IBuildingExporter
{
    private double _k = 1;
    public string Export(Building b, ExportOptions o) { _k = o.Scale; try { return Export(o.Apply(b)); } finally { _k = 1; } }
    public string Id => "dxf"; public string Extension => ".dxf"; public string Purpose => "2D drawing for AutoCAD, LibreCAD, QCAD: one layer per level and per network.";
    public string Export(Building b)
    {
        var sb = new StringBuilder((b.Site != null ? "999\nCadastre: IGN / DGFiP - Licence Ouverte 2.0\n" : "") + $"0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1009\n9\n$INSUNITS\n70\n{(_k == 1 ? 4 : 6)}\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n");
        void Line(string layer, double x1, double y1, double z1, double x2, double y2, double z2) =>
            sb.Append($"0\nLINE\n8\n{layer}\n10\n{F.N(x1 * _k)}\n20\n{F.N(y1 * _k)}\n30\n{F.N(z1 * _k)}\n11\n{F.N(x2 * _k)}\n21\n{F.N(y2 * _k)}\n31\n{F.N(z2 * _k)}\n");
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
    public string Export(Building b, ExportOptions o) => Export(o.Apply(b));   // FreeCAD's native unit is the millimetre: the macro always writes mm
    public string Id => "freecad"; public string Extension => ".FCMacro"; public string Purpose => "Macro that rebuilds editable walls and pipes in FreeCAD.";
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

/// <summary>
/// IFC4 (STEP) export: project with units and a geometric context, site → building → one storey per level, walls as extruded footprints
/// (with voids for rooms), pipe segments as extruded circles, valves/meters, radiators and boilers as extruded boxes, one distribution system and colour per network.
/// Placement: every product is placed in its storey (walls: plan coordinates, pipes: world coordinates relative to the building), lengths in millimetres.
/// </summary>
public sealed class IfcExporter : IBuildingExporter
{
    public string Id => "ifc"; public string Extension => ".ifc"; public string Purpose => "BIM exchange (Revit, ArchiCAD, FreeCAD BIM, BlenderBIM).";
    public string Export(Building b, ExportOptions o) => Export(o.Apply(b));

    private static readonly Dictionary<NetworkType, (double R, double G, double B)> Colour = new()
    {
        [NetworkType.HeatingSupply] = (0.85, 0.12, 0.12), [NetworkType.HeatingReturn] = (0.12, 0.24, 0.85), [NetworkType.DomesticHotWater] = (0.95, 0.45, 0.1),
        [NetworkType.ColdWater] = (0.1, 0.6, 0.95), [NetworkType.Drainage] = (0.4, 0.4, 0.4), [NetworkType.Gas] = (0.9, 0.8, 0.1),
    };

    public string Export(Building b)
    {
        var sb = new StringBuilder(); var id = 0;
        string E(string body) { id++; sb.AppendLine($"#{id}={body};"); return "#" + id; }
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) is var t && !t.Contains('.') ? t + "." : t;
        string Pt(double x, double y, double z) => E($"IFCCARTESIANPOINT(({N(x)},{N(y)},{N(z)}))");
        string Pt2(double x, double y) => E($"IFCCARTESIANPOINT(({N(x)},{N(y)}))");
        string Dir(double x, double y, double z) => E($"IFCDIRECTION(({N(x)},{N(y)},{N(z)}))");
        string Axis(string loc, string? axis = null, string? refDir = null) => E($"IFCAXIS2PLACEMENT3D({loc},{axis ?? "$"},{refDir ?? "$"})");
        string Place(string? rel, string axis) => E($"IFCLOCALPLACEMENT({rel ?? "$"},{axis})");

        sb.AppendLine($"ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'{(b.Site != null ? ",'Cadastre: © IGN / DGFiP - Licence Ouverte 2.0'" : "")}),'2;1');\nFILE_NAME('building.ifc','',(''),(''),'ImmeubleCad','ImmeubleCad','');\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;");
        var origin = Pt(0, 0, 0); var wcs = Axis(origin); var zDir = Dir(0, 0, 1); var xDir = Dir(1, 0, 0);
        var ctx = E($"IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,{wcs},$)");
        var length = E("IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)"); var area = E("IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.)"); var angle = E("IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.)");
        var units = E($"IFCUNITASSIGNMENT(({length},{area},{angle}))");
        var proj = E($"IFCPROJECT('{G()}',$,'{Esc(b.Name)}',$,$,$,$,({ctx}),{units})");
        var sitePl = Place(null, wcs);
        string Dms(double deg) { var sign = deg < 0 ? -1 : 1; deg = Math.Abs(deg); var d = (int)deg; var m = (int)((deg - d) * 60); var sec = (int)((deg - d - m / 60.0) * 3600); var micro = (int)Math.Round(((deg - d - m / 60.0) * 3600 - sec) * 1e6); return $"({sign * d},{sign * m},{sign * sec},{sign * micro})"; }
        var geo = "$,$,$";                                                                                        // RefLatitude, RefLongitude, RefElevation
        if (b.Site is { } siteInfo) { var (lat, lon) = Site.Lambert93.ToWgs84(siteInfo.EastM, siteInfo.NorthM); geo = $"{Dms(lat)},{Dms(lon)},{N(siteInfo.AltitudeM)}"; }
        var site = E($"IFCSITE('{G()}',$,'Site',$,$,{sitePl},$,$,.ELEMENT.,{geo},$,$)");
        if (b.Site is { } st2)
        {   // IfcMapConversion: the model frame (mm) → EPSG:2154 (metres), rotated to true north
            var metre = E("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)"); var crs = E($"IFCPROJECTEDCRS('EPSG:2154','RGF93 / Lambert-93',$,$,'Lambert-93','',{metre})");
            var r = st2.RotationDeg * Math.PI / 180; E($"IFCMAPCONVERSION({ctx},{crs},{N(st2.EastM)},{N(st2.NorthM)},{N(st2.AltitudeM)},{N(Math.Cos(r))},{N(Math.Sin(r))},0.001)");
        }
        var bldPl = Place(sitePl, wcs); var bld = E($"IFCBUILDING('{G()}',$,'{Esc(b.Name)}',$,$,{bldPl},$,$,.ELEMENT.,$,$,$)");
        E($"IFCRELAGGREGATES('{G()}',$,$,$,{proj},({site}))"); E($"IFCRELAGGREGATES('{G()}',$,$,$,{site},({bld}))");

        string ProfileOf(IReadOnlyList<PointMm> ring, IEnumerable<IReadOnlyList<PointMm>>? holes = null)
        {
            string Poly(IReadOnlyList<PointMm> r) { var pts = r.Select(p => Pt2(p.X, p.Y)).ToList(); pts.Add(pts[0]); return E($"IFCPOLYLINE(({string.Join(",", pts)}))"); }
            var outer = E($"IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,{Poly(ring)})");
            var voids = holes?.Where(h => h.Count >= 3).Select(h => E($"IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,{Poly(h)})")).ToList();
            if (voids is { Count: > 0 }) { var outerCurve = ((string)null!) ?? ""; _ = outerCurve; }
            return outer;
        }
        string Rep(string solid, double? r = null, double? g = null, double? bl = null)
        {
            if (r != null) { var col = E($"IFCCOLOURRGB($,{N(r.Value)},{N(g!.Value)},{N(bl!.Value)})"); var rend = E($"IFCSURFACESTYLERENDERING({col},$,$,$,$,$,$,$,.FLAT.)"); var style = E($"IFCSURFACESTYLE($,.BOTH.,({rend}))"); var assign = E($"IFCPRESENTATIONSTYLEASSIGNMENT(({style}))"); E($"IFCSTYLEDITEM({solid},({assign}),$)"); }
            var shape = E($"IFCSHAPEREPRESENTATION({ctx},'Body','SweptSolid',({solid}))"); return E($"IFCPRODUCTDEFINITIONSHAPE($,$,({shape}))");
        }
        string Extrude(string profile, string pos, double depth, string? dir = null) => E($"IFCEXTRUDEDAREASOLID({profile},{pos},{dir ?? zDir},{N(depth)})");

        var storeyPl = new Dictionary<Guid, string>(); var storeyRef = new Dictionary<Guid, string>(); var contained = new Dictionary<Guid, List<string>>();
        foreach (var l in b.Levels)
        {
            var pl = Place(bldPl, Axis(Pt(0, 0, l.AltitudeMm))); var st = E($"IFCBUILDINGSTOREY('{G()}',$,'{Esc(l.Name)}',$,$,{pl},$,$,.ELEMENT.,{N(l.AltitudeMm)})");
            E($"IFCRELAGGREGATES('{G()}',$,$,$,{bld},({st}))"); storeyPl[l.Id] = pl; storeyRef[l.Id] = st; contained[l.Id] = new();
            foreach (var w in l.Walls.Where(w => w.Footprint.Count >= 3))
            {
                var wallPl = Place(pl, wcs); string profile;
                if (w.Holes.Count > 0)
                {
                    string Poly(IReadOnlyList<PointMm> r) { var pts = r.Select(p => Pt2(p.X, p.Y)).ToList(); pts.Add(pts[0]); return E($"IFCPOLYLINE(({string.Join(",", pts)}))"); }
                    profile = E($"IFCARBITRARYPROFILEDEFWITHVOIDS(.AREA.,$,{Poly(w.Footprint)},({string.Join(",", w.Holes.Where(h => h.Count >= 3).Select(h => Poly(h)))}))");
                }
                else profile = ProfileOf(w.Footprint);
                var solid = Extrude(profile, wcs, w.HeightMm ?? l.HeightMm); var rep = Rep(solid, 0.78, 0.78, 0.75);
                contained[l.Id].Add(E($"IFCWALL('{G(w.Id)}',$,'{Esc(w.Label ?? "Wall")}',$,$,{wallPl},{rep},$,$)"));
            }
            foreach (var eq in b.Equipment.Where(e => e.LevelId == l.Id))
            {
                var cat = Catalog.EquipmentCatalog.Find(eq.CatalogId)?.With(eq.Parameters); var w = cat?.WidthMm ?? 500; var d = cat?.DepthMm ?? 100; var h = cat?.HeightMm ?? 500;
                var ep = Place(pl, Axis(Pt(eq.Position.X, eq.Position.Y, eq.ElevationMm), zDir, Dir(Math.Cos(eq.RotationDeg * Math.PI / 180), Math.Sin(eq.RotationDeg * Math.PI / 180), 0)));
                var prof = E($"IFCRECTANGLEPROFILEDEF(.AREA.,$,{E($"IFCAXIS2PLACEMENT2D({Pt2(w / 2.0, d / 2.0)},$)")},{N(w)},{N(d)})");
                var rep = Rep(Extrude(prof, wcs, h), 0.35, 0.35, 0.8); var name = Esc(eq.Label ?? cat?.NameIn("en") ?? eq.CatalogId);
                var kind = eq.CatalogId switch { "radiator" => "IFCSPACEHEATER", "boiler" => "IFCBOILER", "hot-water-tank" => "IFCTANK", "circulator-pump" => "IFCPUMP", _ => "IFCSANITARYTERMINAL" };
                contained[l.Id].Add(E($"{kind}('{G(eq.Id)}',$,'{name}',$,$,{ep},{rep},$,$,.NOTDEFINED.)"));
            }
        }
        foreach (var n in b.Networks)
        {
            var (cr, cg, cb) = Colour[n.Type]; var members = new List<string>();
            foreach (var s in n.Segments)
            {
                var a = n.Nodes.First(x => x.Id == s.A).Position; var c = n.Nodes.First(x => x.Id == s.B).Position; double dx = c.X - a.X, dy = c.Y - a.Y, dz = c.Z - a.Z; var len = Math.Sqrt(dx * dx + dy * dy + dz * dz); if (len < 1) continue;
                var ax = Dir(dx / len, dy / len, dz / len); var refd = Math.Abs(dz / len) > 0.9 ? Dir(1, 0, 0) : Dir(0, 0, 1);
                var host = b.Levels.LastOrDefault(l => l.AltitudeMm <= Math.Min(a.Z, c.Z)) ?? b.Levels.FirstOrDefault();
                var pp = Place(bldPl, Axis(Pt(a.X, a.Y, a.Z), ax, refd));
                var prof = E($"IFCCIRCLEPROFILEDEF(.AREA.,$,{E($"IFCAXIS2PLACEMENT2D({Pt2(0, 0)},$)")},{N(s.DiameterMm / 2.0)})");
                var rep = Rep(Extrude(prof, wcs, len), cr, cg, cb);
                var seg = E($"IFCPIPESEGMENT('{G(s.Id)}',$,'{n.Type} DN{s.DiameterMm}',$,$,{pp},{rep},$,.RIGIDSEGMENT.)"); members.Add(seg);
                if (host != null) contained[host.Id].Add(seg);
            }
            foreach (var comp in n.Nodes.Where(x => x.Component != null))
            {
                var host = b.Levels.LastOrDefault(l => l.AltitudeMm <= comp.Position.Z) ?? b.Levels.FirstOrDefault();
                var prof = E($"IFCRECTANGLEPROFILEDEF(.AREA.,$,{E($"IFCAXIS2PLACEMENT2D({Pt2(0, 0)},$)")},80.,80.)");
                var vp = Place(bldPl, Axis(Pt(comp.Position.X, comp.Position.Y, comp.Position.Z - 40)));
                var kind = comp.Component == "meter" ? "IFCFLOWMETER" : "IFCVALVE"; var rep = Rep(Extrude(prof, wcs, 80), 0.1, 0.1, 0.1);
                var v = E($"{kind}('{G(comp.Id)}',$,'{Esc(comp.Component!)}',$,$,{vp},{rep},$,$)"); members.Add(v); if (host != null) contained[host.Id].Add(v);
            }
            var sys = E($"IFCDISTRIBUTIONSYSTEM('{G(n.Id)}',$,'{Esc(n.Name)}','{n.Type}',$,.NOTDEFINED.)");
            if (members.Count > 0) E($"IFCRELASSIGNSTOGROUP('{G()}',$,$,$,({string.Join(",", members)}),$,{sys})");
        }
        foreach (var (lid, items) in contained) if (items.Count > 0) E($"IFCRELCONTAINEDINSPATIALSTRUCTURE('{G()}',$,$,$,({string.Join(",", items)}),{storeyRef[lid]})");
        return sb.AppendLine("ENDSEC;\nEND-ISO-10303-21;").ToString();
    }
    private static string Esc(string s) => s.Replace("'", "''");
    private static string G() => G(Guid.NewGuid());
    internal static string G(Guid g)
    {
        const string c = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
        var num = new System.Numerics.BigInteger(g.ToByteArray(), isUnsigned: true); var sb = new StringBuilder();
        for (var i = 0; i < 22; i++) { sb.Insert(0, c[(int)(num % 64)]); num /= 64; }
        return sb.ToString();
    }
}

/// <summary>Printable SVG plan of one level.</summary>
public static class SvgExporter
{
    public static string Export(Level l, SiteInfo? site = null)
    {
        var pts = l.Walls.SelectMany(w => w.Footprint).ToList();
        if (pts.Count == 0) return "<svg xmlns=\"http://www.w3.org/2000/svg\"/>";
        int x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{x0} {-y1} {x1 - x0} {y1 - y0}\"><title>{System.Net.WebUtility.HtmlEncode(l.Name)}</title>");
        foreach (var w in l.Walls)
            sb.Append("<path fill-rule=\"evenodd\" fill=\"#444\" stroke=\"#000\" d=\"")
              .Append(string.Join(" ", new[] { w.Footprint }.Concat(w.Holes).Select(r => "M" + string.Join(" L", r.Select(p => $"{p.X},{-p.Y}")) + " Z"))).Append("\"/>");
        if (site != null)
        {   // north arrow (rotated by the site rotation) and the data attribution
            var r = -site.RotationDeg; var w = x1 - x0; var h = y1 - y0; var size = Math.Max(w, h) * 0.08; var cx = x1 - size; var cy = -y1 + size * 1.5;
            sb.Append($"<g transform=\"translate({F.N(cx)} {F.N(cy)}) rotate({F.N(r)})\"><polygon points=\"0,{F.N(-size)} {F.N(size * 0.3)},{F.N(size * 0.4)} 0,{F.N(size * 0.2)} {F.N(-size * 0.3)},{F.N(size * 0.4)}\" fill=\"#222\"/><text x=\"0\" y=\"{F.N(-size * 1.15)}\" font-size=\"{F.N(size * 0.6)}\" text-anchor=\"middle\">N</text></g>");
            sb.Append($"<desc>Cadastre: © IGN / DGFiP - Licence Ouverte 2.0</desc>");
        }
        return sb.Append("</svg>").ToString();
    }
}

public static class Exporters
{
    public static IReadOnlyList<IBuildingExporter> All { get; } = [new ObjExporter(), new DxfExporter(), new FreeCadMacroExporter(), new IfcExporter()];
}
