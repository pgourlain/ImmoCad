using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Scene;

namespace ImmeubleCad.Desktop;

/// <summary>3D view drawn with Avalonia (software wireframe). OpenGL path can replace Render later; same camera math in Scene.</summary>
public class Viewport3D : Control
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private Building? Building => Vm?.Building;
    private Level? CurrentLevel => Vm?.CurrentLevel;
    public OrbitCamera Camera { get; } = new();
    public bool Xray { get; set; }
    public bool CutAboveCurrent { get; set; } = true;
    private Point? _last;


    protected override void OnPointerPressed(PointerPressedEventArgs e) { _last = e.GetPosition(this); e.Pointer.Capture(this); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _last = null; e.Pointer.Capture(null); }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_last is not { } l) return;
        var p = e.GetPosition(this);
        Camera.Orbit(-(p.X - l.X) * 0.01, (p.Y - l.Y) * 0.01); _last = p; InvalidateVisual();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { Camera.Zoom(e.Delta.Y > 0 ? 0.9 : 1.1); InvalidateVisual(); }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (Building is not { Levels.Count: > 0 } b) return;
        var maxZ = CutAboveCurrent && CurrentLevel != null ? CurrentLevel.AltitudeMm + CurrentLevel.HeightMm : int.MaxValue;
        var all = b.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).ToList();
        if (all.Count > 0) Camera.Target = ((all.Min(p => p.X) + all.Max(p => p.X)) / 2000.0, (all.Min(p => p.Y) + all.Max(p => p.Y)) / 2000.0, 1.5);

        var aspect = Bounds.Width / Math.Max(1, Bounds.Height); var half = Math.Min(Bounds.Width, Bounds.Height) / 2;
        Point S(double x, double y, double z)
        {
            var (px, py, _) = Camera.Project(x, y, z);
            return new(Bounds.Width / 2 + px * half, Bounds.Height / 2 - py * half);
        }
        void Draw(Mesh m, IPen pen)
        {
            foreach (var (a, c) in WireEdges.Of(m))
            {
                var p = m.Positions;
                ctx.DrawLine(pen, S(p[a * 3], p[a * 3 + 1], p[a * 3 + 2]), S(p[c * 3], p[c * 3 + 1], p[c * 3 + 2]));
            }
        }
        var wallPen = new Pen(new SolidColorBrush(Xray ? Color.FromArgb(110, 80, 80, 80) : Colors.DimGray), 1);
        foreach (var l in b.Levels.Where(l => l.AltitudeMm < maxZ))
        {
            var m = new Mesh();
            foreach (var w in l.Walls)
            {
                MeshBuilder.ExtrudeWall(w.Footprint.Select(p => (p.X, p.Y)).ToList(), l.AltitudeMm, w.HeightMm ?? l.HeightMm, m);
                foreach (var h in w.Holes) MeshBuilder.ExtrudeSides(h.Select(p => (p.X, p.Y)).ToList(), l.AltitudeMm, w.HeightMm ?? l.HeightMm, m);
            }
            Draw(m, wallPen);
        }
        foreach (var n in b.Networks.Where(n => n.Visible))
        {
            var pen = new Pen(n.Type switch { NetworkType.HeatingSupply => Brushes.Red, NetworkType.HeatingReturn => Brushes.Blue, NetworkType.ColdWater => Brushes.DodgerBlue, NetworkType.DomesticHotWater => Brushes.OrangeRed, _ => Brushes.Green }, 2);
            foreach (var s in n.Segments)
            {
                var a = n.Nodes.First(x => x.Id == s.A).Position; var c = n.Nodes.First(x => x.Id == s.B).Position;
                ctx.DrawLine(pen, S(a.X / 1000.0, a.Y / 1000.0, a.Z / 1000.0), S(c.X / 1000.0, c.Y / 1000.0, c.Z / 1000.0));
            }
        }
    }
}
