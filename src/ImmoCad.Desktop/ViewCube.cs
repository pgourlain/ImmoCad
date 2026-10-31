using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ImmoCad.Scene;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>
/// Small orientation cube drawn over the 3D view: it follows the camera, click a face to look at it (front / back / left / right / top / bottom),
/// drag to orbit, ⌂ for the isometric view. In 2D, two curved arrows turn the drawing a quarter turn about the axis the view looks along.
/// </summary>
public sealed class ViewCube : Control
{
    public enum Face { Front, Back, Left, Right, Top, Bottom }
    public ViewportController? Controller { get; set; }
    private Face? _hover; private Point? _drag; private bool _dragged;

    // 2D mode: the cube chooses the plane of the canvas (top = XY, front/back = XZ, right/left = YZ); drag turns the cube only, the current face is tinted
    public Func<Face>? Current { get; set; } public Action<Face>? FaceChosen { get; set; }
    private bool Flat2D => FaceChosen != null; private double _yaw2 = 0.6, _pitch2 = 0.5;
    /// <summary>Curved arrow clicked: -1 counter-clockwise, +1 clockwise.</summary>
    public Action<int>? Turned { get; set; }
    /// <summary>Extra yaw of the 2D cube, so that it follows the turned drawing.</summary>
    public Func<double>? Spin { get; set; }

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
    private double CubeScale => Math.Min(Bounds.Width, Bounds.Height) * (Flat2D ? 0.185 : 0.26);   // 2D: the control is larger, the outer ring holds the curved arrows
    private Point Centre => new(Bounds.Width / 2, Bounds.Height / 2 + 2);
    private Rect HomeRect => Flat2D ? new(16, Bounds.Height - 36, 20, 20) : new(2, 2, 20, 20);

    private OrbitCamera View() { if (Flat2D) return new OrbitCamera { Yaw = _yaw2 + (Spin?.Invoke() ?? 0), Pitch = _pitch2, Distance = 2, Target = (0, 0, 0) }; var c = Controller?.Camera; return new OrbitCamera { Yaw = c?.Yaw ?? Math.PI / 4, Pitch = c?.Pitch ?? Math.PI / 5, Distance = 2, Target = (0, 0, 0) }; }
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

    private const double ArrowGap = 10 * Math.PI / 180, ArrowBody = 42 * Math.PI / 180, ArrowHead = 16 * Math.PI / 180;
    private Point OnCircle(double r, double a) => new(Centre.X + r * Math.Cos(a), Centre.Y + r * Math.Sin(a));

    /// <summary>Outline of a curved arrow: it starts near the top of the cube and sweeps down the left (-1) or right (+1) side.</summary>
    private Point[] ArrowPoly(int dir)
    {
        double A(double from12) => -Math.PI / 2 + dir * from12;
        const int n = 8; var body = Enumerable.Range(0, n + 1).Select(i => ArrowGap + ArrowBody * i / n).ToList(); var end = ArrowGap + ArrowBody;
        return [.. body.Select(a => OnCircle(61, A(a))), OnCircle(66, A(end)), OnCircle(57.5, A(end + ArrowHead)), OnCircle(49, A(end)), .. Enumerable.Reverse(body).Select(a => OnCircle(54, A(a)))];
    }

    /// <summary>Curved arrow under p (-1 / +1); the cube's faces come first. The hit zone is wider than the drawing.</summary>
    public int? ArrowAt(Point p)
    {
        if (!Flat2D || Turned == null || FaceAt(p) != null) return null;
        double dx = p.X - Centre.X, dy = p.Y - Centre.Y, r = Math.Sqrt(dx * dx + dy * dy); if (r < 46 || r > 70) return null;
        var from12 = Math.Atan2(dx, -dy);                                                       // angle from the top, positive on the right
        return Math.Abs(from12) < ArrowGap || Math.Abs(from12) > ArrowGap + ArrowBody + ArrowHead ? null : Math.Sign(from12);
    }

    public override void Render(DrawingContext ctx)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var edge = new Pen(dark ? Brushes.Gainsboro : Brushes.DimGray, 1.2);
        var fillOff = new SolidColorBrush(dark ? Color.FromRgb(58, 62, 70) : Color.FromRgb(236, 238, 242)); var fillOn = new SolidColorBrush(Color.FromRgb(255, 170, 70)); var fillCur = new SolidColorBrush(dark ? Color.FromRgb(120, 90, 50) : Color.FromRgb(255, 214, 160));
        var text = dark ? Brushes.White : Brushes.Black;
        foreach (var (face, poly) in Visible())
        {
            var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(poly[0], true); foreach (var q in poly.Skip(1)) c.LineTo(q); c.EndFigure(true); }
            ctx.DrawGeometry(_hover == face ? fillOn : Current?.Invoke() == face ? fillCur : fillOff, edge, g);
            var cx = poly.Average(q => q.X); var cy = poly.Average(q => q.Y);
            var area = Math.Abs(poly.Select((q, i) => q.X * poly[(i + 1) % poly.Length].Y - poly[(i + 1) % poly.Length].X * q.Y).Sum()) / 2;
            if (area < 200) continue;                                                           // too foreshortened for a label
            var ft = new FormattedText(Name(face), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, Math.Clamp(Math.Sqrt(area) / 4.2, 8, 12), text);
            ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
        }
        if (Flat2D && Turned != null)
            foreach (var dir in new[] { -1, 1 })
            {
                var poly = ArrowPoly(dir); var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(poly[0], true); foreach (var q in poly.Skip(1)) c.LineTo(q); c.EndFigure(true); }
                ctx.DrawGeometry(_arrowHover == dir ? ArrowOn : fillOff, edge, g);
            }
        ctx.DrawRectangle(_hover == null && _homeHover ? fillOn : fillOff, edge, HomeRect, 4);
        var home = new FormattedText("⌂", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, text);
        ctx.DrawText(home, new Point(HomeRect.X + 10 - home.Width / 2, HomeRect.Y + 10 - home.Height / 2));
    }

    private bool _homeHover; private int? _arrowHover;
    private static readonly IBrush ArrowOn = new SolidColorBrush(Color.FromRgb(160, 216, 250));
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag is { } l)
        {
            if (Math.Abs(p.X - l.X) + Math.Abs(p.Y - l.Y) > 1) _dragged = true;
            if (Flat2D) { _yaw2 -= (p.X - l.X) * 0.012; _pitch2 = Math.Clamp(_pitch2 + (p.Y - l.Y) * 0.012, 0.05, 1.5); _drag = p; InvalidateVisual(); return; }
            Controller?.Camera.Orbit(-(p.X - l.X) * 0.012, (p.Y - l.Y) * 0.012); _drag = p; Controller?.Invalidate?.Invoke(); InvalidateVisual(); return;
        }
        var f = FaceAt(p); var h = f == null && HomeRect.Contains(p); var ar = ArrowAt(p);
        if (ar != _arrowHover) ToolTip.SetTip(this, ar is { } d ? T(d < 0 ? "cube.turnleft" : "cube.turnright") : null);
        if (f != _hover || h != _homeHover || ar != _arrowHover) { _hover = f; _homeHover = h; _arrowHover = ar; InvalidateVisual(); }
    }
    protected override void OnPointerExited(PointerEventArgs e) { _hover = null; _homeHover = false; _arrowHover = null; ToolTip.SetTip(this, null); InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { var p = e.GetPosition(this); _drag = ArrowAt(p) == null ? p : null; _dragged = false; e.Pointer.Capture(this); e.Handled = true; }   // an arrow is a click, not a drag
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var p = e.GetPosition(this); _drag = null; e.Pointer.Capture(null); e.Handled = true;
        if (_dragged) return;
        Click(p);
    }

    /// <summary>Click on a face → look at it; on ⌂ → isometric view; on a curved arrow (2D) → quarter turn.</summary>
    public void Click(Point p)
    {
        if (Flat2D) { if (FaceAt(p) is { } f2 && f2 != Face.Bottom) FaceChosen!(f2); else if (ArrowAt(p) is { } dir) Turned?.Invoke(dir); else if (HomeRect.Contains(p)) FaceChosen!(Face.Top); InvalidateVisual(); return; }
        if (Controller is not { } c) return;
        if (FaceAt(p) is { } f) { var (yaw, pitch) = Look(f); c.LookFrom(f is Face.Top or Face.Bottom ? c.Camera.Yaw : yaw, pitch); }
        else if (HomeRect.Contains(p)) c.LookFrom(Math.PI / 4, Math.PI / 5);
        InvalidateVisual();
    }
}
