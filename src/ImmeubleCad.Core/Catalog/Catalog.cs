using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.Catalog;

// ---- JSON shape (source-generated deserialization: AOT friendly) ----
public sealed class CatalogParameterDto { public string Id { get; set; } = ""; public double Default { get; set; } public double Min { get; set; } public double Max { get; set; } public Dictionary<string, string> Labels { get; set; } = new(); }
public sealed class CatalogPortDto { public string Network { get; set; } = ""; public int DiameterMm { get; set; } public string X { get; set; } = "0"; public string Y { get; set; } = "0"; public string Z { get; set; } = "0"; }
public sealed class CatalogShapeDto { public string Type { get; set; } = "box"; public string X { get; set; } = "0"; public string Y { get; set; } = "0"; public string Z { get; set; } = "0"; public string W { get; set; } = "0"; public string D { get; set; } = "0"; public string H { get; set; } = "0"; public string R { get; set; } = "0"; }
public sealed class CatalogEntryDto
{
    public string Id { get; set; } = ""; public string Category { get; set; } = ""; public Dictionary<string, string> Names { get; set; } = new();
    public bool Inline { get; set; } public int DefaultElevationMm { get; set; }
    public List<CatalogParameterDto> Parameters { get; set; } = new(); public List<string> Symbol { get; set; } = new(); public List<CatalogShapeDto> Shape { get; set; } = new(); public List<CatalogPortDto> Ports { get; set; } = new();
}

[JsonSerializable(typeof(List<CatalogEntryDto>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class CatalogJsonContext : JsonSerializerContext { }

// ---- model used by the app ----
public sealed record Port(NetworkType Network, int DiameterMm, int OffsetX, int OffsetY, int OffsetZ);
public sealed record ShapePart(string Type, int X, int Y, int Z, int W, int D, int H, int R);
public sealed record CatalogParameter(string Id, double Default, double Min, double Max, IReadOnlyDictionary<string, string> Labels);

/// <summary>A catalog object: parameters with defaults, a 2D symbol (path strings), a 3D shape (boxes and cylinders) and typed ports, all evaluated for a given set of parameter values.</summary>
public sealed class CatalogEntry
{
    public CatalogEntryDto Source { get; }
    public IReadOnlyDictionary<string, double> Values { get; }
    public CatalogEntry(CatalogEntryDto source, IReadOnlyDictionary<string, double>? values = null)
    {
        Source = source;
        var v = source.Parameters.ToDictionary(p => p.Id, p => p.Default);
        if (values != null) foreach (var (k, x) in values) if (source.Parameters.FirstOrDefault(p => p.Id == k) is { } p) v[k] = Math.Clamp(x, p.Min, p.Max);
        Values = v;
    }

    public string Id => Source.Id; public string Category => Source.Category; public bool Inline => Source.Inline; public int DefaultElevationMm => Source.DefaultElevationMm;
    public string Name => Source.Names.TryGetValue(Translations.Language, out var n) ? n : Source.Names.TryGetValue("fr", out var f) ? f : Source.Id;
    public string NameIn(string lang) => Source.Names.TryGetValue(lang, out var n) ? n : Source.Id;
    public int WidthMm => (int)Get("width", 100); public int DepthMm => (int)Get("depth", 100); public int HeightMm => (int)Get("height", 100);
    public IReadOnlyList<CatalogParameter> Parameters => Source.Parameters.Select(p => new CatalogParameter(p.Id, p.Default, p.Min, p.Max, p.Labels)).ToList();
    private double Get(string id, double fallback) => Values.TryGetValue(id, out var x) ? x : fallback;

    public List<Port> Ports => Source.Ports.Select(p => new Port(Enum.Parse<NetworkType>(p.Network), p.DiameterMm, (int)Eval(p.X), (int)Eval(p.Y), (int)Eval(p.Z))).ToList();
    public List<ShapePart> Shape => Source.Shape.Select(s => new ShapePart(s.Type, (int)Eval(s.X), (int)Eval(s.Y), (int)Eval(s.Z), (int)Eval(s.W), (int)Eval(s.D), (int)Eval(s.H), (int)Eval(s.R))).ToList();
    /// <summary>2D symbol path strings with the parameters substituted (numbers in mm, origin = bottom-left of the footprint).</summary>
    public List<string> Symbol => Source.Symbol.Select(Substitute).ToList();

    /// <summary>A copy with other parameter values (user objects).</summary>
    public CatalogEntry With(IReadOnlyDictionary<string, double> values) => new(Source, values);

    private string Substitute(string path)
    {   // every coordinate token is an expression: "M$width/6,0 L…" → evaluate each number-like run
        var sb = new System.Text.StringBuilder(); var token = new System.Text.StringBuilder();
        void Flush() { if (token.Length == 0) return; sb.Append(Eval(token.ToString()).ToString("0.##", CultureInfo.InvariantCulture)); token.Clear(); }
        foreach (var ch in path)
        {
            if (ch is ' ' or ',' or 'M' or 'L' or 'Z' or 'C') { Flush(); sb.Append(ch); }
            else token.Append(ch);
        }
        Flush(); return sb.ToString();
    }

    /// <summary>Tiny expression evaluator: numbers, $parameters and + - * / (left to right with * / first).</summary>
    public double Eval(string expr)
    {
        var tokens = new List<string>(); var cur = new System.Text.StringBuilder();
        foreach (var ch in expr.Trim()) { if (ch is '+' or '-' or '*' or '/' && cur.Length > 0) { tokens.Add(cur.ToString()); tokens.Add(ch.ToString()); cur.Clear(); } else cur.Append(ch); }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        double Val(string t) => t.StartsWith('$') ? Get(t[1..], 0) : double.Parse(t, CultureInfo.InvariantCulture);
        return EvalTokens(tokens, Val);
    }
    private static double EvalTokens(List<string> t, Func<string, double> val)
    {   // first pass * and /, second pass + and -
        var nums = new List<double> { val(t[0]) }; var ops = new List<string>();
        for (var i = 1; i + 1 < t.Count; i += 2)
        {
            var op = t[i]; var n = val(t[i + 1]);
            if (op == "*") nums[^1] *= n; else if (op == "/") nums[^1] = n == 0 ? 0 : nums[^1] / n; else { ops.Add(op); nums.Add(n); }
        }
        var r = nums[0]; for (var i = 0; i < ops.Count; i++) r = ops[i] == "+" ? r + nums[i + 1] : r - nums[i + 1];
        return r;
    }
}

public static class EquipmentCatalog
{
    private static IReadOnlyList<CatalogEntry>? _default;
    /// <summary>Built-in catalog loaded from the embedded catalog/*.json resources.</summary>
    public static IReadOnlyList<CatalogEntry> Default => _default ??= LoadEmbedded();
    public static CatalogEntry? Find(string id) => (UserEntries.FirstOrDefault(e => e.Id == id)) ?? Default.FirstOrDefault(e => e.Id == id);
    public static List<CatalogEntry> UserEntries { get; } = new();
    public static IEnumerable<CatalogEntry> All => Default.Concat(UserEntries.Where(u => Default.All(d => d.Id != u.Id)));

    private static IReadOnlyList<CatalogEntry> LoadEmbedded()
    {
        var asm = typeof(EquipmentCatalog).Assembly; var list = new List<CatalogEntry>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("catalog/") && n.EndsWith(".json")).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            list.AddRange((JsonSerializer.Deserialize(s, CatalogJsonContext.Default.ListCatalogEntryDto) ?? new()).Select(d => new CatalogEntry(d)));
        }
        return list;
    }

    // ---- user objects: duplicate a catalog entry, change its parameters, save in the user's catalog folder ----
    public static CatalogEntry Duplicate(CatalogEntry source, string newId, string nameFr, string nameEn, IReadOnlyDictionary<string, double> parameterDefaults)
    {
        var dto = JsonSerializer.Deserialize(JsonSerializer.Serialize(new List<CatalogEntryDto> { source.Source }, CatalogJsonContext.Default.ListCatalogEntryDto), CatalogJsonContext.Default.ListCatalogEntryDto)![0];
        dto.Id = newId; dto.Names = new() { ["fr"] = nameFr, ["en"] = nameEn };
        foreach (var p in dto.Parameters) if (parameterDefaults.TryGetValue(p.Id, out var v)) p.Default = Math.Clamp(v, p.Min, p.Max);
        return new CatalogEntry(dto);
    }

    public static void SaveUser(string folder, CatalogEntry entry)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, entry.Id + ".json"), JsonSerializer.Serialize(new List<CatalogEntryDto> { entry.Source }, CatalogJsonContext.Default.ListCatalogEntryDto));
    }

    public static void LoadUser(string folder)
    {
        UserEntries.Clear(); if (!Directory.Exists(folder)) return;
        foreach (var f in Directory.GetFiles(folder, "*.json"))
            try { UserEntries.AddRange((JsonSerializer.Deserialize(File.ReadAllText(f), CatalogJsonContext.Default.ListCatalogEntryDto) ?? new()).Select(d => new CatalogEntry(d))); }
            catch (JsonException) { /* a broken user file must not stop the app */ }
    }
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
