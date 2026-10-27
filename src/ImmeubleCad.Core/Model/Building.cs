namespace ImmeubleCad.Core.Model;

public enum Stability { Common, Private }
public enum LevelKind { Basement, Ground, Standard, Attic, Roof, Site }
public enum OpeningKind { Door, Window, Opening }
public enum SpaceKind { Cellar, Corridor, BoilerRoom, BikeRoom, Parking, Stairwell, ServiceShaft, Apartment, Room }
public enum NetworkType { HeatingSupply, HeatingReturn, DomesticHotWater, ColdWater, Drainage, Gas }

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
    public string? Number { get; set; }
    public SpaceKind Kind { get; set; } = SpaceKind.Room;
    public string? Lot { get; set; }
    public Stability Stability { get; set; } = Stability.Private;
}

public sealed class Level
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int AltitudeMm { get; set; }
    public int HeightMm { get; set; } = 2700;
    public LevelKind Kind { get; set; } = LevelKind.Standard;
    public string? BackgroundImage { get; set; }
    public double[]? ImageTransform { get; set; } // 2x3 affine, pixels -> mm
    public List<Wall> Walls { get; set; } = new();
    public List<Space> Spaces { get; set; } = new();
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
}

public sealed class Building
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<Level> Levels { get; set; } = new();
    public List<Network> Networks { get; set; } = new();
    public List<Equipment> Equipment { get; set; } = new();
}
