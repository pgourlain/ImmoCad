using System.Text;
using ImmeubleCad.Core.Geometry;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core;

public static class Spaces
{
    /// <summary>CSV of spaces: lot numbers only, never owner names.</summary>
    public static string ToCsv(Building b)
    {
        var sb = new StringBuilder("level,kind,number,name,lot,area_m2,stability\n");
        foreach (var l in b.Levels)
            foreach (var s in l.Spaces)
                sb.AppendLine($"{Q(l.Name)},{s.Kind},{Q(s.Number)},{Q(s.Name)},{Q(s.Lot)},{SpaceFinder.AreaMm2(s.Outline) / 1e6:0.##},{s.Stability}");
        return sb.ToString();
    }
    private static string Q(string? v) => v == null ? "" : v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>Filter "Common parts only".</summary>
    public static IEnumerable<Wall> CommonWalls(Level l) => l.Walls.Where(w => w.Stability == Stability.Common);
    public static IEnumerable<Space> CommonSpaces(Level l) => l.Spaces.Where(s => s.Stability == Stability.Common);

    /// <summary>Spaces a segment crosses (its end points or midpoint lie inside), so reports can say "riser in Cave 12".</summary>
    public static IReadOnlyList<Space> CrossedBy(Building b, Network n, Segment s)
    {
        var a = n.Nodes.First(x => x.Id == s.A).Position; var c = n.Nodes.First(x => x.Id == s.B).Position;
        var zMin = Math.Min(a.Z, c.Z); var zMax = Math.Max(a.Z, c.Z);
        var pts = new[] { new PointMm(a.X, a.Y), new PointMm((a.X + c.X) / 2, (a.Y + c.Y) / 2), new PointMm(c.X, c.Y) };
        return b.Levels.Where(l => l.AltitudeMm <= zMax && l.AltitudeMm + l.HeightMm >= zMin)
            .SelectMany(l => l.Spaces).Where(sp => pts.Any(p => SpaceFinder.Contains(sp.Outline, p))).Distinct().ToList();
    }

    public static Stability DefaultFor(LevelKind level, SpaceKind kind) =>
        level == LevelKind.Basement || kind is SpaceKind.Cellar or SpaceKind.Corridor or SpaceKind.Stairwell or SpaceKind.BoilerRoom or SpaceKind.ServiceShaft or SpaceKind.BikeRoom or SpaceKind.Parking
            ? Stability.Common : Stability.Private;
}
