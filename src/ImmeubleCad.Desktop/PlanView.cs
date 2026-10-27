using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Desktop;

/// <summary>2D plan: walls as filled polygons, scaled to fit. X east, Y north (flipped on screen).</summary>
public class PlanView : Control
{
    public static readonly StyledProperty<Level?> LevelProperty = AvaloniaProperty.Register<PlanView, Level?>(nameof(Level));
    public Level? Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    static PlanView() => AffectsRender<PlanView>(LevelProperty);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (Level is not { Walls.Count: > 0 } l) return;
        var pts = l.Walls.SelectMany(w => w.Footprint).ToList();
        int minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        var s = Math.Min((Bounds.Width - 40) / Math.Max(1, maxX - minX), (Bounds.Height - 40) / Math.Max(1, maxY - minY));
        Point T(PointMm p) => new(20 + (p.X - minX) * s, Bounds.Height - 20 - (p.Y - minY) * s);
        foreach (var w in l.Walls)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(T(w.Footprint[0]), true);
                foreach (var p in w.Footprint.Skip(1)) c.LineTo(T(p));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(Brushes.DimGray, new Pen(Brushes.Black, 1), g);
        }
    }
}
