using System.Text.Json.Serialization;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.Catalog;

public sealed record Port(NetworkType Network, int DiameterMm, int OffsetX, int OffsetY, int OffsetZ);
public sealed record CatalogEntry(string Id, string Category, string Name, int WidthMm, int DepthMm, int HeightMm, bool Inline, int DefaultElevationMm, List<Port> Ports);

[JsonSerializable(typeof(List<CatalogEntry>))]
public partial class CatalogJsonContext : JsonSerializerContext { }

public static class EquipmentCatalog
{
    public static IReadOnlyList<CatalogEntry> Default { get; } =
    [
        new("radiator", "Equipment", "Radiator", 1000, 100, 600, false, 150, [new(NetworkType.HeatingSupply, 14, 50, 0, 500), new(NetworkType.HeatingReturn, 14, 950, 0, 500)]),
        new("boiler", "Equipment", "Boiler", 500, 400, 800, false, 0, [new(NetworkType.HeatingSupply, 20, 100, 0, 800), new(NetworkType.HeatingReturn, 20, 400, 0, 800)]),
        new("hot-water-tank", "Equipment", "Hot water tank", 600, 600, 1500, false, 0, [new(NetworkType.DomesticHotWater, 20, 300, 300, 1500), new(NetworkType.ColdWater, 20, 300, 300, 0)]),
        new("shutoff-valve", "Valves", "Shut-off valve", 50, 50, 50, true, 0, []),
        new("thermostatic-valve", "Valves", "Thermostatic valve", 50, 50, 50, true, 0, []),
        new("meter", "Valves", "Meter", 100, 100, 100, true, 0, []),
        new("washbasin", "Sanitary", "Washbasin", 600, 450, 850, false, 0, [new(NetworkType.ColdWater, 12, 150, 0, 500), new(NetworkType.DomesticHotWater, 12, 450, 0, 500), new(NetworkType.Drainage, 40, 300, 0, 400)]),
        new("wc", "Sanitary", "WC", 380, 700, 800, false, 0, [new(NetworkType.ColdWater, 12, 190, 0, 200), new(NetworkType.Drainage, 100, 190, 100, 0)]),
        new("shower", "Sanitary", "Shower", 900, 900, 2000, false, 0, [new(NetworkType.ColdWater, 15, 100, 0, 1000), new(NetworkType.DomesticHotWater, 15, 800, 0, 1000), new(NetworkType.Drainage, 40, 450, 450, 0)]),
    ];

    public static CatalogEntry? Find(string id) => Default.FirstOrDefault(e => e.Id == id);
}

public sealed record Placement(bool Valid, string? Hint, PointMm3? Snapped = null, Guid? SegmentId = null);

/// <summary>Drop rules: inline components only onto a pipe.</summary>
public static class PlacementRules
{
    public static Placement Check(CatalogEntry entry, PointMm3 at, Network? network, int toleranceMm = 100)
    {
        if (!entry.Inline) return new(true, null, at);
        if (network != null)
            foreach (var s in network.Segments)
            {
                var a = network.Nodes.First(n => n.Id == s.A).Position; var b = network.Nodes.First(n => n.Id == s.B).Position;
                var p = ClosestOnSegment(a, b, at);
                var d = Math.Sqrt(Math.Pow(p.X - at.X, 2) + Math.Pow(p.Y - at.Y, 2) + Math.Pow(p.Z - at.Z, 2));
                if (d <= toleranceMm) return new(true, null, p, s.Id);
            }
        return new(false, "A valve must be placed on a pipe");
    }

    private static PointMm3 ClosestOnSegment(PointMm3 a, PointMm3 b, PointMm3 p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z; var l2 = dx * dx + dy * dy + dz * dz;
        var t = l2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy + (p.Z - a.Z) * dz) / l2, 0, 1);
        return new((int)Math.Round(a.X + t * dx), (int)Math.Round(a.Y + t * dy), (int)Math.Round(a.Z + t * dz));
    }
}
