namespace ImmoCad.Scene;

public enum SceneKind { Wall, Slab, Pipe, Fitting, Component, Equipment, Floor, Ceiling, Grid }
public enum LevelFilter { CurrentOnly, CurrentAndBelow, WholeBuilding }

/// <summary>One drawable thing with the id of the model object it came from (for picking and shared selection).</summary>
public sealed record SceneObject(Guid Id, SceneKind Kind, string Name, Mesh Mesh, (float R, float G, float B) Color, float Alpha, bool Highlight = false);

/// <summary>Input to the builder, Scene stays free of Core types: the caller (Core/Desktop) maps its model onto these.</summary>
public sealed record SceneWall(Guid Id, int LevelIndex, IReadOnlyList<(int X, int Y)> Footprint, IReadOnlyList<IReadOnlyList<(int X, int Y)>> Holes, int BaseZ, int Height);
public sealed record SceneLevel(Guid Id, int Index, int AltitudeMm, int HeightMm);
public sealed record ScenePipe(Guid NetworkId, Guid SegmentId, (int X, int Y, int Z) A, (int X, int Y, int Z) B, int DiameterMm, (float R, float G, float B) Color, bool Visible);
public sealed record SceneFitting(Guid NetworkId, Guid NodeId, string Kind, (int X, int Y, int Z) At, (int X, int Y, int Z)? ArmA, (int X, int Y, int Z)? ArmB, int DiameterMm, (float R, float G, float B) Color, bool Visible);
public sealed record SceneBox(Guid Id, Guid LevelId, string Name, int X, int Y, int Z, int W, int D, int H, (float R, float G, float B) Color, int Radius = 0);
public sealed record SceneInput(IReadOnlyList<SceneLevel> Levels, IReadOnlyList<SceneWall> Walls, IReadOnlyList<ScenePipe> Pipes, IReadOnlyList<SceneFitting> Fittings, IReadOnlyList<SceneBox> Boxes);

public sealed record SceneOptions(Guid? CurrentLevel, LevelFilter Filter = LevelFilter.WholeBuilding, bool CutAboveCurrent = true, bool XRay = false, bool Slabs = true, ISet<Guid>? Selection = null, ISet<Guid>? HiddenLevels = null, bool Walk = false, (int X0, int Y0, int X1, int Y1)? WalkGrid = null);

public static class SceneBuilder
{
    public static readonly (float, float, float) WallColor = (0.78f, 0.78f, 0.75f), SlabColor = (0.62f, 0.62f, 0.6f);
    /// <summary>Walk mode: floor, walls and ceiling get clearly different tones, otherwise a corridor reads as one flat grey.</summary>
    public static readonly (float, float, float) GridColor = (0.62f, 0.66f, 0.72f);
    public static readonly (float, float, float) WalkWallColor = (0.86f, 0.83f, 0.78f), FloorColor = (0.46f, 0.42f, 0.38f), CeilingColor = (0.95f, 0.95f, 0.93f);
    /// <summary>Walk mode shows what crosses the current level's height band, with this margin for the slabs.</summary>
    public const int WalkBandMarginMm = 300;

    public static List<SceneObject> Build(SceneInput input, SceneOptions o)
    {
        var res = new List<SceneObject>();
        var cur = input.Levels.FirstOrDefault(l => l.Id == o.CurrentLevel);
        bool LevelVisible(int index)
        {
            if (o.Walk && cur != null) return index == cur.Index;                                     // walking: the current level only
            if (o.HiddenLevels is { Count: > 0 } && input.Levels.FirstOrDefault(x => x.Index == index) is { } lv && o.HiddenLevels.Contains(lv.Id)) return false;     // switched off with the eye in the level list
            if (cur == null) return true;
            if (o.CutAboveCurrent && index > cur.Index) return false;
            return o.Filter switch { LevelFilter.CurrentOnly => index == cur.Index, LevelFilter.CurrentAndBelow => index <= cur.Index, _ => true };
        }
        var wallAlpha = o.XRay && !o.Walk ? 0.28f : 1f;
        foreach (var w in input.Walls.Where(w => LevelVisible(w.LevelIndex)))
        {
            var m = MeshBuilder.ExtrudeWall(w.Footprint, w.BaseZ, w.Height, null, w.Holes);
            foreach (var h in w.Holes) MeshBuilder.ExtrudeSides(h, w.BaseZ, w.Height, m);
            m.ComputeNormals(); res.Add(new SceneObject(w.Id, SceneKind.Wall, "wall", m, o.Walk ? WalkWallColor : WallColor, wallAlpha, o.Selection?.Contains(w.Id) == true));
        }
        if (o.Walk && cur != null) { AddFloorAndCeiling(res, input, cur); if (o.WalkGrid is { } g) AddGrid(res, cur, g); }
        if (o.Slabs && !o.Walk)
            foreach (var l in input.Levels.Where(l => LevelVisible(l.Index)))
            {
                var walls = input.Walls.Where(w => w.LevelIndex == l.Index && w.Footprint.Count >= 3).ToList(); if (walls.Count == 0) continue;
                var m = new Mesh(); foreach (var w in walls) MeshBuilder.ExtrudeWall(w.Footprint, l.AltitudeMm - 200, 200, m);   // under what the walls enclose (rooms included), not the bounding box of the level
                m.ComputeNormals(); res.Add(new SceneObject(l.Id, SceneKind.Slab, "slab", m, SlabColor, o.XRay ? 0.15f : 1f));
            }
        bool ZVisible(int z) => cur == null || !o.CutAboveCurrent || z <= cur.AltitudeMm + cur.HeightMm;
        bool InBand(int lo, int hi) => lo <= cur!.AltitudeMm + cur.HeightMm + WalkBandMarginMm && hi >= cur.AltitudeMm - WalkBandMarginMm;
        foreach (var p0 in input.Pipes.Where(p => p.Visible && (o.Walk && cur != null ? InBand(Math.Min(p.A.Z, p.B.Z), Math.Max(p.A.Z, p.B.Z)) : ZVisible(Math.Min(p.A.Z, p.B.Z)))))
        {
            var p = o.Walk && cur != null ? ClipToBand(p0, cur) : p0;                       // walking: a riser through the whole building is drawn only around this level
            var m = MeshBuilder.Cylinder(p.A, p.B, p.DiameterMm); m.ComputeNormals();
            res.Add(new SceneObject(p.SegmentId, SceneKind.Pipe, "pipe", m, p.Color, 1f, o.Selection?.Contains(p.SegmentId) == true));
        }
        foreach (var f in input.Fittings.Where(f => f.Visible && (o.Walk && cur != null ? InBand(f.At.Z, f.At.Z) : ZVisible(f.At.Z))))
        {
            Mesh? m = null;
            if (f.Kind == "Elbow90" && f.ArmA is { } a && f.ArmB is { } b) m = MeshBuilder.Elbow(f.At, a, b, f.DiameterMm);
            else if (f.Kind is "Tee" or "Cross" or "Reducer" or "SlabCrossing") m = MeshBuilder.Box(f.At.X - f.DiameterMm, f.At.Y - f.DiameterMm, f.At.Z - f.DiameterMm, f.At.X + f.DiameterMm, f.At.Y + f.DiameterMm, f.At.Z + f.DiameterMm);
            else if (f.Kind == "Component") m = MeshBuilder.Box(f.At.X - f.DiameterMm, f.At.Y - f.DiameterMm, f.At.Z - f.DiameterMm / 2, f.At.X + f.DiameterMm, f.At.Y + f.DiameterMm, f.At.Z + f.DiameterMm / 2);
            if (m == null) continue; m.ComputeNormals(); res.Add(new SceneObject(f.NodeId, f.Kind == "Component" ? SceneKind.Component : SceneKind.Fitting, f.Kind, m, f.Kind == "Component" ? (0.15f, 0.15f, 0.15f) : f.Color, 1f));
        }
        foreach (var b in input.Boxes.Where(b => LevelVisible(input.Levels.FirstOrDefault(l => l.Id == b.LevelId)?.Index ?? 0)))
        {
            var m = b.Radius > 0 ? MeshBuilder.Cylinder((b.X, b.Y, b.Z), (b.X, b.Y, b.Z + b.H), b.Radius * 2) : MeshBuilder.Box(b.X, b.Y, b.Z, b.X + b.W, b.Y + b.D, b.Z + b.H); m.ComputeNormals(); res.Add(new SceneObject(b.Id, SceneKind.Equipment, b.Name, m, b.Color, 1f, o.Selection?.Contains(b.Id) == true));
        }
        return res;
    }

    /// <summary>The part of a pipe inside the level's height band (its height plus the slab margin on each side); the pipe keeps its ids.</summary>
    private static ScenePipe ClipToBand(ScenePipe p, SceneLevel cur)
    {
        int lo = cur.AltitudeMm - WalkBandMarginMm, hi = cur.AltitudeMm + cur.HeightMm + WalkBandMarginMm; if (p.A.Z == p.B.Z) return p;
        double dz = p.B.Z - p.A.Z, t0 = Math.Clamp((lo - p.A.Z) / dz, 0, 1), t1 = Math.Clamp((hi - p.A.Z) / dz, 0, 1); if (t0 > t1) (t0, t1) = (t1, t0);
        (int, int, int) At(double t) => ((int)Math.Round(p.A.X + (p.B.X - p.A.X) * t), (int)Math.Round(p.A.Y + (p.B.Y - p.A.Y) * t), (int)Math.Round(p.A.Z + dz * t));
        return p with { A = At(t0), B = At(t1) };
    }

    /// <summary>
    /// Wire grid in the planes of the floor and the ceiling, over the walk's world: outside the walls the user sees where the floors and ceilings would be instead of a white void.
    /// The lines are 5 mm inside the floor and the ceiling, so they are hidden indoors and only show beyond the walls. Line pairs in <see cref="Mesh.Indices"/>, no triangles.
    /// </summary>
    private static void AddGrid(List<SceneObject> res, SceneLevel cur, (int X0, int Y0, int X1, int Y1) g)
    {
        var step = Math.Max(1000, (int)Math.Ceiling(Math.Max(g.X1 - g.X0, g.Y1 - g.Y0) / 120.0 / 1000.0) * 1000);                // at most about 120 lines per direction
        var m = new Mesh(); const float k = 0.001f;
        foreach (var z in new[] { cur.AltitudeMm - 5, cur.AltitudeMm + cur.HeightMm + 5 })
        {
            for (var x = (int)Math.Ceiling(g.X0 / (double)step) * step; x <= g.X1; x += step) { var a = m.Add(x * k, g.Y0 * k, z * k); var b = m.Add(x * k, g.Y1 * k, z * k); m.Indices.Add(a); m.Indices.Add(b); }
            for (var y = (int)Math.Ceiling(g.Y0 / (double)step) * step; y <= g.Y1; y += step) { var a = m.Add(g.X0 * k, y * k, z * k); var b = m.Add(g.X1 * k, y * k, z * k); m.Indices.Add(a); m.Indices.Add(b); }
        }
        if (m.VertexCount > 0) res.Add(new SceneObject(cur.Id, SceneKind.Grid, "grid", m, GridColor, 1f));
    }

    /// <summary>Floor and ceiling of a level: flat slabs over the bounding box of its walls. Walls hide the overflow from the inside; it only shows through windows and gaps.</summary>
    private static void AddFloorAndCeiling(List<SceneObject> res, SceneInput input, SceneLevel cur)
    {
        var pts = input.Walls.Where(w => w.LevelIndex == cur.Index).SelectMany(w => w.Footprint).ToList(); if (pts.Count == 0) return;
        int x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y), x1 = pts.Max(p => p.X), y1 = pts.Max(p => p.Y);
        var floor = MeshBuilder.Box(x0, y0, cur.AltitudeMm - 100, x1, y1, cur.AltitudeMm); floor.ComputeNormals();
        var ceiling = MeshBuilder.Box(x0, y0, cur.AltitudeMm + cur.HeightMm, x1, y1, cur.AltitudeMm + cur.HeightMm + 100); ceiling.ComputeNormals();
        res.Add(new SceneObject(cur.Id, SceneKind.Floor, "floor", floor, FloorColor, 1f));
        res.Add(new SceneObject(cur.Id, SceneKind.Ceiling, "ceiling", ceiling, CeilingColor, 1f));
    }

    /// <summary>Nearest hit (distance along the ray) among the scene objects, returning the model id.</summary>
    public static (Guid Id, SceneKind Kind, float Distance)? Pick(IEnumerable<SceneObject> objects, (float X, float Y, float Z) origin, (float X, float Y, float Z) dir)
    {
        (Guid, SceneKind, float)? best = null;
        foreach (var o in objects.Where(o => o.Kind is not (SceneKind.Slab or SceneKind.Floor or SceneKind.Ceiling or SceneKind.Grid)))
            if (MeshBuilder.Pick(o.Mesh, origin, dir) is { } d && (best == null || d < best.Value.Item3)) best = (o.Id, o.Kind, d);
        return best;
    }
}
