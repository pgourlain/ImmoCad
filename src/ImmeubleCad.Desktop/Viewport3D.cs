using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using static ImmeubleCad.Core.Translations;
using ImmeubleCad.Scene;

namespace ImmeubleCad.Desktop;

/// <summary>Software 3D view drawn with Avalonia (painter's algorithm): the fallback when OpenGL is unavailable, and what headless tests and screenshots use.</summary>
public class Viewport3D : Control, IViewport3D
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    public ViewportController Controller { get; } = new();
    public Control Control => this;

    public Viewport3D()
    {
        Focusable = true; ClipToBounds = true;
        Controller.Invalidate = InvalidateVisual;
        Controller.Picked = (id, kind) => Vm?.Pick3D(id, kind);
        Controller.PlaneToolActive = () => Vm?.IsPlaneTool == true; Controller.PlaneAltitudeM = () => (Vm?.CurrentLevel?.AltitudeMm ?? 0) / 1000.0; Controller.PlaneClicked = p => Vm?.Click3D(p);
    }

    public void Rebuild() { Controller.Rebuild(Vm); InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { Focus(); var p = e.GetCurrentPoint(this); Controller.Down(e.GetPosition(this), p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed); e.Pointer.Capture(this); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { Controller.Up(e.GetPosition(this), Vm, Bounds.Width, Bounds.Height); e.Pointer.Capture(null); }
    protected override void OnPointerMoved(PointerEventArgs e) => Controller.Move(e.GetPosition(this));
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) => Controller.Wheel(e.Delta.Y);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key) { case Key.T: Controller.Preset("top"); break; case Key.F: Controller.Preset("front"); break; case Key.R: Controller.Preset("side"); break; case Key.I: Controller.Preset("iso"); break; case Key.Home: Controller.FitAll(); Rebuild(); break; default: return; }
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        ctx.FillRectangle(Tokens.Brush(this, "Viewport3DBackground", dark ? Color.FromRgb(30, 32, 36) : Color.FromRgb(246, 247, 249)), new Rect(Bounds.Size));
        if (Controller.Objects.Count == 0) return;
        var half = Math.Min(Bounds.Width, Bounds.Height) / 2; var cam = Controller.Camera;
        Point S(float x, float y, float z) { var (px, py, _) = cam.Project(x, y, z); return new(Bounds.Width / 2 + px * half, Bounds.Height / 2 - py * half); }
        var light = Norm(0.4, -0.5, 0.8);
        var tris = new List<(double Depth, Point A, Point B, Point C, Color Color)>(); var lines = new List<(Point A, Point B, Color Color, double W)>();
        foreach (var o in Controller.Objects)
        {
            var m = o.Mesh; var baseColor = o.Highlight ? (1f, 0.55f, 0f) : o.Color; var alpha = (byte)(o.Alpha * 255);
            if (Controller.Wireframe)
            {
                var col = Color.FromRgb((byte)(baseColor.Item1 * 255), (byte)(baseColor.Item2 * 255), (byte)(baseColor.Item3 * 255)); var w = o.Kind == SceneKind.Pipe ? 2.2 : 1;
                foreach (var (a, b) in WireEdges.Of(m).Where(e => o.Kind != SceneKind.Wall || IsRealEdge(m, e)))
                    lines.Add((S(m.Positions[a * 3], m.Positions[a * 3 + 1], m.Positions[a * 3 + 2]), S(m.Positions[b * 3], m.Positions[b * 3 + 1], m.Positions[b * 3 + 2]), col, w));
                continue;
            }
            for (var t = 0; t + 2 < m.Indices.Count; t += 3)
            {
                int i0 = m.Indices[t], i1 = m.Indices[t + 1], i2 = m.Indices[t + 2];
                float nx = (m.Normals[i0 * 3] + m.Normals[i1 * 3] + m.Normals[i2 * 3]) / 3, ny = (m.Normals[i0 * 3 + 1] + m.Normals[i1 * 3 + 1] + m.Normals[i2 * 3 + 1]) / 3, nz = (m.Normals[i0 * 3 + 2] + m.Normals[i1 * 3 + 2] + m.Normals[i2 * 3 + 2]) / 3;
                var lam = Math.Clamp(Math.Abs(nx * light.X + ny * light.Y + nz * light.Z), 0, 1) * 0.65 + 0.35;
                var c = Color.FromArgb(alpha, (byte)(baseColor.Item1 * 255 * lam), (byte)(baseColor.Item2 * 255 * lam), (byte)(baseColor.Item3 * 255 * lam));
                double d = 0; foreach (var i in new[] { i0, i1, i2 }) d += cam.Project(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]).Depth;
                tris.Add((d / 3, S(m.Positions[i0 * 3], m.Positions[i0 * 3 + 1], m.Positions[i0 * 3 + 2]), S(m.Positions[i1 * 3], m.Positions[i1 * 3 + 1], m.Positions[i1 * 3 + 2]), S(m.Positions[i2 * 3], m.Positions[i2 * 3 + 1], m.Positions[i2 * 3 + 2]), c));
            }
        }
        foreach (var t in tris.OrderByDescending(t => t.Depth))      // far → near
        {
            var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(t.A, true); c.LineTo(t.B); c.LineTo(t.C); c.EndFigure(true); }
            ctx.DrawGeometry(new SolidColorBrush(t.Color), new Pen(new SolidColorBrush(t.Color), 0.6), g);
        }
        foreach (var l in lines) ctx.DrawLine(new Pen(new SolidColorBrush(l.Color), l.W), l.A, l.B);
        var label = Controller.Wireframe ? T("view3d.wire") : Controller.XRay ? T("view3d.xray") : T("view3d.solid");
        ctx.DrawText(new FormattedText(T("view3d.label", label), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, dark ? Brushes.Gainsboro : Brushes.DimGray), new Point(12, 8));
    }

    // wall triangulation diagonals (coplanar fan edges) are not drawn in wireframe: keep edges whose two adjacent faces differ or that border a face boundary
    private static bool IsRealEdge(Mesh m, (int A, int B) e)
    {
        float Dx(int i, int j, int k) => m.Positions[i * 3 + k] - m.Positions[j * 3 + k];
        var dz = Math.Abs(Dx(e.A, e.B, 2)); var dx = Math.Abs(Dx(e.A, e.B, 0)); var dy = Math.Abs(Dx(e.A, e.B, 1));
        var axes = (dx > 1e-6 ? 1 : 0) + (dy > 1e-6 ? 1 : 0) + (dz > 1e-6 ? 1 : 0);
        return axes <= 1 || (dz < 1e-6 && true) ;   // axis-aligned edges and horizontal outline edges
    }
    private static (double X, double Y, double Z) Norm(double x, double y, double z) { var l = Math.Sqrt(x * x + y * y + z * z); return (x / l, y / l, z / l); }
}

public interface IViewport3D
{
    ViewportController Controller { get; }
    Control Control { get; }
    void Rebuild();
}
