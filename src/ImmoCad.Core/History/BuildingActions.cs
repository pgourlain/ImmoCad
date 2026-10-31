using ImmoCad.Core.Model;

namespace ImmoCad.Core.History;

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

    /// <summary>Sets (or clears with null) the frame that limits the walk mode's world.</summary>
    public void SetWorldFrame(RectMm? frame) =>
        Edit("Set world frame", BuildingEventKind.LevelChanged, () => { building.WorldFrame = frame; return Guid.Empty; }, _ => null);

    // ---- floor markings (paint on the floor: never a wall) ----

    /// <summary>Whole walls become floor markings (the selection). Returns how many.</summary>
    public int ConvertWallsToMarkings(Guid levelId, IEnumerable<Guid> wallIds) =>
        Edit("Convert to floor marking", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(levelId); var ids = wallIds.ToHashSet(); var n = 0;
            foreach (var w in l.Walls.Where(w => ids.Contains(w.Id)).ToList())
            {
                l.Markings.Add(new FloorMarking { Outline = w.Footprint.ToList(), Holes = w.Holes.Select(h => h.ToList()).ToList() }); l.Walls.Remove(w); n++;
            }
            return n;
        }, _ => null);

    /// <summary>Applies a plan of <see cref="Geometry.FloorMarkings.PlanPaint"/>: the thin parts become markings, the rest stays walls (the first piece keeps the wall's id and openings).</summary>
    public int PaintMarkings(Guid levelId, IReadOnlyList<Geometry.WallSplit> splits) =>
        Edit("Paint floor markings", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(levelId); var n = 0;
            foreach (var sp in splits)
            {
                var at = l.Walls.FindIndex(x => x.Id == sp.WallId); if (at < 0) continue; var w = l.Walls[at]; l.Walls.RemoveAt(at);
                for (var i = 0; i < sp.Rest.Count; i++)
                    l.Walls.Insert(at + i, new Wall { Id = i == 0 ? w.Id : Guid.NewGuid(), Footprint = sp.Rest[i].Outline, Holes = sp.Rest[i].Holes, HeightMm = w.HeightMm, Label = w.Label, Stability = w.Stability, Openings = i == 0 ? w.Openings : new() });
                foreach (var t in sp.Thin) { l.Markings.Add(new FloorMarking { Outline = t.Outline, Holes = t.Holes }); n++; }
            }
            return n;
        }, _ => null);

    /// <summary>Floor markings become walls again.</summary>
    public int MarkingsToWalls(Guid levelId, IEnumerable<Guid> markingIds) =>
        Edit("Floor markings to walls", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(levelId); var ids = markingIds.ToHashSet(); var n = 0;
            foreach (var m in l.Markings.Where(m => ids.Contains(m.Id)).ToList()) { l.Walls.Add(new Wall { Footprint = m.Outline.ToList(), Holes = m.Holes.Select(h => h.ToList()).ToList() }); l.Markings.Remove(m); n++; }
            return n;
        }, _ => null);

    public int EraseMarkings(Guid levelId, IEnumerable<Guid> markingIds) =>
        Edit("Erase floor markings", BuildingEventKind.LevelChanged, () => { var ids = markingIds.ToHashSet(); return Find(levelId).Markings.RemoveAll(m => ids.Contains(m.Id)); }, _ => null);

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

    /// <summary>
    /// Adds traced shapes as walls. With <paramref name="thinToMarkingsMm"/> above 0, material thinner than that (the painted lines of a parking, the strokes of a scan)
    /// becomes floor markings instead of walls: still one history step.
    /// </summary>
    public void ImportWallShapes(Guid levelId, IEnumerable<(IEnumerable<PointMm> Outline, IEnumerable<IEnumerable<PointMm>> Holes)> shapes, int thinToMarkingsMm = 0) =>
        Edit("Import walls", BuildingEventKind.WallAdded, () =>
        {
            var l = Find(levelId);
            foreach (var (o, holes) in shapes)
            {
                var outline = o.ToList(); var hs = holes.Select(h => h.ToList()).ToList();
                if (thinToMarkingsMm <= 0) { l.Walls.Add(new Wall { Footprint = outline, Holes = hs }); continue; }
                var (thin, rest) = Geometry.PolygonOps.SplitThin(new Geometry.Shape(outline, hs), thinToMarkingsMm);
                foreach (var r in rest) l.Walls.Add(new Wall { Footprint = r.Outline, Holes = r.Holes });
                foreach (var t in thin) l.Markings.Add(new FloorMarking { Outline = t.Outline, Holes = t.Holes });
            }
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
            foreach (var m in from.Markings.Where(m => ids.Contains(m.Id)).ToList())                       // floor markings are selected like walls and move with them
            {
                m.Outline = m.Outline.Select(p => new PointMm(p.X + dx, p.Y + dy)).ToList(); m.Holes = m.Holes.Select(h => h.Select(p => new PointMm(p.X + dx, p.Y + dy)).ToList()).ToList();
                if (to != from) { from.Markings.Remove(m); to.Markings.Add(m); }
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

    /// <summary>Pastes copies of the given walls (clipboard) on a level, shifted by (dx, dy). Returns the ids of the new walls. One history step.</summary>
    public IReadOnlyList<Guid> PasteWalls(Guid levelId, IEnumerable<Wall> templates, int dx, int dy)
    {
        var ids = new List<Guid>(); var src = templates.ToList();
        if (src.Count == 0) return ids;
        PointMm Sh(PointMm q) => new(q.X + dx, q.Y + dy);
        Edit("Paste walls", BuildingEventKind.WallAdded, () =>
        {
            ids.Clear();
            foreach (var w in src)
            {
                var c = new Wall
                {
                    Footprint = w.Footprint.Select(Sh).ToList(), Holes = w.Holes.Select(h => h.Select(Sh).ToList()).ToList(), HeightMm = w.HeightMm, Label = w.Label, Stability = w.Stability,
                    Openings = w.Openings.Select(o => new Opening { Kind = o.Kind, PositionMm = o.PositionMm, WidthMm = o.WidthMm, HeightMm = o.HeightMm, SillMm = o.SillMm, SwingLeft = o.SwingLeft, SwingOut = o.SwingOut, Label = o.Label, Stability = o.Stability }).ToList(),
                };
                Find(levelId).Walls.Add(c); ids.Add(c.Id);
            }
            return levelId;
        }, i => i);
        return ids;
    }

    public void SetWallProperties(Guid levelId, IEnumerable<Guid> wallIds, string? label, int? heightMm) =>
        Edit("Wall properties", BuildingEventKind.WallChanged, () =>
        {
            var ids = wallIds.ToHashSet();
            foreach (var w in Find(levelId).Walls.Where(w => ids.Contains(w.Id))) { w.Label = label; w.HeightMm = heightMm; }
            return levelId;
        }, i => i);

    public void SetEquipmentProperties(Guid equipmentId, string? label, int elevationMm, int rotationDeg, IReadOnlyDictionary<string, double>? parameters = null) =>
        Edit("Equipment properties", BuildingEventKind.LevelChanged, () =>
        {
            var e = building.Equipment.First(x => x.Id == equipmentId); e.Label = label; e.ElevationMm = elevationMm; e.RotationDeg = ((rotationDeg % 360) + 360) % 360;
            if (parameters != null) e.Parameters = parameters.ToDictionary(k => k.Key, k => k.Value);
            return equipmentId;
        }, i => i);

    public void MoveEquipment(Guid equipmentId, PointMm to) =>
        Edit("Move equipment", BuildingEventKind.LevelChanged, () => { building.Equipment.First(x => x.Id == equipmentId).Position = to; return equipmentId; }, i => i);

    /// <summary>
    /// Connect gesture: from a port position to a pipe or riser of the same network. The target segment is split at the point nearest the port
    /// (at the port's altitude for risers), then the connection is routed orthogonally at the port's height (X, then Y), as one history step.
    /// Returns the new segments, or null when the target cannot be reached orthogonally.
    /// </summary>
    public IReadOnlyList<Segment>? ConnectPortToSegment(Guid networkId, Guid targetSegmentId, PointMm3 port, int diameterMm = 14)
    {
        var net = Net(networkId); var seg = net.Segments.First(s => s.Id == targetSegmentId);
        var a = net.Nodes.First(n => n.Id == seg.A).Position; var b = net.Nodes.First(n => n.Id == seg.B).Position;
        PointMm3 at;
        if (a.X == b.X && a.Y == b.Y) { var lo = Math.Min(a.Z, b.Z); var hi = Math.Max(a.Z, b.Z); at = new(a.X, a.Y, Math.Clamp(port.Z, lo, hi)); }                    // riser: join at the port's altitude
        else if (a.Z == b.Z && a.Y == b.Y) at = new(Math.Clamp(port.X, Math.Min(a.X, b.X), Math.Max(a.X, b.X)), a.Y, a.Z);
        else if (a.Z == b.Z && a.X == b.X) at = new(a.X, Math.Clamp(port.Y, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)), a.Z);
        else return null;
        return Edit("Connect port", BuildingEventKind.NetworkChanged, () =>
        {
            Node target = net.Nodes.FirstOrDefault(n => n.Position == at) ?? SplitInPlace(net, seg, at);
            var route = new List<PointMm3> { port }; var cur = port;
            if (cur.Z != at.Z) { cur = cur with { Z = at.Z }; route.Add(cur); }
            if (cur.X != at.X) { cur = cur with { X = at.X }; route.Add(cur); }
            if (cur.Y != at.Y) { cur = cur with { Y = at.Y }; route.Add(cur); }
            var nodes = route.Select(p => p == at ? target : net.Nodes.FirstOrDefault(n => n.Position == p) ?? new Node { Position = p }).ToList();
            nodes[^1] = target;
            foreach (var n in nodes.Where(n => !net.Nodes.Contains(n))) net.Nodes.Add(n);
            var created = new List<Segment>();
            for (var i = 1; i < nodes.Count; i++) if (nodes[i - 1].Id != nodes[i].Id) { var sg = new Segment { A = nodes[i - 1].Id, B = nodes[i].Id, DiameterMm = diameterMm, Material = seg.Material }; net.Segments.Add(sg); created.Add(sg); }
            return (IReadOnlyList<Segment>)created;
        }, _ => networkId);
    }

    private static Node SplitInPlace(Network net, Segment seg, PointMm3 at)
    {
        var node = new Node { Position = at }; net.Nodes.Add(node);
        var oldB = seg.B; seg.B = node.Id; net.Segments.Add(new Segment { A = node.Id, B = oldB, DiameterMm = seg.DiameterMm, Material = seg.Material }); return node;
    }

    public void DeleteEquipment(Guid equipmentId) =>
        Edit("Delete equipment", BuildingEventKind.LevelChanged, () => { building.Equipment.RemoveAll(e => e.Id == equipmentId); return equipmentId; }, i => i);

    public void DeleteNetwork(Guid networkId) =>
        Edit("Delete network", BuildingEventKind.NetworkChanged, () => { building.Networks.RemoveAll(n => n.Id == networkId); return networkId; }, i => i);

    public void DeleteWalls(Guid levelId, IEnumerable<Guid> wallIds) =>
        Edit("Delete walls", BuildingEventKind.WallRemoved, () => { var ids = wallIds.ToHashSet(); var l = Find(levelId); l.Walls.RemoveAll(w => ids.Contains(w.Id)); l.Markings.RemoveAll(m => ids.Contains(m.Id)); return levelId; }, i => i);

    private static Geometry.Shape ShapeOf(Wall w) => new(w.Footprint.ToList(), w.Holes.Select(h => h.ToList()).ToList());

    /// <summary>Fuses the walls into one wall per connected piece (gaps narrower than <paramref name="closeMm"/> between fragments are closed). The first wall keeps its id, label and openings. Returns how many walls the group became (0: nothing to do). One history step.</summary>
    public int MergeWalls(Guid levelId, IEnumerable<Guid> wallIds, int closeMm = 20)
    {
        var ids = wallIds.ToHashSet(); var l = Find(levelId); var group = l.Walls.Where(w => ids.Contains(w.Id) && w.Footprint.Count >= 3).ToList(); if (group.Count < 2) return 0;
        var merged = Geometry.PolygonOps.Merge(group.Select(ShapeOf).ToList(), closeMm); if (merged.Count == 0) return 0;
        Edit("Merge walls", BuildingEventKind.WallChanged, () =>
        {
            var first = group[0]; var at = l.Walls.IndexOf(first); var openings = group.SelectMany(w => w.Openings).ToList();
            l.Walls.RemoveAll(w => group.Contains(w));
            for (var i = 0; i < merged.Count; i++)
            {
                var w = i == 0 ? first : new Wall { Label = first.Label, Stability = first.Stability, HeightMm = first.HeightMm };
                w.Footprint = merged[i].Outline; w.Holes = merged[i].Holes; if (i == 0) w.Openings = openings;
                l.Walls.Insert(Math.Min(at + i, l.Walls.Count), w);
            }
            return levelId;
        }, i => i);
        return merged.Count;
    }

    /// <summary>Cuts along the line through a and b and drops everything on the other side, in the given walls or, without ids, in every wall the segment ab crosses. A wall that is cut in several pieces keeps its id on the first; a wall left empty is deleted. Returns the walls changed. One history step.</summary>
    public int CutWalls(Guid levelId, PointMm a, PointMm b, bool keepLeft, IEnumerable<Guid>? wallIds = null)
    {
        var l = Find(levelId); var ids = wallIds?.ToHashSet();                                           // the ids may name walls and floor markings: both are cut
        var targets = l.Walls.Where(w => w.Footprint.Count >= 3 && (ids?.Contains(w.Id) ?? Geometry.PolygonOps.Crosses(ShapeOf(w), a, b)));
        var marks = l.Markings.Where(m => m.Outline.Count >= 3 && (ids?.Contains(m.Id) ?? Geometry.PolygonOps.Crosses(ShapeOf(m), a, b)));
        return ApplyCuts(l, targets.Select(w => (Wall: w, Parts: Geometry.PolygonOps.KeepSide(ShapeOf(w), a, b, keepLeft))).ToList(), marks.Select(m => (Mark: m, Parts: Geometry.PolygonOps.KeepSide(ShapeOf(m), a, b, keepLeft))).ToList());
    }

    /// <summary>Removes what lies inside the zone from the given walls (and floor markings) or, without ids, from every wall and marking the zone reaches. Same rules as <see cref="CutWalls"/>; the rest of a wall that runs far beyond the zone is left exactly as it was.</summary>
    public int CutWallsRegion(Guid levelId, IReadOnlyList<PointMm> zone, IEnumerable<Guid>? wallIds = null)
    {
        var l = Find(levelId); var ids = wallIds?.ToHashSet();
        var targets = l.Walls.Where(w => w.Footprint.Count >= 3 && (ids?.Contains(w.Id) ?? Geometry.PolygonOps.Touches(ShapeOf(w), zone)));
        var marks = l.Markings.Where(m => m.Outline.Count >= 3 && (ids?.Contains(m.Id) ?? Geometry.PolygonOps.Touches(ShapeOf(m), zone)));
        return ApplyCuts(l, targets.Select(w => (Wall: w, Parts: Geometry.PolygonOps.Subtract(ShapeOf(w), zone))).ToList(), marks.Select(m => (Mark: m, Parts: Geometry.PolygonOps.Subtract(ShapeOf(m), zone))).ToList());
    }

    private static Geometry.Shape ShapeOf(FloorMarking m) => new(m.Outline.ToList(), m.Holes.Select(h => h.ToList()).ToList());

    private int ApplyCuts(Level l, List<(Wall Wall, List<Geometry.Shape> Parts)> all, List<(FloorMarking Mark, List<Geometry.Shape> Parts)> allMarks)
    {
        var cuts = all.Where(t => !(t.Parts.Count == 1 && t.Parts[0].Outline.SequenceEqual(t.Wall.Footprint) && t.Parts[0].Holes.Count == t.Wall.Holes.Count)).ToList();
        var markCuts = allMarks.Where(t => !(t.Parts.Count == 1 && t.Parts[0].Outline.SequenceEqual(t.Mark.Outline) && t.Parts[0].Holes.Count == t.Mark.Holes.Count)).ToList();
        if (cuts.Count == 0 && markCuts.Count == 0) return 0;
        Edit("Cut walls", BuildingEventKind.WallChanged, () =>
        {
            foreach (var (w, parts) in cuts)
            {
                var at = l.Walls.IndexOf(w);
                if (parts.Count == 0) { l.Walls.RemoveAt(at); continue; }
                w.Footprint = parts[0].Outline; w.Holes = parts[0].Holes;
                for (var i = 1; i < parts.Count; i++) l.Walls.Insert(at + i, new Wall { Footprint = parts[i].Outline, Holes = parts[i].Holes, Label = w.Label, Stability = w.Stability, HeightMm = w.HeightMm });
            }
            foreach (var (m, parts) in markCuts)
            {   // the pieces of a cut marking stay markings
                var at = l.Markings.IndexOf(m);
                if (parts.Count == 0) { l.Markings.RemoveAt(at); continue; }
                m.Outline = parts[0].Outline; m.Holes = parts[0].Holes;
                for (var i = 1; i < parts.Count; i++) l.Markings.Insert(at + i, new FloorMarking { Outline = parts[i].Outline, Holes = parts[i].Holes });
            }
            return l.Id;
        }, i => i);
        return cuts.Count + markCuts.Count;
    }

    /// <summary>Sets the background transform; with <paramref name="moveWalls"/> the walls and spaces follow the same move (new ∘ old⁻¹).</summary>
    public void SetImageTransform(Guid levelId, double[] matrix2x3, bool moveWalls = false) =>
        Edit("Set image transform", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(levelId); var old = l.ImageTransform; l.ImageTransform = matrix2x3;
            if (moveWalls && old is { Length: 6 })
            {
                var det = old[0] * old[4] - old[1] * old[3];
                if (Math.Abs(det) > 1e-12)
                {
                    // inverse of old, then the new transform: maps a point of the old plan to where it now lies
                    double ia = old[4] / det, ib = -old[1] / det, ic = -old[3] / det, id = old[0] / det;
                    double itx = -(ia * old[2] + ib * old[5]), ity = -(ic * old[2] + id * old[5]);
                    double fa = matrix2x3[0] * ia + matrix2x3[1] * ic, fb = matrix2x3[0] * ib + matrix2x3[1] * id, ftx = matrix2x3[0] * itx + matrix2x3[1] * ity + matrix2x3[2];
                    double fc = matrix2x3[3] * ia + matrix2x3[4] * ic, fd = matrix2x3[3] * ib + matrix2x3[4] * id, fty = matrix2x3[3] * itx + matrix2x3[4] * ity + matrix2x3[5];
                    PointMm M(PointMm p) => new((int)Math.Round(fa * p.X + fb * p.Y + ftx), (int)Math.Round(fc * p.X + fd * p.Y + fty));
                    foreach (var w in l.Walls) { w.Footprint = w.Footprint.Select(M).ToList(); w.Holes = w.Holes.Select(h => h.Select(M).ToList()).ToList(); }
                    foreach (var sp in l.Spaces) sp.Outline = sp.Outline.Select(M).ToList();
                }
            }
            return levelId;
        }, i => i);

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
            foreach (var n in net.Nodes.Where(x => points.Contains(x.Position)).ToList()) SplitVerticalAt(net, n);     // an end on a riser joins it (elbow / tee)
            return (IReadOnlyList<Segment>)segs;
        }, _ => networkId);

    /// <summary>When the node lies strictly inside a vertical segment of the network (same X, Y), splits that segment at the node so the two are connected.</summary>
    private static void SplitVerticalAt(Network net, Node n)
    {
        var p = n.Position;
        foreach (var seg in net.Segments.ToList())
        {
            if (seg.A == n.Id || seg.B == n.Id) continue;
            var a = net.Nodes.First(x => x.Id == seg.A).Position; var b = net.Nodes.First(x => x.Id == seg.B).Position;
            if (a.X != p.X || a.Y != p.Y || b.X != p.X || b.Y != p.Y || p.Z <= Math.Min(a.Z, b.Z) || p.Z >= Math.Max(a.Z, b.Z)) continue;
            var oldB = seg.B; seg.B = n.Id; net.Segments.Add(new Segment { A = n.Id, B = oldB, DiameterMm = seg.DiameterMm, Material = seg.Material });
            return;
        }
    }

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

    /// <summary>Vertical pipe on a wall face (elevation view): offset from the face, from zFrom to zTo (snap level lines before calling).</summary>
    public Network AddVerticalPipe(Geometry.ElevationPlane plane, double u, NetworkType type, int zFromMm, int zToMm, int diameterMm, Guid? networkId = null) =>
        Edit("Add vertical pipe", BuildingEventKind.NetworkChanged, () =>
        {
            var net = networkId is { } id ? Net(id) : new Network { Name = type.ToString(), Type = type };
            if (networkId == null) building.Networks.Add(net);
            var p = plane.ToPlan(u);
            var a = new Node { Position = new(p.X, p.Y, Math.Min(zFromMm, zToMm)) }; var b2 = new Node { Position = new(p.X, p.Y, Math.Max(zFromMm, zToMm)) };
            net.Nodes.Add(a); net.Nodes.Add(b2); net.Segments.Add(new Segment { A = a.Id, B = b2.Id, DiameterMm = diameterMm, Material = Networks.NetworkQueries.DefaultMaterial(type) });
            return net;
        }, n => n.Id);

    /// <summary>Horizontal pipe along the wall face at a given altitude, from u0 to u1.</summary>
    public Network AddHorizontalAlong(Geometry.ElevationPlane plane, double u0, double u1, NetworkType type, int zMm, int diameterMm, Guid? networkId = null) =>
        Edit("Add horizontal pipe", BuildingEventKind.NetworkChanged, () =>
        {
            var net = networkId is { } id ? Net(id) : new Network { Name = type.ToString(), Type = type };
            if (networkId == null) building.Networks.Add(net);
            var p0 = plane.ToPlan(u0); var p1 = plane.ToPlan(u1);
            var a = new Node { Position = new(p0.X, p0.Y, zMm) }; var b2 = new Node { Position = new(p1.X, p1.Y, zMm) };
            net.Nodes.Add(a); net.Nodes.Add(b2); net.Segments.Add(new Segment { A = a.Id, B = b2.Id, DiameterMm = diameterMm, Material = Networks.NetworkQueries.DefaultMaterial(type) });
            return net;
        }, n => n.Id);

    // ---- Direct manipulation of pipes (handles). Each is one history step; segments stay axis-aligned. ----

    private static bool AxisAligned(PointMm3 a, PointMm3 b) => ((a.X != b.X ? 1 : 0) + (a.Y != b.Y ? 1 : 0) + (a.Z != b.Z ? 1 : 0)) <= 1;

    /// <summary>Extends/shortens a free end along its segment axis to a new point. Returns false (no change) when the move would not stay on the axis.</summary>
    public bool ExtendEnd(Guid networkId, Guid nodeId, PointMm3 to)
    {
        var net = Net(networkId); var seg = net.Segments.Where(s => s.A == nodeId || s.B == nodeId).ToList();
        if (seg.Count != 1) return false;
        var other = net.Nodes.First(n => n.Id == (seg[0].A == nodeId ? seg[0].B : seg[0].A)).Position;
        if (!AxisAligned(other, to) || other == to || net.Nodes.First(n => n.Id == nodeId).Position == to) return false;
        Edit("Extend pipe", BuildingEventKind.NetworkChanged, () => { net.Nodes.First(n => n.Id == nodeId).Position = to; return nodeId; }, i => i);
        return true;
    }

    /// <summary>Moves a free end anywhere (also off the segment's axis, which makes the pipe oblique). False (no change) when the node is not a free end or does not move.</summary>
    public bool MoveEnd(Guid networkId, Guid nodeId, PointMm3 to)
    {
        var net = Net(networkId); var seg = net.Segments.Where(s => s.A == nodeId || s.B == nodeId).ToList();
        if (seg.Count != 1) return false;
        var other = net.Nodes.First(n => n.Id == (seg[0].A == nodeId ? seg[0].B : seg[0].A)).Position;
        if (other == to || net.Nodes.First(n => n.Id == nodeId).Position == to) return false;
        Edit("Move pipe end", BuildingEventKind.NetworkChanged, () => { net.Nodes.First(n => n.Id == nodeId).Position = to; return nodeId; }, i => i);
        return true;
    }

    /// <summary>Creates an elbow at a free end: a new segment along another axis (X, Y or Z) to the given point.</summary>
    public Node? AddElbow(Guid networkId, Guid endNodeId, PointMm3 to, int? diameterMm = null)
    {
        var net = Net(networkId); var end = net.Nodes.First(n => n.Id == endNodeId);
        if (!AxisAligned(end.Position, to) || end.Position == to) return null;
        var d = diameterMm ?? net.Segments.First(s => s.A == endNodeId || s.B == endNodeId).DiameterMm;
        return Edit("Add elbow", BuildingEventKind.NetworkChanged, () =>
        {
            var n = new Node { Position = to }; net.Nodes.Add(n); net.Segments.Add(new Segment { A = endNodeId, B = n.Id, DiameterMm = d, Material = net.Segments.First(s => s.A == endNodeId || s.B == endNodeId).Material }); return n;
        }, n => n.Id);
    }

    /// <summary>Tee: splits the segment at <paramref name="at"/> and starts a branch to <paramref name="branchEnd"/> (axis-aligned with the tee point).</summary>
    public Node? AddTeeBranch(Guid networkId, Guid segmentId, PointMm3 at, PointMm3 branchEnd, int? diameterMm = null)
    {
        var net = Net(networkId); var seg = net.Segments.First(s => s.Id == segmentId);
        if (!AxisAligned(at, branchEnd) || at == branchEnd) return null;
        return Edit("Add tee", BuildingEventKind.NetworkChanged, () =>
        {
            var tee = new Node { Position = at }; var end = new Node { Position = branchEnd }; net.Nodes.Add(tee); net.Nodes.Add(end);
            var b = seg.B; seg.B = tee.Id;
            net.Segments.Add(new Segment { A = tee.Id, B = b, DiameterMm = seg.DiameterMm, Material = seg.Material });
            net.Segments.Add(new Segment { A = tee.Id, B = end.Id, DiameterMm = diameterMm ?? seg.DiameterMm, Material = seg.Material });
            return end;
        }, n => n.Id);
    }

    /// <summary>Moves a whole run (the segment and the collinear segments chained to it) perpendicular to itself by <paramref name="delta"/>; connected segments along the move axis are stretched. Refuses (false, no change) when a connection would become diagonal.</summary>
    public bool MoveRun(Guid networkId, Guid segmentId, int dx, int dy, int dz)
    {
        var net = Net(networkId); var seg = net.Segments.First(s => s.Id == segmentId);
        var a = net.Nodes.First(n => n.Id == seg.A).Position; var b = net.Nodes.First(n => n.Id == seg.B).Position;
        // the run: follow collinear neighbours at both ends
        var run = new HashSet<Guid> { seg.A, seg.B }; var queue = new Queue<Guid>(run);
        while (queue.Count > 0)
        {
            var nid = queue.Dequeue(); var np = net.Nodes.First(n => n.Id == nid).Position;
            var attached = net.Segments.Where(s => s.A == nid || s.B == nid).ToList();
            if (attached.Count != 2) continue;
            foreach (var s in attached) { var o = s.A == nid ? s.B : s.A; var op = net.Nodes.First(n => n.Id == o).Position;
                if (SameAxis(a, b, np, op) && run.Add(o)) queue.Enqueue(o); }
        }
        // a run end whose other segment (a riser, a perpendicular pipe) would stop being axis-aligned stays where it is; the run leaves it through a short jog
        PointMm3 Shift(PointMm3 p) => new(p.X + dx, p.Y + dy, p.Z + dz);
        var anchored = new HashSet<Guid>();
        foreach (var s in net.Segments)
        {
            var inA = run.Contains(s.A); var inB = run.Contains(s.B); if (inA == inB) continue;
            var pa = net.Nodes.First(n => n.Id == s.A).Position; var pb = net.Nodes.First(n => n.Id == s.B).Position;
            if (!AxisAligned(inA ? Shift(pa) : pa, inB ? Shift(pb) : pb)) anchored.Add(inA ? s.A : s.B);
        }
        if (anchored.Count > 0 && !AxisAligned(default, new PointMm3(dx, dy, dz))) return false;                      // the jog itself must be along one axis
        Edit("Move pipe run", BuildingEventKind.NetworkChanged, () =>
        {
            foreach (var n in net.Nodes.Where(n => run.Contains(n.Id) && !anchored.Contains(n.Id)).ToList()) n.Position = Shift(n.Position);
            var runSegs = net.Segments.Where(s => run.Contains(s.A) && run.Contains(s.B)).ToList();
            foreach (var id in anchored)
            {
                var old = net.Nodes.First(n => n.Id == id); var moved = new Node { Position = Shift(old.Position) }; net.Nodes.Add(moved);
                var inRun = runSegs.Where(s => s.A == id || s.B == id).ToList();
                foreach (var s in inRun) { if (s.A == id) s.A = moved.Id; else s.B = moved.Id; }
                var like = inRun.FirstOrDefault() ?? net.Segments.First(); net.Segments.Add(new Segment { A = id, B = moved.Id, DiameterMm = like.DiameterMm, Material = like.Material });
            }
            return networkId;
        }, i => i);
        return true;
    }

    private static bool SameAxis(PointMm3 a, PointMm3 b, PointMm3 c, PointMm3 d)
    {
        var ax = (a.X != b.X ? 1 : 0, a.Y != b.Y ? 1 : 0, a.Z != b.Z ? 1 : 0); var bx = (c.X != d.X ? 1 : 0, c.Y != d.Y ? 1 : 0, c.Z != d.Z ? 1 : 0);
        if (ax != bx) return false;
        // same line, not just same direction
        return (ax.Item1 == 1 || (a.X == c.X)) && (ax.Item2 == 1 || (a.Y == c.Y)) && (ax.Item3 == 1 || (a.Z == c.Z));
    }

    /// <summary>Moves the end node of a vertical run to a new altitude (top/bottom handle). Refuses when the node is not the end of a vertical segment.</summary>
    public bool SetNodeAltitude(Guid networkId, Guid nodeId, int zMm)
    {
        var net = Net(networkId); var node = net.Nodes.First(n => n.Id == nodeId); var segs = net.Segments.Where(s => s.A == nodeId || s.B == nodeId).ToList();
        if (segs.Count != 1) return false;
        var other = net.Nodes.First(n => n.Id == (segs[0].A == nodeId ? segs[0].B : segs[0].A)).Position;
        if (other.X != node.Position.X || other.Y != node.Position.Y || other.Z == zMm || zMm == node.Position.Z) return false;      // must stay vertical and keep a length
        Edit("Riser extent", BuildingEventKind.NetworkChanged, () => { node.Position = node.Position with { Z = zMm }; return nodeId; }, i => i);
        return true;
    }

    public void MoveNode(Guid networkId, Guid nodeId, PointMm3 p) =>
        Edit("Move node", BuildingEventKind.NetworkChanged, () => { Net(networkId).Nodes.First(n => n.Id == nodeId).Position = p; return nodeId; }, i => i);

    public void DeleteSegment(Guid networkId, Guid segId) =>
        Edit("Delete segment", BuildingEventKind.NetworkChanged, () =>
        {   // nodes left without a segment go with it, and so does a network that becomes empty
            var n = Net(networkId); n.Segments.RemoveAll(s => s.Id == segId);
            n.Nodes.RemoveAll(x => !n.Segments.Any(s => s.A == x.Id || s.B == x.Id));
            if (n.Segments.Count == 0) building.Networks.Remove(n);
            return segId;
        }, i => i);

    public void DeleteSpace(Guid levelId, Guid spaceId) =>
        Edit("Delete space", BuildingEventKind.LevelChanged, () => { Find(levelId).Spaces.RemoveAll(s => s.Id == spaceId); return spaceId; }, i => i);

    public void SetSegmentDiameter(Guid networkId, Guid segId, int diameterMm) =>
        Edit("Change diameter", BuildingEventKind.NetworkChanged, () => { Net(networkId).Segments.First(s => s.Id == segId).DiameterMm = diameterMm; return segId; }, i => i);

    /// <summary>Changes the type of a connected run of pipes: every segment reachable from the given one moves to a network of the new type (created when there is none). Null (no change) when the type is the same or the network is locked. One history step.</summary>
    public Network? ChangeRunType(Guid networkId, Guid segmentId, NetworkType type)
    {
        var src = Net(networkId); if (src.Type == type || src.Locked) return null;
        return Edit("Change pipe type", BuildingEventKind.NetworkChanged, () =>
        {
            var nodes = new HashSet<Guid>(); var run = new HashSet<Guid>(); var first = src.Segments.First(x => x.Id == segmentId); nodes.Add(first.A); nodes.Add(first.B); run.Add(first.Id);
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var sg in src.Segments.Where(x => !run.Contains(x.Id) && (nodes.Contains(x.A) || nodes.Contains(x.B)))) { run.Add(sg.Id); nodes.Add(sg.A); nodes.Add(sg.B); grew = true; }
            }
            var dst = building.Networks.FirstOrDefault(n => n.Type == type && !n.Locked && n != src) ?? new Network { Name = type.ToString(), Type = type };
            if (!building.Networks.Contains(dst)) building.Networks.Add(dst);
            dst.Nodes.AddRange(src.Nodes.Where(n => nodes.Contains(n.Id))); dst.Segments.AddRange(src.Segments.Where(x => run.Contains(x.Id)));
            src.Nodes.RemoveAll(n => nodes.Contains(n.Id)); src.Segments.RemoveAll(x => run.Contains(x.Id));
            if (src.Segments.Count == 0 && src.Nodes.Count == 0) building.Networks.Remove(src);
            return dst;
        }, n => n.Id);
    }

    /// <summary>Same diameter for several segments (a whole riser) as one history step.</summary>
    public void SetSegmentsDiameter(Guid networkId, IReadOnlyCollection<Guid> segIds, int diameterMm) =>
        Edit("Change diameter", BuildingEventKind.NetworkChanged, () => { foreach (var s in Net(networkId).Segments.Where(x => segIds.Contains(x.Id))) s.DiameterMm = diameterMm; return networkId; }, i => i);

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

    /// <summary>Changes the thickness of rectangular walls symmetrically about their centre line (other footprints are left unchanged).</summary>
    public int SetWallThickness(Guid levelId, IEnumerable<Guid> wallIds, int thicknessMm)
    {
        var ids = wallIds.ToHashSet(); var changed = 0;
        var l = Find(levelId);
        if (!l.Walls.Any(w => ids.Contains(w.Id) && w.Footprint.Count == 4 && Geometry.WallGeometry.Axis(w) != null)) return 0;
        Edit("Wall thickness", BuildingEventKind.WallChanged, () =>
        {
            foreach (var w in l.Walls.Where(w => ids.Contains(w.Id) && w.Footprint.Count == 4))
                if (Geometry.WallGeometry.Axis(w) is { } ax) { w.Footprint = Geometry.WallGeometry.Rectangle(ax.A, ax.B, thicknessMm).ToList(); changed++; }
            return levelId;
        }, i => i);
        return changed;
    }

    public void MoveOpening(Guid levelId, Guid wallId, Guid openingId, int positionMm, int? widthMm = null) =>
        Edit("Move opening", BuildingEventKind.WallChanged, () =>
        {
            var o = Find(levelId).Walls.First(w => w.Id == wallId).Openings.First(o => o.Id == openingId); o.PositionMm = positionMm; if (widthMm is { } wd) o.WidthMm = wd; return openingId;
        }, i => i);

    /// <summary>Edits a door/window: size, sill, swing, label. One history step.</summary>
    public void SetOpeningProperties(Guid levelId, Guid wallId, Guid openingId, int widthMm, int heightMm, int sillMm, bool swingLeft, bool swingOut, string? label) =>
        Edit("Opening properties", BuildingEventKind.WallChanged, () =>
        {
            var o = Find(levelId).Walls.First(w => w.Id == wallId).Openings.First(o => o.Id == openingId);
            o.WidthMm = Math.Max(100, widthMm); o.HeightMm = Math.Max(100, heightMm); o.SillMm = Math.Max(0, sillMm); o.SwingLeft = swingLeft; o.SwingOut = swingOut; o.Label = label;
            return openingId;
        }, i => i);

    public void DeleteOpening(Guid levelId, Guid wallId, Guid openingId) =>
        Edit("Delete opening", BuildingEventKind.WallChanged, () => { Find(levelId).Walls.First(w => w.Id == wallId).Openings.RemoveAll(o => o.Id == openingId); return openingId; }, i => i);

    public void SetLevelKind(Guid levelId, LevelKind kind) =>
        Edit("Level kind", BuildingEventKind.LevelChanged, () => { Find(levelId).Kind = kind; return levelId; }, i => i);

    public void SetSpaceProperties(Guid levelId, Guid spaceId, string name, SpaceKind kind, string? number, string? lot) =>
        Edit("Space properties", BuildingEventKind.LevelChanged, () =>
        {
            var s = Find(levelId).Spaces.First(x => x.Id == spaceId); s.Name = name; s.Kind = kind; s.Number = number; s.Lot = lot; s.Stability = Spaces.DefaultFor(Find(levelId).Kind, kind); return spaceId;
        }, i => i);

    /// <summary>Adds a level directly above (or below) the given one, using its height. One history step.</summary>
    public Level AddLevelNextTo(Guid levelId, bool above, string name)
    {
        var l = Find(levelId);
        return AddLevel(name, above ? l.AltitudeMm + l.HeightMm : l.AltitudeMm - 2700, above ? l.HeightMm : 2700, above ? LevelKind.Standard : LevelKind.Basement);
    }

    public void SetSite(SiteInfo? site) =>
        Edit("Set site", BuildingEventKind.LevelChanged, () => { building.Site = site; return Guid.Empty; }, _ => null);

    /// <summary>Moves the facade walls onto the footprint edges (the suggestion computed by <see cref="Geometry.FootprintFit"/>). One history step.</summary>
    public int FitOuterWalls(Guid levelId, IReadOnlyList<Geometry.WallShift> shifts) =>
        Edit("Fit outer walls to cadastre", BuildingEventKind.WallChanged, () =>
        {
            var l = Find(levelId); var n = 0;
            foreach (var sh in shifts)
                if (l.Walls.FirstOrDefault(w => w.Id == sh.WallId) is { } w)
                { w.Footprint = w.Footprint.Select(p => sh.Moves.TryGetValue(p, out var q) ? q : p).ToList(); n++; }
            return n;
        }, _ => levelId);

    /// <summary>Scales a level about a pivot (the origin by default) (the cadastre says the plan is too wide / too long): the background and, optionally, the walls. One history step.</summary>
    public void ScaleLevel(Guid levelId, double sx, double sy, bool moveWalls = true, double pivotX = 0, double pivotY = 0) =>
        Edit("Scale level", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(levelId); var t = l.ImageTransform;
            if (t is { Length: 6 }) l.ImageTransform = [t[0] * sx, t[1] * sx, t[2] * sx + (1 - sx) * pivotX, t[3] * sy, t[4] * sy, t[5] * sy + (1 - sy) * pivotY];
            if (moveWalls)
            {
                PointMm M(PointMm p) => new((int)Math.Round(pivotX + (p.X - pivotX) * sx), (int)Math.Round(pivotY + (p.Y - pivotY) * sy));
                foreach (var w in l.Walls) { w.Footprint = w.Footprint.Select(M).ToList(); w.Holes = w.Holes.Select(h => h.Select(M).ToList()).ToList(); }
                foreach (var sp in l.Spaces) sp.Outline = sp.Outline.Select(M).ToList();
            }
            return levelId;
        }, i => i);

    public ReferenceMesh AddReference(Guid levelId, ReferenceMesh mesh) =>
        Edit("Add reference model", BuildingEventKind.LevelChanged, () => { Find(levelId).References.Add(mesh); return mesh; }, _ => levelId);

    public void RemoveReference(Guid levelId, string name) =>
        Edit("Remove reference model", BuildingEventKind.LevelChanged, () => { Find(levelId).References.RemoveAll(r => r.Name == name); return levelId; }, i => i);

    public void SetStability(Guid levelId, Guid spaceId, Stability stability) =>
        Edit("Set stability", BuildingEventKind.LevelChanged, () => { Find(levelId).Spaces.First(s => s.Id == spaceId).Stability = stability; return spaceId; }, i => i);

    /// <summary>Numbers the given spaces in order ("Cave 1", "Cave 2"…) as one history step; also labels their doors.</summary>
    public void NumberSpaces(Guid levelId, IReadOnlyList<Guid> spaceIds, string? prefix = null, int start = 1) =>
        Edit("Number spaces", BuildingEventKind.LevelChanged, () =>
        {
            var l = Find(levelId); var n = start;
            foreach (var id in spaceIds)
            {
                var sp = l.Spaces.First(x => x.Id == id); sp.Number = n.ToString(); sp.Name = prefix == null || prefix == Translations.T("cellar.prefix") ? Translations.Token("cellar.name", n) : prefix + n;
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

    /// <summary>Pure preview of Straighten: the new outline of each wall and nothing changed in the model.</summary>
    public static IReadOnlyList<(Guid WallId, List<PointMm> Outline)> PreviewStraighten(Level l, IEnumerable<Guid> wallIds, double toleranceMm, double angleDeg, IReadOnlyList<double>? referenceAngles = null)
    {
        var ids = wallIds.ToHashSet(); var res = new List<(Guid, List<PointMm>)>();
        foreach (var w in l.Walls.Where(w => ids.Contains(w.Id)))
            res.Add((w.Id, Plan.WallStraightener  .Straighten(w.Footprint.Select(p => new Plan.PointPx(p.X, p.Y)).ToList(), toleranceMm, angleDeg, referenceAngles).Select(p => new PointMm((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToList()));
        return res;
    }

    /// <summary>Straighten the given walls (one history step), then snap their corners onto the faces of neighbouring walls within the tolerance so junctions stay closed.</summary>
    public void StraightenWalls(Guid levelId, IEnumerable<Guid> wallIds, double toleranceMm = 30, double angleDeg = 5, IReadOnlyList<double>? referenceAngles = null) =>
        Edit("Straighten walls", BuildingEventKind.WallChanged, () =>
        {
            var l = Find(levelId); var ids = wallIds.ToHashSet(); var preview = PreviewStraighten(l, ids, toleranceMm, angleDeg, referenceAngles).ToDictionary(x => x.WallId, x => x.Outline);
            foreach (var w in l.Walls.Where(w => ids.Contains(w.Id))) w.Footprint = preview[w.Id];
            SnapToNeighbours(l, ids, toleranceMm);
            return levelId;
        }, i => i);

    /// <summary>"Make rectangular": replaces each wall by the best-fitting oriented rectangle. One history step.</summary>
    public void MakeWallsRectangular(Guid levelId, IEnumerable<Guid> wallIds) =>
        Edit("Make rectangular", BuildingEventKind.WallChanged, () =>
        {
            var l = Find(levelId); var ids = wallIds.ToHashSet();
            foreach (var w in l.Walls.Where(w => ids.Contains(w.Id) && w.Footprint.Count >= 3))
                w.Footprint = Plan.WallShapes.MakeRectangular(w.Footprint.Select(p => new Plan.PointPx(p.X, p.Y)).ToList()).Select(p => new PointMm((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToList();
            return levelId;
        }, i => i);

    private static void SnapToNeighbours(Level l, HashSet<Guid> moved, double tol)
    {
        var all = l.Walls.Select(w => (IReadOnlyList<Plan.PointPx>)w.Footprint.Select(p => new Plan.PointPx(p.X, p.Y)).ToList()).ToList();
        var snapped = Plan.WallShapes.SnapJunctions(all, tol);
        for (var i = 0; i < l.Walls.Count; i++)
            if (moved.Contains(l.Walls[i].Id)) l.Walls[i].Footprint = snapped[i].Select(p => new PointMm((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToList();
    }

    /// <summary>Several risers at once (hot+cold…) at fixed spacing along a row (east by default, turned by <paramref name="quarterTurns"/> quarter turns counter-clockwise about the first), from zFrom to zTo. One history step.</summary>
    public IReadOnlyList<Network> AddRiserTemplate(PointMm origin, int spacingMm, IReadOnlyList<NetworkType> types, int zFromMm, int zToMm, int diameterMm = 20, int quarterTurns = 0) =>
        Edit("Add riser template", BuildingEventKind.NetworkChanged, () =>
        {
            var res = new List<Network>(); var (sx, sy) = Tools.RiserSnap.RowStep(quarterTurns);
            for (var i = 0; i < types.Count; i++)
            {
                var n = new Network { Name = "Riser " + types[i], Type = types[i] };
                var a = new Node { Position = new(origin.X + sx * i * spacingMm, origin.Y + sy * i * spacingMm, zFromMm) };
                var b = new Node { Position = new(origin.X + sx * i * spacingMm, origin.Y + sy * i * spacingMm, zToMm) };
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
