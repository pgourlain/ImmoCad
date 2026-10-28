using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ImmoCad.Scene;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>
/// Small orientation cube drawn over the 3D view: it follows the camera, click a face to look at it (front / back / left / right / top / bottom),
/// drag to orbit, ⌂ for the isometric view.
/// </summary>
public sealed class ViewCube : Control
{
    public enum Face { Front, Back, Left, Right, Top, Bottom }
    public ViewportController? Controller { get; set; }
    private Face? _hover; private Point? _drag; private bool _dragged;

    public ViewCube() { Width = 112; Height = 112; Focusable = false; ClipToBounds = false; }

    private static readonly (Face Face, (double X, double Y, double Z) N, (double X, double Y, double Z)[] Quad)[] Faces =
    [
        (Face.Front,  (0, -1, 0), [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)]),
        (Face.Back,   (0, 1, 0),  [(1, 1, -1), (-1, 1, -1), (-1, 1, 1), (1, 1, 1)]),
        (Face.Right,  (1, 0, 0),  [(1, -1, -1), (1, 1, -1), (1, 1, 1), (1, -1, 1)]),
        (Face.Left,   (-1, 0, 0), [(-1, 1, -1), (-1, -1, -1), (-1, -1, 1), (-1, 1, 1)]),
        (Face.Top,    (0, 0, 1),  [(-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]),
        (Face.Bottom, (0, 0, -1), [(-1, 1, -1), (1, 1, -1), (1, -1, -1), (-1, -1, -1)]),
    ];

    /// <summary>Yaw / pitch that look at a face from outside.</summary>
    public static (double Yaw, double Pitch) Look(Face f) => f switch
    {
        Face.Front => (0, 0.02), Face.Back => (Math.PI, 0.02), Face.Right => (Math.PI / 2, 0.02), Face.Left => (-Math.PI / 2, 0.02),
        Face.Top => (Controller0Yaw, Math.PI / 2 - 0.01), _ => (Controller0Yaw, -Math.PI / 2 + 0.01),
    };
    private const double Controller0Yaw = 0;

    private static string Name(Face f) => T("cube." + f.ToString().ToLowerInvariant());
    private double CubeScale => Math.Min(Bounds.Width, Bounds.Height) * 0.26;
    private Point Centre => new(Bounds.Width / 2, Bounds.Height / 2 + 2);
    private Rect HomeRect => new(2, 2, 20, 20);

    private OrbitCamera View() { var c = Controller?.Camera; return new OrbitCamera { Yaw = c?.Yaw ?? Math.PI / 4, Pitch = c?.Pitch ?? Math.PI / 5, Distance = 2, Target = (0, 0, 0) }; }
    private Point S(OrbitCamera cam, (double X, double Y, double Z) v) { var (x, y, _) = cam.Project(v.X, v.Y, v.Z); return new(Centre.X + x * CubeScale, Centre.Y - y * CubeScale); }

    private List<(Face Face, Point[] Poly)> Visible()
    {
        var cam = View(); var (cy, sy) = (Math.Cos(cam.Yaw), Math.Sin(cam.Yaw)); var (cp, sp) = (Math.Cos(cam.Pitch), Math.Sin(cam.Pitch));
        var eye = (X: sy * cp, Y: -cy * cp, Z: sp);                                           // direction from the target to the camera
        return Faces.Where(f => f.N.X * eye.X + f.N.Y * eye.Y + f.N.Z * eye.Z > 1e-6).Select(f => (f.Face, f.Quad.Select(q => S(cam, q)).ToArray())).ToList();
    }

    public Face? FaceAt(Point p)
    {
        foreach (var (face, poly) in Visible()) if (Inside(poly, p)) return face;
        return null;
    }
    private static bool Inside(Point[] poly, Point p)
    {
        var inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    public override void Render(DrawingContext ctx)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var edge = new Pen(dark ? Brushes.Gainsboro : Brushes.DimGray, 1.2);
        var fillOff = new SolidColorBrush(dark ? Color.FromRgb(58, 62, 70) : Color.FromRgb(236, 238, 242)); var fillOn = new SolidColorBrush(Color.FromRgb(255, 170, 70));
        var text = dark ? Brushes.White : Brushes.Black;
        foreach (var (face, poly) in Visible())
        {
            var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(poly[0], true); foreach (var q in poly.Skip(1)) c.LineTo(q); c.EndFigure(true); }
            ctx.DrawGeometry(_hover == face ? fillOn : fillOff, edge, g);
            var cx = poly.Average(q => q.X); var cy = poly.Average(q => q.Y);
            var area = Math.Abs(poly.Select((q, i) => q.X * poly[(i + 1) % poly.Length].Y - poly[(i + 1) % poly.Length].X * q.Y).Sum()) / 2;
            if (area < 200) continue;                                                           // too foreshortened for a label
            var ft = new FormattedText(Name(face), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, Math.Clamp(Math.Sqrt(area) / 4.2, 8, 12), text);
            ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
        }
        ctx.DrawRectangle(_hover == null && _homeHover ? fillOn : fillOff, edge, HomeRect, 4);
        var home = new FormattedText("⌂", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, text);
        ctx.DrawText(home, new Point(HomeRect.X + 10 - home.Width / 2, HomeRect.Y + 10 - home.Height / 2));
    }

    private bool _homeHover;
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag is { } l)
        {
            if (Math.Abs(p.X - l.X) + Math.Abs(p.Y - l.Y) > 1) _dragged = true;
            Controller?.Camera.Orbit(-(p.X - l.X) * 0.012, (p.Y - l.Y) * 0.012); _drag = p; Controller?.Invalidate?.Invoke(); InvalidateVisual(); return;
        }
        var f = FaceAt(p); var h = f == null && HomeRect.Contains(p);
        if (f != _hover || h != _homeHover) { _hover = f; _homeHover = h; InvalidateVisual(); }
    }
    protected override void OnPointerExited(PointerEventArgs e) { _hover = null; _homeHover = false; InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { _drag = e.GetPosition(this); _dragged = false; e.Pointer.Capture(this); e.Handled = true; }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var p = e.GetPosition(this); _drag = null; e.Pointer.Capture(null); e.Handled = true;
        if (_dragged) return;
        Click(p);
    }

    /// <summary>Click on a face → look at it; on ⌂ → isometric view.</summary>
    public void Click(Point p)
    {
        if (Controller is not { } c) return;
        if (FaceAt(p) is { } f) { var (yaw, pitch) = Look(f); c.LookFrom(f is Face.Top or Face.Bottom ? c.Camera.Yaw : yaw, pitch); }
        else if (HomeRect.Contains(p)) c.LookFrom(Math.PI / 4, Math.PI / 5);
        InvalidateVisual();
    }
}
