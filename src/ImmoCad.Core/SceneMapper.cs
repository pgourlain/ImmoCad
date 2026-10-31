using ImmoCad.Core.Geometry;
using ImmoCad.Core.Model;
using ImmoCad.Core.Networks;
using ImmoCad.Scene;

namespace ImmoCad.Core;

/// <summary>Maps the building model onto Scene's plain input records (Scene stays model-free).</summary>
public static class SceneMapper
{
    /// <summary>
    /// A wall without openings is one extrusion. A wall with openings is cut in horizontal bands at the sills and tops: in each band, the openings that span it are
    /// taken out of the outline, so the lintel above a door and the part under a window stay solid. All parts keep the wall's id (selection and picking).
    /// </summary>
    private static IEnumerable<SceneWall> WallParts(Wall w, Level l, int index)
    {
        var footprint = w.Footprint.Select(p => (p.X, p.Y)).ToList(); var holes = w.Holes.Select(h => (IReadOnlyList<(int X, int Y)>)h.Select(p => (p.X, p.Y)).ToList()).ToList(); var height = w.HeightMm ?? l.HeightMm;
        var spans = w.Openings.Select(o => WallGeometry.SpanOf(w, o)).Where(s => s is { } s2 && s2.TopMm > 0 && s2.SillMm < height).Select(s => s!.Value).ToList();
        if (spans.Count == 0) { yield return new SceneWall(w.Id, index, footprint, holes, l.AltitudeMm, height); yield break; }
        var cuts = new SortedSet<int> { 0, height }; foreach (var s in spans) { cuts.Add(Math.Clamp(s.SillMm, 0, height)); cuts.Add(Math.Clamp(s.TopMm, 0, height)); }
        var levels = cuts.ToList();
        for (var k = 0; k + 1 < levels.Count; k++)
        {
            int z0 = levels[k], z1 = levels[k + 1]; if (z1 - z0 < 1) continue;
            var shapes = new List<Shape> { new(w.Footprint.ToList(), w.Holes.Select(h => h.ToList()).ToList()) };
            foreach (var s in spans.Where(s => s.SillMm <= z0 && s.TopMm >= z1)) shapes = shapes.SelectMany(sh => PolygonOps.Subtract(sh, s.Zone())).ToList();
            foreach (var sh in shapes)
                yield return new SceneWall(w.Id, index, sh.Outline.Select(p => (p.X, p.Y)).ToList(), sh.Holes.Select(h => (IReadOnlyList<(int X, int Y)>)h.Select(p => (p.X, p.Y)).ToList()).ToList(), l.AltitudeMm + z0, z1 - z0);
        }
    }

    public static SceneInput Map(Building b)
    {
        var levels = b.Levels.Select((l, i) => new SceneLevel(l.Id, i, l.AltitudeMm, l.HeightMm)).ToList();
        var walls = b.Levels.SelectMany((l, i) => l.Walls.SelectMany(w => WallParts(w, l, i))).ToList();
        var pipes = new List<ScenePipe>(); var fittings = new List<SceneFitting>();
        foreach (var n in b.Networks)
        {
            var c = NetworkQueries.ColorOf(n.Type); var col = (c.R / 255f, c.G / 255f, c.B / 255f);
            foreach (var s in n.Segments)
            {
                var a = n.Nodes.First(x => x.Id == s.A).Position; var e = n.Nodes.First(x => x.Id == s.B).Position;
                pipes.Add(new ScenePipe(n.Id, s.Id, (a.X, a.Y, a.Z), (e.X, e.Y, e.Z), s.DiameterMm, col, n.Visible));
            }
            foreach (var f in FittingResolver.Resolve(n, b.Levels))
            {
                if (f.Kind is FittingKind.EndCap or FittingKind.Joint) continue;
                var arms = n.Segments.Where(s => s.A == f.NodeId || s.B == f.NodeId).Select(s => n.Nodes.First(x => x.Id == (s.A == f.NodeId ? s.B : s.A)).Position).ToList();
                var d = n.Segments.Where(s => s.A == f.NodeId || s.B == f.NodeId).Max(s => s.DiameterMm);
                (int, int, int)? A(int i) => i < arms.Count ? (arms[i].X, arms[i].Y, arms[i].Z) : null;
                fittings.Add(new SceneFitting(n.Id, f.NodeId, f.Kind.ToString(), (f.Position.X, f.Position.Y, f.Position.Z), A(0), A(1), d, col, n.Visible));
            }
        }
        var boxes = new List<SceneBox>();
        foreach (var e in b.Equipment)
        {
            var cat = Catalog.EquipmentCatalog.Find(e.CatalogId); var lv = b.Levels.FirstOrDefault(l => l.Id == e.LevelId); var baseZ = (lv?.AltitudeMm ?? 0) + e.ElevationMm;
            if (cat == null) { boxes.Add(new SceneBox(e.Id, e.LevelId, e.CatalogId, e.Position.X, e.Position.Y, baseZ, 500, 100, 500, (0.35f, 0.35f, 0.8f))); continue; }
            foreach (var p in Catalog.Symbol.Parts(cat, e.Position, baseZ, e.RotationDeg))
                boxes.Add(new SceneBox(e.Id, e.LevelId, cat.Name, p.X, p.Y, p.Z, p.W, p.D, p.H, cat.Category == "Sanitary" ? (0.92f, 0.92f, 0.95f) : (0.35f, 0.35f, 0.8f), p.Type == "cylinder" ? p.R : 0));
        }
        return new SceneInput(levels, walls, pipes, fittings, boxes);
    }
}
