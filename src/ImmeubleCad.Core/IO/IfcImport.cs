using System.Globalization;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.IO;

/// <summary>
/// Minimal IFC4 STEP reader: storeys (name, elevation) and walls with a swept-solid body (arbitrary closed profile extruded upwards,
/// with optional voids). Placements are followed for translations only (enough for files written by ImmeubleCad and most simple exports).
/// </summary>
public static class IfcImport
{
    private sealed record Entity(int Id, string Type, List<object?> Args);
    private sealed record Ref(int Id);
    private sealed record Enum(string Name);

    public sealed record Result(Building Building, int Walls, IReadOnlyList<string> Warnings);

    public static Result Import(string stepText)
    {
        var entities = Parse(stepText); var warnings = new List<string>();
        var b = new Building(); var unit = LengthScale(entities);
        var storeys = entities.Values.Where(e => e.Type == "IFCBUILDINGSTOREY").OrderBy(e => Num(e.Args[9])).ToList();
        var levelOf = new Dictionary<int, Level>();
        foreach (var s in storeys)
        {
            var l = new Level { Name = Str(s.Args[2]) ?? "Level", AltitudeMm = (int)Math.Round(Num(s.Args[9]) * unit), HeightMm = 2700 }; b.Levels.Add(l); levelOf[s.Id] = l;
        }
        for (var i = 0; i + 1 < b.Levels.Count; i++) b.Levels[i].HeightMm = Math.Max(1000, b.Levels[i + 1].AltitudeMm - b.Levels[i].AltitudeMm);   // storey height = distance to the next one
        var walls = 0;
        foreach (var rel in entities.Values.Where(e => e.Type == "IFCRELCONTAINEDINSPATIALSTRUCTURE"))
        {
            if (rel.Args[5] is not Ref sref || !levelOf.TryGetValue(sref.Id, out var level)) continue;
            foreach (var item in (rel.Args[4] as List<object?>) ?? new())
            {
                if (item is not Ref r || !entities.TryGetValue(r.Id, out var e) || e.Type != "IFCWALL") continue;
                try { if (ReadWall(entities, e, unit) is { } w) { level.Walls.Add(w); walls++; } else warnings.Add($"Wall #{e.Id} has no extruded footprint: skipped"); }
                catch (Exception ex) when (ex is InvalidCastException or ArgumentOutOfRangeException or KeyNotFoundException or FormatException) { warnings.Add($"Wall #{e.Id}: {ex.Message}"); }
            }
        }
        if (storeys.Count == 0) warnings.Add("No storey found");
        var proj = entities.Values.FirstOrDefault(e => e.Type == "IFCPROJECT"); if (proj != null) b.Name = Str(proj.Args[2]) ?? "";
        return new Result(b, walls, warnings);
    }

    private static Wall? ReadWall(Dictionary<int, Entity> ents, Entity wall, double unit)
    {
        if (wall.Args[6] is not Ref pr) return null; var pds = ents[pr.Id];
        foreach (var repRef in (pds.Args[2] as List<object?>) ?? new())
        {
            var rep = ents[((Ref)repRef!).Id];
            foreach (var itemRef in (rep.Args[3] as List<object?>) ?? new())
            {
                var solid = ents[((Ref)itemRef!).Id]; if (solid.Type != "IFCEXTRUDEDAREASOLID") continue;
                var prof = ents[((Ref)solid.Args[0]!).Id]; var depth = Num(solid.Args[3]) * unit;
                var (dx, dy) = Translation(ents, wall.Args[5]); var (px, py) = Translation2(ents, solid.Args[1]);
                List<PointMm> Ring(Entity closed)
                {
                    var curve = ents[((Ref)closed.Args[2]!).Id]; var pts = new List<PointMm>();
                    foreach (var pRef in (curve.Args[0] as List<object?>) ?? new()) { var c = (List<object?>)ents[((Ref)pRef!).Id].Args[0]!; pts.Add(new((int)Math.Round((Num(c[0]) * unit) + dx + px), (int)Math.Round((Num(c[1]) * unit) + dy + py))); }
                    if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1); return pts;
                }
                var w = new Wall { HeightMm = (int)Math.Round(depth), Label = Str(wall.Args[2]) is { } n && n != "Wall" ? n : null };
                if (prof.Type == "IFCARBITRARYPROFILEDEFWITHVOIDS")
                {
                    var outerCurve = ents[((Ref)prof.Args[2]!).Id]; w.Footprint = RingFromCurve(ents, outerCurve, unit, dx + px, dy + py);
                    foreach (var h in (prof.Args[3] as List<object?>) ?? new()) w.Holes.Add(RingFromCurve(ents, ents[((Ref)h!).Id], unit, dx + px, dy + py));
                }
                else if (prof.Type == "IFCARBITRARYCLOSEDPROFILEDEF") w.Footprint = Ring(prof);
                else return null;
                return w.Footprint.Count >= 3 ? w : null;
            }
        }
        return null;
    }

    private static List<PointMm> RingFromCurve(Dictionary<int, Entity> ents, Entity curve, double unit, double dx, double dy)
    {
        var pts = new List<PointMm>();
        foreach (var pRef in (curve.Args[0] as List<object?>) ?? new()) { var c = (List<object?>)ents[((Ref)pRef!).Id].Args[0]!; pts.Add(new((int)Math.Round(Num(c[0]) * unit + dx), (int)Math.Round(Num(c[1]) * unit + dy))); }
        if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1); return pts;
    }

    /// <summary>Sum of the translations of a chain of IFCLOCALPLACEMENTs below the storey (X, Y only).</summary>
    private static (double X, double Y) Translation(Dictionary<int, Entity> ents, object? placement)
    {
        double x = 0, y = 0; var seen = 0;
        while (placement is Ref r && ents.TryGetValue(r.Id, out var pl) && pl.Type == "IFCLOCALPLACEMENT" && seen++ < 16)
        {
            var (ax, ay) = Translation2(ents, pl.Args[1]); x += ax; y += ay;
            if (pl.Args[0] is Ref rel && ents.TryGetValue(rel.Id, out var parent) && parent.Type == "IFCLOCALPLACEMENT" && ents.Values.Any(e => e.Type == "IFCBUILDINGSTOREY" && e.Args[5] is Ref sr && sr.Id == rel.Id)) break;   // stop at the storey: its own offset is the elevation
            placement = pl.Args[0];
        }
        return (x, y);
    }

    private static (double X, double Y) Translation2(Dictionary<int, Entity> ents, object? axisPlacement)
    {
        if (axisPlacement is not Ref r || !ents.TryGetValue(r.Id, out var ap) || ap.Args[0] is not Ref pr) return (0, 0);
        var c = (List<object?>)ents[pr.Id].Args[0]!; return (Num(c[0]), c.Count > 1 ? Num(c[1]) : 0);
    }

    private static double LengthScale(Dictionary<int, Entity> ents)
    {
        foreach (var u in ents.Values.Where(e => e.Type == "IFCSIUNIT"))
            if (u.Args[1] is Enum { Name: "LENGTHUNIT" }) return u.Args[2] is Enum { Name: "MILLI" } ? 1 : u.Args[2] is Enum { Name: "CENTI" } ? 10 : 1000;     // metres by default
        return 1000;
    }

    // ---- STEP tokenizer ----
    private static Dictionary<int, Entity> Parse(string text)
    {
        var res = new Dictionary<int, Entity>(); var data = text; var start = text.IndexOf("DATA;", StringComparison.Ordinal); if (start >= 0) data = text[(start + 5)..];
        var pos = 0;
        while (pos < data.Length)
        {
            var hash = data.IndexOf('#', pos); if (hash < 0) break;
            var eq = data.IndexOf('=', hash); if (eq < 0) break;
            if (!int.TryParse(data.AsSpan(hash + 1, eq - hash - 1).Trim(), out var id)) { pos = hash + 1; continue; }
            var open = data.IndexOf('(', eq); var type = data[(eq + 1)..open].Trim().ToUpperInvariant();
            var i = open; var args = ParseList(data, ref i); pos = data.IndexOf(';', i) + 1; if (pos <= 0) break;
            res[id] = new Entity(id, type, args);
        }
        return res;
    }

    private static List<object?> ParseList(string s, ref int i)
    {
        var list = new List<object?>(); i++;   // skip '('
        while (i < s.Length)
        {
            var c = s[i];
            if (c == ')') { i++; return list; }
            if (c is ',' or ' ' or '\n' or '\r' or '\t') { i++; continue; }
            if (c == '(') list.Add(ParseList(s, ref i));
            else if (c == '#') { var j = i + 1; while (j < s.Length && char.IsDigit(s[j])) j++; list.Add(new Ref(int.Parse(s.AsSpan(i + 1, j - i - 1)))); i = j; }
            else if (c == '\'') { var sb = new System.Text.StringBuilder(); i++; while (i < s.Length) { if (s[i] == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; } i++; break; } sb.Append(s[i++]); } list.Add(sb.ToString()); }
            else if (c == '.') { var j = s.IndexOf('.', i + 1); list.Add(new Enum(s[(i + 1)..j])); i = j + 1; }
            else if (c is '$' or '*') { list.Add(null); i++; }
            else { var j = i; while (j < s.Length && s[j] is not (',' or ')' or ' ')) j++; var tok = s[i..j]; if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) list.Add(d); else list.Add(tok); i = j; }
        }
        return list;
    }

    private static double Num(object? o) => o switch { double d => d, int n => n, _ => 0 };
    private static string? Str(object? o) => o as string;

    /// <summary>Imports the walls and storeys of an IFC file into a building through <see cref="BuildingActions"/> (one history step per level, undoable).</summary>
    public static Result ImportInto(BuildingActions actions, string stepText)
    {
        var r = Import(stepText);
        foreach (var l in r.Building.Levels)
        {
            var created = actions.AddLevel(l.Name, l.AltitudeMm, l.HeightMm);
            if (l.Walls.Count > 0) actions.ImportWallShapes(created.Id, l.Walls.Select(w => ((IEnumerable<PointMm>)w.Footprint, (IEnumerable<IEnumerable<PointMm>>)w.Holes.Select(h => (IEnumerable<PointMm>)h).ToList())));
        }
        return r;
    }
}
