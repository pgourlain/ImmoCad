using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using static ImmoCad.Core.Translations;
using ImmoCad.Scene;

namespace ImmoCad.Desktop;

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
        Controller.Handles = () => Vm?.Handles3D() ?? []; Controller.HandleDropped = (n, nd, mm) => Vm?.DropPipeEnd3D(n, nd, mm);
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
        var half = Math.Min(Bounds.Width, Bounds.Height) / 2; var cam = Controller.Camera; double cx0 = Bounds.Width / 2, cy0 = Bounds.Height / 2;
        Point S(float x, float y, float z) { var (px, py, _) = cam.Project(x, y, z); return new(cx0 + px * half, cy0 - py * half); }
        var light = Norm(0.4, -0.5, 0.8);
        // every vertex is projected once; triangles facing away (closed opaque meshes) and the ones smaller than a pixel are dropped; the rest is sorted far → near
        var tris = new List<(double Depth, Point A, Point B, Point C, uint Color)>(); var lines = new List<(Point A, Point B, Color Color, double W)>();
        double[] xs = [], ys = [], ds = [];
        foreach (var o in Controller.Objects)
        {
            var m = o.Mesh; var baseColor = o.Highlight ? (1f, 0.55f, 0f) : o.Color; var alpha = (byte)(o.Alpha * 255); var n = m.Positions.Count / 3;
            if (xs.Length < n) { xs = new double[n * 2]; ys = new double[n * 2]; ds = new double[n * 2]; }
            for (var v = 0; v < n; v++) { var (px, py, d) = cam.Project(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]); xs[v] = cx0 + px * half; ys[v] = cy0 - py * half; ds[v] = d; }
            if (Controller.Wireframe)
            {
                var col = Color.FromRgb((byte)(baseColor.Item1 * 255), (byte)(baseColor.Item2 * 255), (byte)(baseColor.Item3 * 255)); var w = o.Kind == SceneKind.Pipe ? 2.2 : 1;
                foreach (var (a, b) in _outlines.GetValue(m, WireEdges.Outline)) lines.Add((new(xs[a], ys[a]), new(xs[b], ys[b]), col, w));
                continue;
            }
            var cull = o.Alpha >= 0.999f; var minArea = Controller.Dragging ? 2.0 : 0.1;                                           // see-through walls (X-ray) show their back faces too
            for (var t = 0; t + 2 < m.Indices.Count; t += 3)
            {
                int i0 = m.Indices[t], i1 = m.Indices[t + 1], i2 = m.Indices[t + 2];
                var area = (xs[i1] - xs[i0]) * (ys[i2] - ys[i0]) - (xs[i2] - xs[i0]) * (ys[i1] - ys[i0]);
                if (cull && area > 0 || Math.Abs(area) < minArea) continue;
                float nx = m.Normals[i0 * 3] + m.Normals[i1 * 3] + m.Normals[i2 * 3], ny = m.Normals[i0 * 3 + 1] + m.Normals[i1 * 3 + 1] + m.Normals[i2 * 3 + 1], nz = m.Normals[i0 * 3 + 2] + m.Normals[i1 * 3 + 2] + m.Normals[i2 * 3 + 2];
                var lam = Math.Round(Math.Clamp(Math.Abs(nx * light.X + ny * light.Y + nz * light.Z) / 3, 0, 1) * 16) / 16 * 0.65 + 0.35;     // 16 shades, so neighbours share one brush
                var c = (uint)alpha << 24 | (uint)(byte)(baseColor.Item1 * 255 * lam) << 16 | (uint)(byte)(baseColor.Item2 * 255 * lam) << 8 | (byte)(baseColor.Item3 * 255 * lam);
                tris.Add((ds[i0] + ds[i1] + ds[i2], new(xs[i0], ys[i0]), new(xs[i1], ys[i1]), new(xs[i2], ys[i2]), c));
            }
        }
        tris.Sort((a, b) => b.Depth.CompareTo(a.Depth));                           // far → near
        for (var k = 0; k < tris.Count;)
        {   // consecutive triangles of the same shade go in one geometry
            var c = tris[k].Color; var g = new StreamGeometry();
            using (var gc = g.Open()) for (; k < tris.Count && tris[k].Color == c; k++) { var t = tris[k]; gc.BeginFigure(t.A, true); gc.LineTo(t.B); gc.LineTo(t.C); gc.EndFigure(true); }
            var (brush, pen) = Paint(c); ctx.DrawGeometry(brush, Controller.Dragging ? null : pen, g);      // no seam stroke while turning: faster
        }
        foreach (var l in lines) ctx.DrawLine(new Pen(new SolidColorBrush(l.Color), l.W), l.A, l.B);
        Controller.Size(Bounds.Width, Bounds.Height);
        foreach (var h in Controller.Handles?.Invoke() ?? [])                // selected pipe: axis gizmo and end handles
        {
            var a = S((float)h.Other.X, (float)h.Other.Y, (float)h.Other.Z); var b = S((float)h.End.X, (float)h.End.Y, (float)h.End.Z);
            ctx.DrawLine(new Pen(Brushes.OrangeRed, 2.5, DashStyle.Dash), a, b); ctx.DrawRectangle(Brushes.White, new Pen(Brushes.OrangeRed, 2), new Rect(b.X - 6, b.Y - 6, 12, 12));
            var ax = new Point(b.X + (b.X - a.X) * 0.0, b.Y); _ = ax;
            if (Controller.ActiveHandle is { } act && act.NodeId == h.NodeId && Controller.HandlePreview is { } pv)
            {
                var c = S((float)pv.X, (float)pv.Y, (float)pv.Z); ctx.DrawLine(new Pen(Brushes.DodgerBlue, 3), b, c); ctx.DrawEllipse(Brushes.DodgerBlue, null, c, 6, 6);
                var len = Math.Sqrt(Math.Pow(pv.X - h.Other.X, 2) + Math.Pow(pv.Y - h.Other.Y, 2) + Math.Pow(pv.Z - h.Other.Z, 2));
                ctx.DrawText(new FormattedText($"{len:0.00} m", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.DodgerBlue), c + new Point(10, -18));
            }
        }
        var label = Controller.Wireframe ? T("view3d.wire") : Controller.XRay ? T("view3d.xray") : T("view3d.solid");
        ctx.DrawText(new FormattedText(T("view3d.label", label), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, dark ? Brushes.Gainsboro : Brushes.DimGray), new Point(12, 8));
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, List<(int A, int B)>> _outlines = new();
    private readonly Dictionary<uint, (IBrush Brush, IPen Pen)> _paint = new();
    /// <summary>Fill and a thin stroke of the same colour (hides the seams between triangles), made once per shade.</summary>
    private (IBrush Brush, IPen Pen) Paint(uint argb)
    {
        if (_paint.TryGetValue(argb, out var p)) return p;
        var b = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromUInt32(argb)); return _paint[argb] = (b, new Avalonia.Media.Immutable.ImmutablePen(b, 0.6));
    }
    private static (double X, double Y, double Z) Norm(double x, double y, double z) { var l = Math.Sqrt(x * x + y * y + z * z); return (x / l, y / l, z / l); }
}

public interface IViewport3D
{
    ViewportController Controller { get; }
    Control Control { get; }
    void Rebuild();
}
