namespace ImmoCad.Core.Model;

public enum Stability { Common, Private }
public enum LevelKind { Basement, Ground, Standard, Attic, Roof, Site }
public enum OpeningKind { Door, Window, Opening }
public enum SpaceKind { Cellar, Corridor, BoilerRoom, BikeRoom, Parking, Stairwell, ServiceShaft, Apartment, Room }
public enum NetworkType { HeatingSupply, HeatingReturn, DomesticHotWater, ColdWater, Drainage, Gas, DhwReturn }

public sealed class Opening
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public OpeningKind Kind { get; set; }
    public int PositionMm { get; set; }
    public int WidthMm { get; set; } = 800;
    public int HeightMm { get; set; } = 2100;
    public int SillMm { get; set; }
    public bool SwingLeft { get; set; }
    public bool SwingOut { get; set; }
    public string? Label { get; set; }
    public Stability Stability { get; set; } = Stability.Common;
}

public sealed class Wall
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<PointMm> Footprint { get; set; } = new();
    /// <summary>Rooms enclosed by this wall group (even-odd).</summary>
    public List<List<PointMm>> Holes { get; set; } = new();
    public int? HeightMm { get; set; }
    public string? Label { get; set; }
    public Stability Stability { get; set; } = Stability.Common;
    public List<Opening> Openings { get; set; } = new();
}

public sealed class Space
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<PointMm> Outline { get; set; } = new();
    public string Name { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => Translations.Display(Name);
    public string? Number { get; set; }
    public SpaceKind Kind { get; set; } = SpaceKind.Room;
    public string? Lot { get; set; }
    public Stability Stability { get; set; } = Stability.Private;
}

/// <summary>A locked, non-editable 3D mesh (from glTF/OBJ) shown as a section under the level so it can be traced over. Positions are millimetres.</summary>
public sealed class ReferenceMesh
{
    public string Name { get; set; } = "";
    public List<int> Positions { get; set; } = new();   // x,y,z triples (Z up)
    public List<int> Indices { get; set; } = new();     // triangles
}

public sealed class Level
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>The name in the current language when it is a generated one ("@level.n:2").</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => Translations.Display(Name);
    public int AltitudeMm { get; set; }
    public int HeightMm { get; set; } = 2700;
    public LevelKind Kind { get; set; } = LevelKind.Standard;
    public string? BackgroundImage { get; set; }
    public double[]? ImageTransform { get; set; } // 2x3 affine, pixels -> mm
    public double[]? ImageHomography { get; set; } // optional 8 coefficients for perspective mode
    public double BackgroundOpacity { get; set; } = 0.5;
    public bool BackgroundVisible { get; set; } = true;
    public List<Wall> Walls { get; set; } = new();
    public List<Space> Spaces { get; set; } = new();
    public List<ReferenceMesh> References { get; set; } = new();
}

public sealed class Node
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PointMm3 Position { get; set; }
    public string? Component { get; set; }
}

public sealed class Segment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid A { get; set; }
    public Guid B { get; set; }
    public int DiameterMm { get; set; } = 20;
    public string Material { get; set; } = "copper";
}

public sealed class Network
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public NetworkType Type { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public List<Node> Nodes { get; set; } = new();
    public List<Segment> Segments { get; set; } = new();
}

public sealed class Equipment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CatalogId { get; set; } = "";
    public Guid LevelId { get; set; }
    public PointMm Position { get; set; }
    public int ElevationMm { get; set; }
    public int RotationDeg { get; set; }
    public string? Label { get; set; }
    /// <summary>Overrides of the catalog parameters (width, depth, height…) for this instance.</summary>
    public Dictionary<string, double> Parameters { get; set; } = new();
}

/// <summary>Georeferencing: the model's origin (0,0) sits at this Lambert 93 point (metres), rotated to true north.</summary>
public sealed class SiteInfo
{
    public double EastM { get; set; }
    public double NorthM { get; set; }
    /// <summary>Rotation of the model's Y axis from true north, degrees counter-clockwise.</summary>
    public double RotationDeg { get; set; }
    public double AltitudeM { get; set; }
    public string? Address { get; set; }
    /// <summary>Cadastral footprint used as reference outline, in Lambert 93 metres (closed ring without repeated point).</summary>
    public List<double[]> Footprint { get; set; } = new();
    /// <summary>Further cadastral parcels of the same building (same anchor, so no extra alignment), rings in Lambert 93.</summary>
    public List<List<double[]>> ExtraFootprints { get; set; } = new();
    /// <summary>Neighbouring cadastral buildings (rings in Lambert 93), for party-wall detection.</summary>
    public List<List<double[]>> Neighbours { get; set; } = new();
    public bool ShowFootprint { get; set; } = true;
}

public sealed class Building
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => Translations.Display(Name);
    public List<Level> Levels { get; set; } = new();
    public List<Network> Networks { get; set; } = new();
    public List<Equipment> Equipment { get; set; } = new();
    public SiteInfo? Site { get; set; }
    /// <summary>Optional limit of the walk mode's world (the whole building, every level). Absent = the bounding box of what is modelled, plus one metre.</summary>
    public RectMm? WorldFrame { get; set; }
}

/// <summary>Axis-aligned rectangle in millimetres (X0 ≤ X1, Y0 ≤ Y1).</summary>
public readonly record struct RectMm(int X0, int Y0, int X1, int Y1)
{
    public static RectMm Of(PointMm a, PointMm b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}
