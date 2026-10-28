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

    public void ImportWallShapes(Guid levelId, IEnumerable<(IEnumerable<PointMm> Outline, IEnumerable<IEnumerable<PointMm>> Holes)> shapes) =>
        Edit("Import walls", BuildingEventKind.WallAdded, () =>
        {
            var l = Find(levelId);
            foreach (var (o, holes) in shapes) l.Walls.Add(new Wall { Footprint = o.ToList(), Holes = holes.Select(h => h.ToList()).ToList() });
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

    /// <summary>Moves walls (a group) by dx/dy on their level, and optionally to another level. One history step.</summary>
    public void MoveWalls(Guid levelId, IEnumerable<Guid> wallIds, int dx, int dy, Guid? targetLevelId = null) =>
        Edit("Move walls", BuildingEventKind.WallChanged, () =>
        {
            var from = Find(levelId); var to = targetLevelId is { } t ? Find(t) : from; var ids = wallIds.ToHashSet();
            var moving = from.Walls.Where(w => ids.Contains(w.Id)).ToList();
            foreach (var w in moving)
            {
                w.Footprint = w.Footprint.Select(p => new PointMm(p.X + dx, p.Y + dy)).ToList();
                w.Holes = w.Holes.Select(h => h.Select(p => new PointMm(p.X + dx, p.Y + dy)).ToList()).ToList();
                if (to != from) { from.Walls.Remove(w); to.Walls.Add(w); }
            }
            return levelId;
        }, i => i);

    /// <summary>Copies walls to other levels (same XY), e.g. to repeat a floor plan. One history step.</summary>
    public void CopyWalls(Guid levelId, IEnumerable<Guid> wallIds, IEnumerable<Guid> targetLevelIds) =>
        Edit("Copy walls", BuildingEventKind.WallAdded, () =>
        {
            var src = Find(levelId).Walls.Where(w => wallIds.Contains(w.Id)).ToList();
            foreach (var t in targetLevelIds.Where(t => t != levelId))
                foreach (var w in src)
                    Find(t).Walls.Add(new Wall { Footprint = w.Footprint.ToList(), Holes = w.Holes.Select(h => h.ToList()).ToList(), HeightMm = w.HeightMm, Label = w.Label, Stability = w.Stability });
            return levelId;
        }, i => i);

    public void DeleteWalls(Guid levelId, IEnumerable<Guid> wallIds) =>
        Edit("Delete walls", BuildingEventKind.WallRemoved, () => { var ids = wallIds.ToHashSet(); Find(levelId).Walls.RemoveAll(w => ids.Contains(w.Id)); return levelId; }, i => i);

    public void SetImageTransform(Guid levelId, double[] matrix2x3) =>
        Edit("Set image transform", BuildingEventKind.LevelChanged, () => { Find(levelId).ImageTransform = matrix2x3; return levelId; }, i => i);

    public Space AddSpace(Guid levelId, IEnumerable<PointMm> outline, string name, SpaceKind kind, string? number = null) =>
        Edit("Add space", BuildingEventKind.LevelChanged, () =>
        {
            var s = new Space { Outline = outline.ToList(), Name = name, Kind = kind, Number = number, Stability = Spaces.DefaultFor(Find(levelId).Kind, kind) };
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

    public Opening AddOpening(Guid levelId, Guid wallId, OpeningKind kind, int positionMm, int? widthMm = null, string? label = null) =>
        Edit("Add opening", BuildingEventKind.WallChanged, () =>
        {
            var l = Find(levelId); var w = l.Walls.First(w => w.Id == wallId);
            var o = new Opening
            {
                Kind = kind, PositionMm = positionMm, Label = label,
                WidthMm = widthMm ?? (kind == OpeningKind.Door ? (l.Kind == LevelKind.Basement ? 700 : 800) : 1200),
                HeightMm = kind == OpeningKind.Window ? 1200 : 2100, SillMm = kind == OpeningKind.Window ? 900 : 0,
                Stability = w.Stability,
            };
            w.Openings.Add(o); return o;
        }, o => o.Id);

    public void FlipOpening(Guid levelId, Guid wallId, Guid openingId) =>
        Edit("Flip opening", BuildingEventKind.WallChanged, () => { var o = Find(levelId).Walls.First(w => w.Id == wallId).Openings.First(o => o.Id == openingId); o.SwingLeft = !o.SwingLeft; return openingId; }, i => i);

    public void SetStability(Guid levelId, Guid spaceId, Stability stability) =>
        Edit("Set stability", BuildingEventKind.LevelChanged, () => { Find(levelId).Spaces.First(s => s.Id == spaceId).Stability = stability; return spaceId; }, i => i);

    /// <summary>Numbers the given spaces in order ("Cave 1", "Cave 2"…) as one history step; also labels their doors.</summary>
    public void NumberSpaces(Guid levelId, IReadOnlyList<Guid> spaceIds, string prefix = "Cave ", int start = 1) =>
        Edit("Number spaces", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(levelId); var n = start;
            foreach (var id in spaceIds)
            {
                var sp = l.Spaces.First(x => x.Id == id); sp.Number = n.ToString(); sp.Name = prefix + n;
                foreach (var w in l.Walls)
                    foreach (var o in w.Openings.Where(o => o.Kind == OpeningKind.Door && IsOnOutline(w, o, sp)))
                        o.Label = sp.Name;
                n++;
            }
            return levelId;
        }, i => i);

    private static bool IsOnOutline(Wall w, Opening o, Space sp)
    {
        // door centre (on the wall's long axis) lies within 300 mm of the space outline
        var pts = w.Footprint; if (pts.Count < 2) return false;
        var a = pts[0]; var b = pts[1]; double dx = b.X - a.X, dy = b.Y - a.Y; var len = Math.Sqrt(dx * dx + dy * dy); if (len < 1) return false;
        var c = new PointMm((int)(a.X + dx / len * o.PositionMm), (int)(a.Y + dy / len * o.PositionMm));
        return Geometry.SpaceFinder.Contains(sp.Outline, c) || sp.Outline.Any(p => Math.Abs(p.X - c.X) < 300 && Math.Abs(p.Y - c.Y) < 300);
    }

    /// <summary>Straighten the given walls with the Plan straightener (mm), one history step.</summary>
    public void StraightenWalls(Guid levelId, IEnumerable<Guid> wallIds, double toleranceMm = 30, double angleDeg = 5) =>
        Edit("Straighten walls", BuildingEventKind.WallChanged, () =>
        {
            var l = Find(levelId); var ids = wallIds.ToHashSet();
            foreach (var w in l.Walls.Where(w => ids.Contains(w.Id)))
                w.Footprint = Plan.WallStraightener.Straighten(w.Footprint.Select(p => new Plan.PointPx(p.X, p.Y)).ToList(), toleranceMm, angleDeg)
                    .Select(p => new PointMm((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToList();
            return levelId;
        }, i => i);

    /// <summary>Several risers at once (hot+cold…) at fixed spacing along X, from zFrom to zTo. One history step.</summary>
    public IReadOnlyList<Network> AddRiserTemplate(PointMm origin, int spacingMm, IReadOnlyList<NetworkType> types, int zFromMm, int zToMm, int diameterMm = 20) =>
        Edit("Add riser template", BuildingEventKind.NetworkChanged, () =>
        {
            var res = new List<Network>();
            for (var i = 0; i < types.Count; i++)
            {
                var n = new Network { Name = "Riser " + types[i], Type = types[i] };
                var a = new Node { Position = new(origin.X + i * spacingMm, origin.Y, zFromMm) };
                var b = new Node { Position = new(origin.X + i * spacingMm, origin.Y, zToMm) };
                n.Nodes.Add(a); n.Nodes.Add(b); n.Segments.Add(new Segment { A = a.Id, B = b.Id, DiameterMm = diameterMm });
                building.Networks.Add(n); res.Add(n);
            }
            return (IReadOnlyList<Network>)res;
        }, r => r.FirstOrDefault()?.Id);

    /// <summary>Connect a point (equipment port) to a pipe node with orthogonal routing: along Z first, then X, then Y.</summary>
    public IReadOnlyList<Segment> ConnectOrthogonal(Guid networkId, PointMm3 from, Guid toNodeId, int diameterMm = 14) =>
        Edit("Connect", BuildingEventKind.NetworkChanged, () =>
        {
            var net = Net(networkId); var target = net.Nodes.First(n => n.Id == toNodeId);
            var route = new List<PointMm3> { from };
            var t = target.Position;
            if (from.Z != t.Z) route.Add(new(from.X, from.Y, t.Z));
            if (from.X != t.X) route.Add(new(t.X, from.Y, t.Z));
            if (from.Y != t.Y || route.Count == 1) route.Add(t);
            var nodes = route.Select(p => net.Nodes.FirstOrDefault(n => n.Position == p) ?? new Node { Position = p }).ToList();
            nodes[^1] = target;
            foreach (var n in nodes.Where(n => !net.Nodes.Contains(n))) net.Nodes.Add(n);
            var segs = new List<Segment>();
            for (var i = 1; i < nodes.Count; i++) if (nodes[i - 1].Id != nodes[i].Id) { var s = new Segment { A = nodes[i - 1].Id, B = nodes[i].Id, DiameterMm = diameterMm }; net.Segments.Add(s); segs.Add(s); }
            return (IReadOnlyList<Segment>)segs;
        }, _ => networkId);

    private Level Find(Guid id) => building.Levels.First(l => l.Id == id);
    private Network Net(Guid id) => building.Networks.First(n => n.Id == id);
}
