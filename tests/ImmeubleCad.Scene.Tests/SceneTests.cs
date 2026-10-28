using ImmeubleCad.Scene;
using Xunit;

namespace ImmeubleCad.Scene.Tests;

public class SceneTests
{
    private static readonly (int, int)[] Rect = [(0, 0), (4000, 0), (4000, 200), (0, 200)];

    [Fact] public void Extrusion_is_deterministic()
    {
        var a = MeshBuilder.ExtrudeWall(Rect, 0, 2700).Checksum();
        Assert.Equal(a, MeshBuilder.ExtrudeWall(Rect, 0, 2700).Checksum());
        Assert.NotEqual(a, MeshBuilder.ExtrudeWall(Rect, 0, 2800).Checksum());
    }

    [Fact] public void Pick_hits_wall_from_above_and_misses_aside()
    {
        var m = MeshBuilder.ExtrudeWall(Rect, 0, 2700);
        Assert.NotNull(MeshBuilder.Pick(m, (2, 0.1f, 10), (0, 0, -1)));
        Assert.Null(MeshBuilder.Pick(m, (2, 5, 10), (0, 0, -1)));
    }

    [Fact] public void Vertical_pipe_spans_height()
    {
        var m = MeshBuilder.Pipe((0, 0, 0), (0, 0, 2700), 20);
        Assert.Equal(8, m.VertexCount);
        Assert.Equal(2.71f, m.Positions.Where((_, i) => i % 3 == 2).Max(), 3);
    }
}

public class CameraTests
{
    [Fact] public void Target_projects_to_center_and_orbit_is_clamped()
    {
        var c = new OrbitCamera { Target = (1, 2, 3) };
        var (x, y, _) = c.Project(1, 2, 3);
        Assert.Equal(0, x, 9); Assert.Equal(0, y, 9);
        c.Orbit(0, 100); Assert.True(c.Pitch < Math.PI / 2);
    }

    [Fact] public void Higher_point_projects_higher()
    {
        var c = new OrbitCamera { Target = (0, 0, 0), Pitch = 0.3 };
        Assert.True(c.Project(0, 0, 2).Y > c.Project(0, 0, 0).Y);
    }

    [Fact] public void Wire_edges_of_box_are_18_unique()
        => Assert.Equal(18, WireEdges.Of(MeshBuilder.Box(0, 0, 0, 1, 1, 1)).Count);
}

public class SceneArchitectureTests
{
    [Fact] public void Scene_is_BCL_only()
    {
        var refs = typeof(MeshBuilder).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.All(refs, n => Assert.True(n.StartsWith("System") || n is "netstandard" or "mscorlib", $"Scene references {n}"));
    }
}

public class SceneBuilderTests
{
    private static SceneInput Input()
    {
        var l0 = new SceneLevel(Guid.NewGuid(), 0, 0, 2700); var l1 = new SceneLevel(Guid.NewGuid(), 1, 2700, 2700); var l2 = new SceneLevel(Guid.NewGuid(), 2, 5400, 2700);
        (int, int)[] rect = [(0, 0), (4000, 0), (4000, 200), (0, 200)];
        var walls = new[] { l0, l1, l2 }.Select(l => new SceneWall(Guid.NewGuid(), l.Index, rect, [], l.AltitudeMm, l.HeightMm)).ToList();
        var pipe = new ScenePipe(Guid.NewGuid(), Guid.NewGuid(), (100, 100, 0), (100, 100, 8100), 20, (1, 0, 0), true);
        var elbow = new SceneFitting(Guid.NewGuid(), Guid.NewGuid(), "Elbow90", (100, 100, 2400), (100, 100, 0), (1100, 100, 2400), 20, (1, 0, 0), true);
        return new SceneInput([l0, l1, l2], walls, [pipe], [elbow], []);
    }

    [Fact] public void Cylinder_has_round_section_and_normals_point_outwards()
    {
        var m = MeshBuilder.Cylinder((0, 0, 0), (0, 0, 1000), 100, 12); m.ComputeNormals();
        Assert.Equal(12 * 2 + 2, m.VertexCount);
        for (var i = 0; i < 24; i++) Assert.InRange(MathF.Sqrt(m.Positions[i * 3] * m.Positions[i * 3] + m.Positions[i * 3 + 1] * m.Positions[i * 3 + 1]), 0.0499f, 0.0501f);
        var n = (m.Normals[0], m.Normals[1]); Assert.True(n.Item1 * m.Positions[0] + n.Item2 * m.Positions[1] > 0);   // radial normal faces away from the axis
        Assert.NotNull(MeshBuilder.Pick(m, (0.3f, 0, 0.5f), (-1, 0, 0)));
        Assert.Null(MeshBuilder.Pick(m, (0.3f, 0.2f, 0.5f), (-1, 0, 0)));
    }

    [Fact] public void Level_filters_and_cut_above_current()
    {
        var inp = Input(); var cur = inp.Levels[1].Id;
        int Walls(SceneOptions o) => SceneBuilder.Build(inp, o).Count(s => s.Kind == SceneKind.Wall);
        Assert.Equal(2, Walls(new(cur, LevelFilter.WholeBuilding, CutAboveCurrent: true)));     // dollhouse: nothing above the current level
        Assert.Equal(3, Walls(new(cur, LevelFilter.WholeBuilding, CutAboveCurrent: false)));
        Assert.Equal(1, Walls(new(cur, LevelFilter.CurrentOnly, CutAboveCurrent: false)));
        Assert.Equal(2, Walls(new(cur, LevelFilter.CurrentAndBelow, CutAboveCurrent: false)));
        var pipes = SceneBuilder.Build(inp, new(cur, CutAboveCurrent: true)).Where(s => s.Kind == SceneKind.Pipe).ToList(); Assert.Single(pipes);
    }

    [Fact] public void Xray_makes_walls_translucent_but_not_pipes_and_hidden_networks_disappear()
    {
        var inp = Input(); var xr = SceneBuilder.Build(inp, new(null, XRay: true));
        Assert.All(xr.Where(s => s.Kind == SceneKind.Wall), s => Assert.True(s.Alpha < 0.5f)); Assert.All(xr.Where(s => s.Kind is SceneKind.Pipe or SceneKind.Fitting), s => Assert.Equal(1f, s.Alpha));
        var hidden = inp with { Pipes = inp.Pipes.Select(p => p with { Visible = false }).ToList(), Fittings = inp.Fittings.Select(f => f with { Visible = false }).ToList() };
        Assert.DoesNotContain(SceneBuilder.Build(hidden, new(null)), s => s.Kind is SceneKind.Pipe or SceneKind.Fitting);
    }

    [Fact] public void Picking_returns_the_model_id_of_the_nearest_object_and_selection_highlights()
    {
        var inp = Input(); var wallId = inp.Walls[0].Id; var objs = SceneBuilder.Build(inp, new(null, Selection: new HashSet<Guid> { wallId }));
        Assert.True(objs.First(o => o.Id == wallId).Highlight);
        var hit = SceneBuilder.Pick(objs, (2, 0.1f, 20), (0, 0, -1));       // from above, through the stack of three identical walls: the highest wall is nearest
        Assert.Equal(inp.Walls[2].Id, hit!.Value.Id); Assert.Equal(SceneKind.Wall, hit.Value.Kind);
        Assert.Null(SceneBuilder.Pick(objs, (20, 20, 20), (0, 0, -1)));
    }

    [Fact] public void Elbow_and_scene_checksums_are_deterministic()
    {
        var a = SceneBuilder.Build(Input(), new(null)); var b = SceneBuilder.Build(Input(), new(null));
        Assert.Equal(a.Select(o => o.Mesh.Checksum()).Aggregate(0L, (x, y) => x ^ y), b.Select(o => o.Mesh.Checksum()).Aggregate(0L, (x, y) => x ^ y));
        Assert.Contains(a, o => o.Name == "Elbow90" && o.Mesh.VertexCount > 20);
    }
}

public class CameraTransitionTests
{
    [Fact] public void Easing_endpoints_and_monotonic()
    {
        Assert.Equal(0, CameraTransition.EaseInOut(0)); Assert.Equal(1, CameraTransition.EaseInOut(1)); Assert.Equal(0.5, CameraTransition.EaseInOut(0.5), 9);
        var prev = -1.0; for (var t = 0.0; t <= 1; t += 0.05) { var v = CameraTransition.EaseInOut(t); Assert.True(v >= prev); prev = v; }
    }

    [Fact] public void Plan_to_top_to_plan_is_exact_ten_times()
    {
        var (x, y, scale) = (12345.0, 6789.0, 0.137); const double h = 700;
        for (var i = 0; i < 10; i++)
        {
            var pose = CameraTransition.TopFromPlan(x, y, scale, h, 0); var back = CameraTransition.PlanFromTop(pose, h);
            Assert.Equal(x, back.X, 6); Assert.Equal(y, back.Y, 6); Assert.Equal(scale, back.Scale, 9); (x, y, scale) = back;
        }
    }

    [Fact] public void Transition_runs_half_a_second_and_can_be_disabled()
    {
        var a = CameraTransition.TopFromPlan(0, 0, 0.1, 700); var b = new CameraPose(0.8, 0.5, 20, 4, 3, 1, 0);
        Assert.Equal(a, CameraTransition.At(a, b, 0)); Assert.Equal(b, CameraTransition.At(a, b, 0.5)); Assert.Equal(b, CameraTransition.At(a, b, 0.7));
        var mid = CameraTransition.At(a, b, 0.25); Assert.InRange(mid.OrthoBlend, 0.4, 0.6); Assert.Equal(b, CameraTransition.At(a, b, 0.01, animate: false));
    }
}

public class UnprojectTests
{
    [Theory] [InlineData(0.8, 0.5, 15.0)] [InlineData(-2.0, 0.1, 6.0)] [InlineData(3.0, 1.2, 30.0)]
    public void Ray_through_a_projected_point_passes_through_it(double yaw, double pitch, double dist)
    {
        var c = new OrbitCamera { Yaw = yaw, Pitch = pitch, Distance = dist, Target = (2, 3, 1) };
        var p = (X: 4.0, Y: -1.0, Z: 2.5); var (sx, sy, _) = c.Project(p.X, p.Y, p.Z);
        var (o, d) = c.Unproject(sx, sy);
        // distance from the point to the ray
        double vx = p.X - o.X, vy = p.Y - o.Y, vz = p.Z - o.Z; var t = vx * d.X + vy * d.Y + vz * d.Z;
        double qx = vx - d.X * t, qy = vy - d.Y * t, qz = vz - d.Z * t;
        Assert.True(Math.Sqrt(qx * qx + qy * qy + qz * qz) < 1e-9); Assert.Equal(1, Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z), 9);
    }

    [Fact] public void Picking_through_the_camera_hits_the_wall_under_the_pixel()
    {
        var wall = MeshBuilder.ExtrudeWall(new (int, int)[] { (0, 0), (4000, 0), (4000, 200), (0, 200) }, 0, 2700); wall.ComputeNormals();
        var id = Guid.NewGuid(); var objs = new[] { new SceneObject(id, SceneKind.Wall, "w", wall, (1, 1, 1), 1) };
        var c = new OrbitCamera { Yaw = 0.6, Pitch = 0.7, Distance = 12, Target = (2, 0.1, 1.35) };
        var (sx, sy, _) = c.Project(2, 0.1, 2.7); var (o, d) = c.Unproject(sx, sy);                        // a point on top of the wall
        var hit = SceneBuilder.Pick(objs, ((float)o.X, (float)o.Y, (float)o.Z), ((float)d.X, (float)d.Y, (float)d.Z));
        Assert.Equal(id, hit!.Value.Id);
        var (mx, my, _) = c.Project(9, 9, 0); var (o2, d2) = c.Unproject(mx, my);
        Assert.Null(SceneBuilder.Pick(objs, ((float)o2.X, (float)o2.Y, (float)o2.Z), ((float)d2.X, (float)d2.Y, (float)d2.Z)));
    }
}
