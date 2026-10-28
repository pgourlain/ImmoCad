using ImmoCad.Core.Model;
using ImmoCad.Core.Networks;
using ImmoCad.Scene;

namespace ImmoCad.Core;

/// <summary>Maps the building model onto Scene's plain input records (Scene stays model-free).</summary>
public static class SceneMapper
{
    public static SceneInput Map(Building b)
    {
        var levels = b.Levels.Select((l, i) => new SceneLevel(l.Id, i, l.AltitudeMm, l.HeightMm)).ToList();
        var walls = b.Levels.SelectMany((l, i) => l.Walls.Select(w => new SceneWall(w.Id, i, w.Footprint.Select(p => (p.X, p.Y)).ToList(),
            w.Holes.Select(h => (IReadOnlyList<(int X, int Y)>)h.Select(p => (p.X, p.Y)).ToList()).ToList(), l.AltitudeMm, w.HeightMm ?? l.HeightMm))).ToList();
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
