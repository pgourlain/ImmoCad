using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Tools;

namespace ImmeubleCad.Desktop;

/// <summary>2D plan: walls, ghost of the level below, pan (middle/space drag), zoom (wheel), Draw Wall tool in hybrid click/drag mode.</summary>
public class PlanView : Control
{
    public static readonly StyledProperty<Level?> LevelProperty = AvaloniaProperty.Register<PlanView, Level?>(nameof(Level));
    public static readonly StyledProperty<Building?> BuildingProperty = AvaloniaProperty.Register<PlanView, Building?>(nameof(Building));
    public Level? Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Building? Building { get => GetValue(BuildingProperty); set => SetValue(BuildingProperty, value); }

    public double Scale { get; set; } = 0.1;               // px per mm
    public (double X, double Y) Offset { get; set; } = (50, 0); // screen px of the plan origin (Y flipped)
    public DrawChainTool? Tool { get; set; }
    public Snapper Snapper { get; } = new();
    public event Action<PointMm>? CursorMoved;
    private Point? _pan;
    private bool _fitted;

    static PlanView() => AffectsRender<PlanView>(LevelProperty, BuildingProperty);

    public PointMm ToMm(Point p) => new((int)Math.Round((p.X - Offset.X) / Scale), (int)Math.Round((Bounds.Height - p.Y - Offset.Y) / Scale));
    private Point ToScreen(PointMm p) => new(Offset.X + p.X * Scale, Bounds.Height - Offset.Y - p.Y * Scale);

    public void FitAll()
    {
        var pts = Building?.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).ToList();
        if (pts is not { Count: > 0 } || Bounds.Width < 10) return;
        int minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        Scale = Math.Min((Bounds.Width - 80) / Math.Max(1, maxX - minX), (Bounds.Height - 80) / Math.Max(1, maxY - minY));
        Offset = (40 - minX * Scale, 40 - minY * Scale); _fitted = true;
    }

    private IEnumerable<IReadOnlyList<PointMm>> Polys() => Level?.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint) ?? [];

    private ToolPointer TP(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var prev = Tool?.Points.Count > 0 ? Tool.Points[^1] : (PointMm?)null;
        var snapped = Snapper.Snap(ToMm(p), Polys(), prev);
        return new ToolPointer(snapped.Point, p.X, p.Y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed) { _pan = e.GetPosition(this); return; }
        if (props.IsRightButtonPressed) { Tool?.Cancel(); InvalidateVisual(); return; }
        if (Tool == null) return;
        Tool.Down(TP(e)); e.Pointer.Capture(this); InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_pan is { } l) { Offset = (Offset.X + p.X - l.X, Offset.Y - (p.Y - l.Y)); _pan = p; InvalidateVisual(); return; }
        CursorMoved?.Invoke(ToMm(p));
        if (Tool == null) return;
        Tool.Move(TP(e)); InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _pan = null; e.Pointer.Capture(null);
        Tool?.Up(TP(e)); InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this); var before = ToMm(p);
        Scale = Math.Clamp(Scale * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.005, 5);
        // keep the point under the mouse fixed
        Offset = (p.X - before.X * Scale, Bounds.Height - p.Y - before.Y * Scale); InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Tool == null) return;
        if (e.Key == Key.Enter) Tool.Finish();
        else if (e.Key == Key.Back) Tool.Backspace();
        else if (e.Key == Key.Escape) Tool.Cancel();
        else return;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (!_fitted) FitAll();
        // grid every metre
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(235, 235, 235)), 1);
        if (Scale * 1000 > 8)
            for (var x = Offset.X % (Scale * 1000); x < Bounds.Width; x += Scale * 1000) ctx.DrawLine(gridPen, new(x, 0), new(x, Bounds.Height));
        if (Scale * 1000 > 8)
            for (var y = Bounds.Height - Offset.Y; y > -Scale * 1000; y -= Scale * 1000) ctx.DrawLine(gridPen, new(0, y), new(Bounds.Width, y));
        if (Level is not { } l) return;
        var below = Building?.Levels.LastOrDefault(x => x.AltitudeMm < l.AltitudeMm);
        if (below != null) DrawWalls(ctx, below, new SolidColorBrush(Color.FromArgb(50, 0, 120, 255)), null);
        DrawWalls(ctx, l, Brushes.DimGray, new Pen(Brushes.Black, 1));
        if (Tool is { Points.Count: > 0 } t)
        {
            var pen = new Pen(Brushes.OrangeRed, 2);
            for (var i = 1; i < t.Points.Count; i++) ctx.DrawLine(pen, ToScreen(t.Points[i - 1]), ToScreen(t.Points[i]));
            if (t.Preview is { } pv)
            {
                var a = t.Points[^1];
                ctx.DrawLine(new Pen(Brushes.OrangeRed, 1, DashStyle.Dash), ToScreen(a), ToScreen(pv));
                var len = Math.Sqrt(Math.Pow(pv.X - a.X, 2) + Math.Pow(pv.Y - a.Y, 2)) / 1000;
                var ft = new FormattedText($"{len:0.00} m", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.OrangeRed);
                ctx.DrawText(ft, ToScreen(pv) + new Point(8, -16));
            }
        }
    }

    private void DrawWalls(DrawingContext ctx, Level l, IBrush fill, IPen? pen)
    {
        foreach (var w in l.Walls)
        {
            if (w.Footprint.Count < 3) continue;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.SetFillRule(FillRule.EvenOdd);
                c.BeginFigure(ToScreen(w.Footprint[0]), true);
                foreach (var p in w.Footprint.Skip(1)) c.LineTo(ToScreen(p));
                c.EndFigure(true);
                foreach (var hole in w.Holes.Where(h => h.Count >= 3))
                {
                    c.BeginFigure(ToScreen(hole[0]), true);
                    foreach (var p in hole.Skip(1)) c.LineTo(ToScreen(p));
                    c.EndFigure(true);
                }
            }
            ctx.DrawGeometry(fill, pen, g);
        }
    }
}
