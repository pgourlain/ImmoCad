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
