namespace ImmeubleCad.Scene;

public enum SceneKind { Wall, Slab, Pipe, Fitting, Component, Equipment }
public enum LevelFilter { CurrentOnly, CurrentAndBelow, WholeBuilding }

/// <summary>One drawable thing with the id of the model object it came from (for picking and shared selection).</summary>
public sealed record SceneObject(Guid Id, SceneKind Kind, string Name, Mesh Mesh, (float R, float G, float B) Color, float Alpha, bool Highlight = false);

/// <summary>Input to the builder, Scene stays free of Core types: the caller (Core/Desktop) maps its model onto these.</summary>
public sealed record SceneWall(Guid Id, int LevelIndex, IReadOnlyList<(int X, int Y)> Footprint, IReadOnlyList<IReadOnlyList<(int X, int Y)>> Holes, int BaseZ, int Height);
public sealed record SceneLevel(Guid Id, int Index, int AltitudeMm, int HeightMm);
public sealed record ScenePipe(Guid NetworkId, Guid SegmentId, (int X, int Y, int Z) A, (int X, int Y, int Z) B, int DiameterMm, (float R, float G, float B) Color, bool Visible);
public sealed record SceneFitting(Guid NetworkId, Guid NodeId, string Kind, (int X, int Y, int Z) At, (int X, int Y, int Z)? ArmA, (int X, int Y, int Z)? ArmB, int DiameterMm, (float R, float G, float B) Color, bool Visible);
public sealed record SceneBox(Guid Id, Guid LevelId, string Name, int X, int Y, int Z, int W, int D, int H, (float R, float G, float B) Color);
public sealed record SceneInput(IReadOnlyList<SceneLevel> Levels, IReadOnlyList<SceneWall> Walls, IReadOnlyList<ScenePipe> Pipes, IReadOnlyList<SceneFitting> Fittings, IReadOnlyList<SceneBox> Boxes);

public sealed record SceneOptions(Guid? CurrentLevel, LevelFilter Filter = LevelFilter.WholeBuilding, bool CutAboveCurrent = true, bool XRay = false, bool Slabs = true, ISet<Guid>? Selection = null);

public static class SceneBuilder
{
    public static readonly (float, float, float) WallColor = (0.78f, 0.78f, 0.75f), SlabColor = (0.62f, 0.62f, 0.6f);

    public static List<SceneObject> Build(SceneInput input, SceneOptions o)
    {
        var res = new List<SceneObject>();
        var cur = input.Levels.FirstOrDefault(l => l.Id == o.CurrentLevel);
        bool LevelVisible(int index)
        {
            if (cur == null) return true;
            if (o.CutAboveCurrent && index > cur.Index) return false;
            return o.Filter switch { LevelFilter.CurrentOnly => index == cur.Index, LevelFilter.CurrentAndBelow => index <= cur.Index, _ => true };
        }
        var wallAlpha = o.XRay ? 0.28f : 1f;
        foreach (var w in input.Walls.Where(w => LevelVisible(w.LevelIndex)))
        {
            var m = MeshBuilder.ExtrudeWall(w.Footprint, w.BaseZ, w.Height);
            foreach (var h in w.Holes) MeshBuilder.ExtrudeSides(h, w.BaseZ, w.Height, m);
            m.ComputeNormals(); res.Add(new SceneObject(w.Id, SceneKind.Wall, "wall", m, WallColor, wallAlpha, o.Selection?.Contains(w.Id) == true));
        }
        if (o.Slabs)
            foreach (var l in input.Levels.Where(l => LevelVisible(l.Index)))
            {
                var pts = input.Walls.Where(w => w.LevelIndex == l.Index).SelectMany(w => w.Footprint).ToList(); if (pts.Count < 3) continue;
                var m = MeshBuilder.Slab(pts, l.AltitudeMm); m.ComputeNormals(); res.Add(new SceneObject(l.Id, SceneKind.Slab, "slab", m, SlabColor, o.XRay ? 0.15f : 1f));
            }
        bool ZVisible(int z) => cur == null || !o.CutAboveCurrent || z <= cur.AltitudeMm + cur.HeightMm;
        foreach (var p in input.Pipes.Where(p => p.Visible && ZVisible(Math.Min(p.A.Z, p.B.Z))))
        {
            var m = MeshBuilder.Cylinder(p.A, p.B, p.DiameterMm); m.ComputeNormals();
            res.Add(new SceneObject(p.SegmentId, SceneKind.Pipe, "pipe", m, p.Color, 1f, o.Selection?.Contains(p.SegmentId) == true));
        }
        foreach (var f in input.Fittings.Where(f => f.Visible && ZVisible(f.At.Z)))
        {
            Mesh? m = null;
            if (f.Kind == "Elbow90" && f.ArmA is { } a && f.ArmB is { } b) m = MeshBuilder.Elbow(f.At, a, b, f.DiameterMm);
            else if (f.Kind is "Tee" or "Cross" or "Reducer" or "SlabCrossing") m = MeshBuilder.Box(f.At.X - f.DiameterMm, f.At.Y - f.DiameterMm, f.At.Z - f.DiameterMm, f.At.X + f.DiameterMm, f.At.Y + f.DiameterMm, f.At.Z + f.DiameterMm);
            else if (f.Kind == "Component") m = MeshBuilder.Box(f.At.X - f.DiameterMm, f.At.Y - f.DiameterMm, f.At.Z - f.DiameterMm / 2, f.At.X + f.DiameterMm, f.At.Y + f.DiameterMm, f.At.Z + f.DiameterMm / 2);
            if (m == null) continue; m.ComputeNormals(); res.Add(new SceneObject(f.NodeId, f.Kind == "Component" ? SceneKind.Component : SceneKind.Fitting, f.Kind, m, f.Kind == "Component" ? (0.15f, 0.15f, 0.15f) : f.Color, 1f));
        }
        foreach (var b in input.Boxes.Where(b => LevelVisible(input.Levels.FirstOrDefault(l => l.Id == b.LevelId)?.Index ?? 0)))
        {
            var m = MeshBuilder.Box(b.X, b.Y, b.Z, b.X + b.W, b.Y + b.D, b.Z + b.H); m.ComputeNormals(); res.Add(new SceneObject(b.Id, SceneKind.Equipment, b.Name, m, b.Color, 1f, o.Selection?.Contains(b.Id) == true));
        }
        return res;
    }

    /// <summary>Nearest hit (distance along the ray) among the scene objects, returning the model id.</summary>
    public static (Guid Id, SceneKind Kind, float Distance)? Pick(IEnumerable<SceneObject> objects, (float X, float Y, float Z) origin, (float X, float Y, float Z) dir)
    {
        (Guid, SceneKind, float)? best = null;
        foreach (var o in objects.Where(o => o.Kind != SceneKind.Slab))
            if (MeshBuilder.Pick(o.Mesh, origin, dir) is { } d && (best == null || d < best.Value.Item3)) best = (o.Id, o.Kind, d);
        return best;
    }
}
