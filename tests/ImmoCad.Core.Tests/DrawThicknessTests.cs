using ImmoCad.Core.Tools;
using ImmoCad.Core.Model;
using Xunit;

namespace ImmoCad.Core.Tests;

public class DrawThicknessTests
{
    private static ToolPointer P(int x, int y) => new(new(x, y), x, y);
    private static void Click(DrawChainTool t, int x, int y) { t.Down(P(x, y)); t.Up(P(x, y)); }
    private static void Place(DrawChainTool t, int x, int y) { t.Move(P(x, y)); t.Down(P(x, y)); }

    [Fact] public void The_thickness_is_stamped_per_section_when_its_end_is_placed_and_backspace_forgets_it()
    {
        var values = new List<int>(); DrawChainTool? t = null; t = new DrawChainTool(_ => values.AddRange(t!.SegmentValues));
        t.Value = 200; Click(t, 0, 0); Place(t, 3000, 0);
        t.Value = 300; Place(t, 3000, 2000);                               // thickened while the 2nd section is drawn: the 1st keeps 200
        t.Value = 100; Place(t, 0, 2000); Assert.Equal([200, 300, 100], t.SegmentValues);
        t.Backspace(); Assert.Equal([200, 300], t.SegmentValues);
        t.Finish(); Assert.Equal([200, 300], values);
    }
}
