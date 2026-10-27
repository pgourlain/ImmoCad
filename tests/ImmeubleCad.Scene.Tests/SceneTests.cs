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
