using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.History;

public enum BuildingEventKind { LevelAdded, LevelRemoved, LevelChanged, WallAdded, WallRemoved, WallChanged, NetworkChanged, Reset }
public sealed record BuildingEventItem(BuildingEventKind Kind, Guid? Id = null);

public interface IBuildingEventsService : IObservable<BuildingEventItem>
{
    void Publish(BuildingEventItem item);
}

public sealed class BuildingEventsService : IBuildingEventsService
{
    private readonly List<IObserver<BuildingEventItem>> _obs = new();
    public IDisposable Subscribe(IObserver<BuildingEventItem> o) { _obs.Add(o); return new Unsub(_obs, o); }
    public void Publish(BuildingEventItem item) { foreach (var o in _obs.ToArray()) o.OnNext(item); }
    private sealed class Unsub(List<IObserver<BuildingEventItem>> l, IObserver<BuildingEventItem> o) : IDisposable { public void Dispose() => l.Remove(o); }
}

/// <summary>The only way to edit a building. Each action pushes exactly one history item.</summary>
public sealed class BuildingActions(Building building, BuildingHistory history, IBuildingEventsService events)
{
    public Building Building => building;

    private T Edit<T>(string name, BuildingEventKind kind, Func<T> change, Func<T, Guid?> id)
    {
        var before = BuildingJson.Serialize(building);
        var result = change();
        history.Push(new SnapshotItem(name, before, BuildingJson.Serialize(building)));
        events.Publish(new BuildingEventItem(kind, id(result)));
        return result;
    }

    public Level AddLevel(string name, int altitudeMm, int heightMm = 2700, LevelKind kind = LevelKind.Standard) =>
        Edit("Add level", BuildingEventKind.LevelAdded, () =>
        {
            var l = new Level { Name = name, AltitudeMm = altitudeMm, HeightMm = heightMm, Kind = kind };
            building.Levels.Add(l);
            building.Levels.Sort((a, b) => a.AltitudeMm.CompareTo(b.AltitudeMm));
            return l;
        }, l => l.Id);

    public void DeleteLevel(Guid id) =>
        Edit("Delete level", BuildingEventKind.LevelRemoved, () => { building.Levels.RemoveAll(l => l.Id == id); return id; }, i => i);

    public void RenameLevel(Guid id, string name) =>
        Edit("Rename level", BuildingEventKind.LevelChanged, () => { Find(id).Name = name; return id; }, i => i);

    public void SetLevelGeometry(Guid id, int altitudeMm, int heightMm) =>
        Edit("Change level altitude/height", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(id); l.AltitudeMm = altitudeMm; l.HeightMm = heightMm;
            building.Levels.Sort((a, b) => a.AltitudeMm.CompareTo(b.AltitudeMm));
            return id;
        }, i => i);

    public Wall AddWall(Guid levelId, IEnumerable<PointMm> footprint, string? label = null) =>
        Edit("Add wall", BuildingEventKind.WallAdded, () =>
        {
            var l = Find(levelId);
            var w = new Wall { Footprint = footprint.ToList(), Label = label, Stability = l.Kind == LevelKind.Basement ? Stability.Common : Stability.Private };
            l.Walls.Add(w); return w;
        }, w => w.Id);

    public void ImportWalls(Guid levelId, IEnumerable<IEnumerable<PointMm>> footprints) =>
        Edit("Import walls", BuildingEventKind.WallAdded, () =>
        {
            var l = Find(levelId);
            foreach (var f in footprints) l.Walls.Add(new Wall { Footprint = f.ToList() });
            return levelId;
        }, i => i);

    public void DeleteWall(Guid levelId, Guid wallId) =>
        Edit("Delete wall", BuildingEventKind.WallRemoved, () => { Find(levelId).Walls.RemoveAll(w => w.Id == wallId); return wallId; }, i => i);

    public void MoveWall(Guid levelId, Guid wallId, int dx, int dy) =>
        Edit("Move wall", BuildingEventKind.WallChanged, () =>
        {
            var w = Find(levelId).Walls.First(w => w.Id == wallId);
            w.Footprint = w.Footprint.Select(p => new PointMm(p.X + dx, p.Y + dy)).ToList();
            return wallId;
        }, i => i);

    public void SetImageTransform(Guid levelId, double[] matrix2x3) =>
        Edit("Set image transform", BuildingEventKind.LevelChanged, () => { Find(levelId).ImageTransform = matrix2x3; return levelId; }, i => i);

    public Space AddSpace(Guid levelId, IEnumerable<PointMm> outline, string name, SpaceKind kind, string? number = null) =>
        Edit("Add space", BuildingEventKind.LevelChanged, () =>
        {
            var s = new Space { Outline = outline.ToList(), Name = name, Kind = kind, Number = number };
            Find(levelId).Spaces.Add(s); return s;
        }, s => s.Id);

    public Network AddNetwork(string name, NetworkType type) =>
        Edit("Add network", BuildingEventKind.NetworkChanged, () =>
        {
            var n = new Network { Name = name, Type = type }; building.Networks.Add(n); return n;
        }, n => n.Id);

    public Node AddNode(Guid networkId, PointMm3 p) =>
        Edit("Add node", BuildingEventKind.NetworkChanged, () =>
        {
            var n = new Node { Position = p }; Net(networkId).Nodes.Add(n); return n;
        }, n => n.Id);

    public Segment AddSegment(Guid networkId, Guid a, Guid b, int diameterMm = 20) =>
        Edit("Add segment", BuildingEventKind.NetworkChanged, () =>
        {
            var s = new Segment { A = a, B = b, DiameterMm = diameterMm }; Net(networkId).Segments.Add(s); return s;
        }, s => s.Id);

    /// <summary>Whole gesture (chain of points) as one history step.</summary>
    public IReadOnlyList<Segment> AddPipeRun(Guid networkId, IReadOnlyList<PointMm3> points, int diameterMm = 20) =>
        Edit("Add pipe run", BuildingEventKind.NetworkChanged, () =>
        {
            var net = Net(networkId); var segs = new List<Segment>(); Node? prev = null;
            foreach (var p in points)
            {
                var n = net.Nodes.FirstOrDefault(x => x.Position == p) ?? new Node { Position = p };
                if (!net.Nodes.Contains(n)) net.Nodes.Add(n);
                if (prev != null) { var s = new Segment { A = prev.Id, B = n.Id, DiameterMm = diameterMm }; net.Segments.Add(s); segs.Add(s); }
                prev = n;
            }
            return (IReadOnlyList<Segment>)segs;
        }, _ => networkId);

    public void MoveNode(Guid networkId, Guid nodeId, PointMm3 p) =>
        Edit("Move node", BuildingEventKind.NetworkChanged, () => { Net(networkId).Nodes.First(n => n.Id == nodeId).Position = p; return nodeId; }, i => i);

    public void DeleteSegment(Guid networkId, Guid segId) =>
        Edit("Delete segment", BuildingEventKind.NetworkChanged, () => { Net(networkId).Segments.RemoveAll(s => s.Id == segId); return segId; }, i => i);

    public void SetSegmentDiameter(Guid networkId, Guid segId, int diameterMm) =>
        Edit("Change diameter", BuildingEventKind.NetworkChanged, () => { Net(networkId).Segments.First(s => s.Id == segId).DiameterMm = diameterMm; return segId; }, i => i);

    public Equipment AddEquipment(Guid levelId, string catalogId, PointMm pos, int elevationMm = 0) =>
        Edit("Add equipment", BuildingEventKind.LevelChanged, () =>
        {
            var e = new Equipment { CatalogId = catalogId, LevelId = levelId, Position = pos, ElevationMm = elevationMm };
            building.Equipment.Add(e); return e;
        }, e => e.Id);

    private Level Find(Guid id) => building.Levels.First(l => l.Id == id);
    private Network Net(Guid id) => building.Networks.First(n => n.Id == id);
}
