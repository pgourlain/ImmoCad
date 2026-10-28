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

    public void SetWallProperties(Guid levelId, IEnumerable<Guid> wallIds, string? label, int? heightMm) =>
        Edit("Wall properties", BuildingEventKind.WallChanged, () =>
        {
            var ids = wallIds.ToHashSet();
            foreach (var w in Find(levelId).Walls.Where(w => ids.Contains(w.Id))) { w.Label = label; w.HeightMm = heightMm; }
            return levelId;
        }, i => i);

    public void SetEquipmentProperties(Guid equipmentId, string? label, int elevationMm, int rotationDeg) =>
        Edit("Equipment properties", BuildingEventKind.LevelChanged, () =>
        {
            var e = building.Equipment.First(x => x.Id == equipmentId); e.Label = label; e.ElevationMm = elevationMm; e.RotationDeg = rotationDeg; return equipmentId;
        }, i => i);

    public void DeleteEquipment(Guid equipmentId) =>
        Edit("Delete equipment", BuildingEventKind.LevelChanged, () => { building.Equipment.RemoveAll(e => e.Id == equipmentId); return equipmentId; }, i => i);

    public void DeleteNetwork(Guid networkId) =>
        Edit("Delete network", BuildingEventKind.NetworkChanged, () => { building.Networks.RemoveAll(n => n.Id == networkId); return networkId; }, i => i);

    public void DeleteWalls(Guid levelId, IEnumerable<Guid> wallIds) =>
        Edit("Delete walls", BuildingEventKind.WallRemoved, () => { var ids = wallIds.ToHashSet(); Find(levelId).Walls.RemoveAll(w => ids.Contains(w.Id)); return levelId; }, i => i);

    public void SetImageTransform(Guid levelId, double[] matrix2x3) =>
        Edit("Set image transform", BuildingEventKind.LevelChanged, () => { Find(levelId).ImageTransform = matrix2x3; return levelId; }, i => i);

    public void SetBackground(Guid levelId, string imageName, double[] matrix2x3, double opacity = 0.5) =>
        Edit("Set background", BuildingEventKind.LevelChanged, () => { var l = Find(levelId); l.BackgroundImage = imageName; l.ImageTransform = matrix2x3; l.BackgroundOpacity = opacity; return levelId; }, i => i);

    public void SetBackgroundDisplay(Guid levelId, double opacity, bool visible) =>
        Edit("Background display", BuildingEventKind.LevelChanged, () => { var l = Find(levelId); l.BackgroundOpacity = Math.Clamp(opacity, 0, 1); l.BackgroundVisible = visible; return levelId; }, i => i);

    /// <summary>
    /// Aligns a level on the one below from matching points (pairs: point on this level → same point on the level below, mm).
    /// 2 pairs = similarity, 3+ = affine. The background transform is composed with the fit; walls move along when <paramref name="moveWalls"/>.
    /// Returns the residual error of each pair in mm (null when the fit is degenerate).
    /// </summary>
    public double[]? AlignLevel(Guid levelId, IReadOnlyList<((double X, double Y) From, (double X, double Y) To)> pairs, bool moveWalls = true)
    {
        var fit = Geometry.ImageFit.Fit(pairs); if (fit is null) return null;
        var residuals = Geometry.ImageFit.Residuals(fit.Value, pairs);
        Edit("Align with level below", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(levelId); var f = fit.Value;
            var t = l.ImageTransform ?? [1, 0, 0, 0, 1, 0];
            // new = f ∘ old  (old maps px→mm, f maps mm→mm)
            l.ImageTransform = [f.A * t[0] + f.B * t[3], f.A * t[1] + f.B * t[4], f.A * t[2] + f.B * t[5] + f.Tx,
                                f.C * t[0] + f.D * t[3], f.C * t[1] + f.D * t[4], f.C * t[2] + f.D * t[5] + f.Ty];
            if (moveWalls)
            {
                PointMm M(PointMm p) { var (x, y) = f.Apply(p.X, p.Y); return new((int)Math.Round(x), (int)Math.Round(y)); }
                foreach (var w in l.Walls) { w.Footprint = w.Footprint.Select(M).ToList(); w.Holes = w.Holes.Select(h => h.Select(M).ToList()).ToList(); }
            }
            return levelId;
        }, i => i);
        return residuals;
    }

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

    /// <summary>Inserts a component (valve, meter…) on a pipe: splits the segment at the point; the new node carries the component id.</summary>
    public Node InsertComponent(Guid networkId, Guid segmentId, PointMm3 at, string component) =>
        Edit("Insert component", BuildingEventKind.NetworkChanged, () =>
        {
            var net = Net(networkId); var seg = net.Segments.First(s => s.Id == segmentId);
            var node = new Node { Position = at, Component = component }; net.Nodes.Add(node);
            var b = seg.B; seg.B = node.Id;
            net.Segments.Add(new Segment { A = node.Id, B = b, DiameterMm = seg.DiameterMm, Material = seg.Material });
            return node;
        }, n => n.Id);

    /// <summary>Splits a segment in two at a point without a component (tee branch start, corner…).</summary>
    public Node SplitSegment(Guid networkId, Guid segmentId, PointMm3 at) =>
        Edit("Split segment", BuildingEventKind.NetworkChanged, () =>
        {
            var net = Net(networkId); var seg = net.Segments.First(s => s.Id == segmentId);
            var node = new Node { Position = at }; net.Nodes.Add(node);
            var b = seg.B; seg.B = node.Id;
            net.Segments.Add(new Segment { A = node.Id, B = b, DiameterMm = seg.DiameterMm, Material = seg.Material });
            return node;
        }, n => n.Id);

    /// <summary>Merges two collinear segments sharing a node (and no component, no third segment).</summary>
    public bool MergeAt(Guid networkId, Guid nodeId)
    {
        var net = Net(networkId); var segs = net.Segments.Where(s => s.A == nodeId || s.B == nodeId).ToList(); var node = net.Nodes.First(n => n.Id == nodeId);
        if (segs.Count != 2 || node.Component != null || segs[0].DiameterMm != segs[1].DiameterMm) return false;
        Guid O(Segment s) => s.A == nodeId ? s.B : s.A;
        var a = net.Nodes.First(n => n.Id == O(segs[0])).Position; var c = net.Nodes.First(n => n.Id == O(segs[1])).Position; var p = node.Position;
        if (Math.Sign(a.X - p.X) != -Math.Sign(c.X - p.X) || Math.Sign(a.Y - p.Y) != -Math.Sign(c.Y - p.Y) || Math.Sign(a.Z - p.Z) != -Math.Sign(c.Z - p.Z)) return false;
        Edit("Merge segments", BuildingEventKind.NetworkChanged, () =>
        {
            segs[0].A = O(segs[0]); segs[0].B = O(segs[1]); net.Segments.Remove(segs[1]); net.Nodes.Remove(node); return nodeId;
        }, i => i);
        return true;
    }

    public void SetNetworkDisplay(Guid networkId, bool visible, bool locked) =>
        Edit("Network display", BuildingEventKind.NetworkChanged, () => { var n = Net(networkId); n.Visible = visible; n.Locked = locked; return networkId; }, i => i);

    /// <summary>Changes the vertical extent of a riser (all segments of the network at that XY): from/to altitude in mm. Crossings are derived.</summary>
    public void SetRiserExtent(Guid networkId, int fromZ, int toZ) =>
        Edit("Riser extent", BuildingEventKind.NetworkChanged, () =>
        {
            var net = Net(networkId); var low = net.Nodes.OrderBy(n => n.Position.Z).First(); var high = net.Nodes.OrderBy(n => n.Position.Z).Last();
            low.Position = low.Position with { Z = Math.Min(fromZ, toZ) }; high.Position = high.Position with { Z = Math.Max(fromZ, toZ) }; return networkId;
        }, i => i);

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
