using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.Networks;

public sealed record SegmentRef(Network Network, Segment Segment, PointMm3 A, PointMm3 B);

/// <summary>Read-only helpers the views use to draw and hit-test networks on a level.</summary>
public static class NetworkQueries
{
    public static IEnumerable<SegmentRef> All(Building b) =>
        b.Networks.SelectMany(n => n.Segments.Select(s => new SegmentRef(n, s, n.Nodes.First(x => x.Id == s.A).Position, n.Nodes.First(x => x.Id == s.B).Position)));

    public static bool IsVertical(SegmentRef s) => s.A.X == s.B.X && s.A.Y == s.B.Y && s.A.Z != s.B.Z;

    /// <summary>Vertical segments that cross or touch the level (risers drawn as circles in plan).</summary>
    public static IEnumerable<SegmentRef> RisersAt(Building b, Level l) =>
        All(b).Where(s => IsVertical(s) && Math.Min(s.A.Z, s.B.Z) < l.AltitudeMm + l.HeightMm && Math.Max(s.A.Z, s.B.Z) >= l.AltitudeMm);

    /// <summary>Horizontal segments lying in the level's height range.</summary>
    public static IEnumerable<SegmentRef> HorizontalAt(Building b, Level l) =>
        All(b).Where(s => !IsVertical(s) && s.A.Z == s.B.Z && s.A.Z >= l.AltitudeMm && s.A.Z < l.AltitudeMm + l.HeightMm);

    /// <summary>The next level a riser reaches above/below the given level (for the up/down arrow), or null.</summary>
    public static Level? NextLevelReached(Building b, SegmentRef riser, Level from, int direction)
    {
        var zMin = Math.Min(riser.A.Z, riser.B.Z); var zMax = Math.Max(riser.A.Z, riser.B.Z);
        var i = b.Levels.FindIndex(l => l.Id == from.Id) + direction;
        if (i < 0 || i >= b.Levels.Count) return null;
        var n = b.Levels[i];
        return direction > 0 ? (zMax >= n.AltitudeMm ? n : null) : (zMin < n.AltitudeMm + n.HeightMm ? n : null);
    }

    public static (SegmentRef Segment, PointMm3 Point, double Distance)? Nearest(Building b, PointMm3 p, int maxDistanceMm, Func<SegmentRef, bool>? filter = null)
    {
        (SegmentRef, PointMm3, double)? best = null;
        foreach (var s in All(b).Where(s => filter?.Invoke(s) ?? true))
        {
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, dz = s.B.Z - s.A.Z; var l2 = dx * dx + dy * dy + dz * dz;
            var t = l2 == 0 ? 0 : Math.Clamp(((p.X - s.A.X) * dx + (p.Y - s.A.Y) * dy + (p.Z - s.A.Z) * dz) / l2, 0, 1);
            var q = new PointMm3((int)Math.Round(s.A.X + t * dx), (int)Math.Round(s.A.Y + t * dy), (int)Math.Round(s.A.Z + t * dz));
            var d = Math.Sqrt(Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2) + Math.Pow(q.Z - p.Z, 2));
            if (d <= maxDistanceMm && (best == null || d < best.Value.Item3)) best = (s, q, d);
        }
        return best;
    }

    public static Rgb ColorOf(NetworkType t) => t switch
    {
        NetworkType.HeatingSupply => new(0xD9, 0x1E, 0x1E), NetworkType.HeatingReturn => new(0x1E, 0x3C, 0xD9), NetworkType.DomesticHotWater => new(0xF2, 0x73, 0x1A),
        NetworkType.ColdWater => new(0x1A, 0x99, 0xF2), NetworkType.Drainage => new(0x66, 0x66, 0x66), _ => new(0xE6, 0xCC, 0x1A),
    };
    public readonly record struct Rgb(byte R, byte G, byte B);

    public static int DefaultDiameter(NetworkType t) => t switch { NetworkType.Drainage => 100, NetworkType.HeatingSupply or NetworkType.HeatingReturn => 22, NetworkType.Gas => 22, _ => 16 };
    public static string DefaultMaterial(NetworkType t) => t switch { NetworkType.Drainage => "PVC", NetworkType.Gas => "steel", _ => "copper" };
}
