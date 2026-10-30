using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImmoCad.Plan;

namespace ImmoCad.Desktop;

/// <summary>Shows a picture with zoom/pan, click points, a red mask overlay and traced outlines. Used by the import wizard.</summary>
public class ImageCanvas : Control
{
    public Bitmap? Picture { get; private set; }
    public double Fade { get; set; } = 1.0;
    public List<(double X, double Y)> Points { get; } = new();
    public Bitmap? MaskOverlay { get; private set; }
    public IReadOnlyList<WallOutline>? Outlines { get; set; }
    public ISet<int>? Rejected { get; set; }
    /// <summary>Alignment helpers: points picked along a wall (cyan) and guide lines that follow the pointer.</summary>
    public List<(double X, double Y)> RefPoints { get; } = new();
    public bool Guides { get; set; }
    /// <summary>Live preview of a rotation not yet applied (degrees, clockwise).</summary>
    public double PreviewDeg { get; set; }
    public event Action<(double X, double Y)>? PixelClicked;
    public event Action<int>? OutlineClicked;
    private double _scale = 1; private Point _offset; private Point? _pan; private bool _fit = true; private bool _space; private Point? _cursor;

    public ImageCanvas()
    {
        Focusable = true; ClipToBounds = true;
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) => { ZoomAt(e.GetPosition(this), Math.Exp(e.Delta.X)); e.Handled = true; });   // trackpad pinch, as in the plan
    }
    public double Zoom => _scale;

    internal void PixelClicked_ForTest(double x, double y) => PixelClicked?.Invoke((x, y));
    public void SetImage(byte[] png, bool keepView = false) { Picture = new Bitmap(new MemoryStream(png)); _fit = !keepView || _scale <= 0; PreviewDeg = 0; InvalidateVisual(); }

    /// <summary>"in", "out" (about the centre) or "fit".</summary>
    public void ZoomBy(string how)
    {
        if (how == "fit") { _fit = true; InvalidateVisual(); return; }
        ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), how == "in" ? 1.25 : 1 / 1.25);
    }
    private void ZoomAt(Point p, double factor)
    {
        var before = ToPixel(p); _scale = Math.Clamp(_scale * factor, 0.02, 40);
        _offset = new Point(p.X - before.X * _scale, p.Y - before.Y * _scale); InvalidateVisual();
    }

    public void SetMask(byte[] mask, int w, int h)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            var row = new byte[w * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++) { var on = mask[y * w + x] != 0; row[x * 4] = 0; row[x * 4 + 1] = 0; row[x * 4 + 2] = (byte)(on ? 170 : 0); row[x * 4 + 3] = (byte)(on ? 170 : 0); }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }
        }
        MaskOverlay = bmp; InvalidateVisual();
    }
    public void ClearMask() { MaskOverlay = null; InvalidateVisual(); }

    private Point ToScreen(double x, double y) => new(_offset.X + x * _scale, _offset.Y + y * _scale);
    private (double X, double Y) ToPixel(Point p) => ((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus(); var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed || _space || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { _pan = e.GetPosition(this); e.Pointer.Capture(this); return; }
        var px = ToPixel(e.GetPosition(this));
        if (Outlines != null)
        {
            for (var i = Outlines.Count - 1; i >= 0; i--)
                if (Inside(Outlines[i].Outline, px) && !Outlines[i].Holes.Any(h => Inside(h, px))) { OutlineClicked?.Invoke(i); return; }
        }
        PixelClicked?.Invoke(px);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _cursor = e.GetPosition(this);
        if (_pan is not { } l) return;
        var p = e.GetPosition(this); _offset += p - l; _pan = p; InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e) { _cursor = null; }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _pan = null; e.Pointer.Capture(null); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space) { _space = true; Cursor = new Cursor(StandardCursorType.Hand); e.Handled = true; }
        else if (e.Key is Key.OemPlus or Key.Add) { ZoomBy("in"); e.Handled = true; }
        else if (e.Key is Key.OemMinus or Key.Subtract) { ZoomBy("out"); e.Handled = true; }
        else if (e.Key is Key.D0 or Key.NumPad0) { ZoomBy("fit"); e.Handled = true; }
    }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.Key == Key.Space) { _space = false; Cursor = null; e.Handled = true; } }
    protected override void OnLostFocus(FocusChangedEventArgs e) { _space = false; Cursor = null; base.OnLostFocus(e); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (OperatingSystem.IsMacOS() && !e.KeyModifiers.HasFlag(MenuFactory.CommandModifiers)) { _offset += new Vector(e.Delta.X * 40, e.Delta.Y * 40); InvalidateVisual(); e.Handled = true; return; }   // trackpad: two fingers pan
        ZoomAt(e.GetPosition(this), Math.Pow(1.15, Math.Clamp(e.Delta.Y, -3, 3)));
    }

    private static bool Inside(IReadOnlyList<PointPx> poly, (double X, double Y) p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Tokens.Brush(this, "ImageCanvasBackground", Color.FromRgb(40, 42, 46)), new Rect(Bounds.Size));
        if (Picture == null) return;
        if (_fit && Bounds.Width > 10)
        {
            _scale = Math.Min(Bounds.Width / Picture.PixelSize.Width, Bounds.Height / Picture.PixelSize.Height) * 0.95;
            _offset = new Point((Bounds.Width - Picture.PixelSize.Width * _scale) / 2, (Bounds.Height - Picture.PixelSize.Height * _scale) / 2); _fit = false;
        }
        var dest = new Rect(_offset.X, _offset.Y, Picture.PixelSize.Width * _scale, Picture.PixelSize.Height * _scale);
        var tilt = Matrix.CreateTranslation(-dest.Center.X, -dest.Center.Y) * Matrix.CreateRotation(PreviewDeg * Math.PI / 180) * Matrix.CreateTranslation(dest.Center.X, dest.Center.Y);
        using (ctx.PushTransform(tilt)) using (ctx.PushOpacity(Fade)) ctx.DrawImage(Picture, new Rect(Picture.PixelSize.ToSize(1)), dest);
        if (MaskOverlay != null) ctx.DrawImage(MaskOverlay, new Rect(MaskOverlay.PixelSize.ToSize(1)), dest);
        if (Outlines != null)
            for (var i = 0; i < Outlines.Count; i++)
            {
                var rej = Rejected?.Contains(i) == true;
                var pen = new Pen(rej ? Brushes.Gray : Brushes.LimeGreen, 2, rej ? DashStyle.Dash : null);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    foreach (var ring in new[] { Outlines[i].Outline }.Concat(Outlines[i].Holes))
                    { c.BeginFigure(ToScreen(ring[0].X, ring[0].Y), false); foreach (var p in ring.Skip(1)) c.LineTo(ToScreen(p.X, p.Y)); c.EndFigure(true); }
                }
                ctx.DrawGeometry(rej ? new SolidColorBrush(Color.FromArgb(60, 120, 120, 120)) : new SolidColorBrush(Color.FromArgb(40, 50, 205, 50)), pen, g);
            }
        if (Guides)
        {   // fixed horizontal/vertical lines over the plan: a wall edge that is parallel to them is straight (0.1° = 1 px drift per 570 px)
            var g = new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 200, 255)), 1); const double step = 60;
            for (var x = step; x < Bounds.Width; x += step) ctx.DrawLine(g, new Point(x, 0), new Point(x, Bounds.Height));
            for (var y = step; y < Bounds.Height; y += step) ctx.DrawLine(g, new Point(0, y), new Point(Bounds.Width, y));
        }
        for (var i = 0; i < RefPoints.Count; i++)
        {
            var s = ToScreen(RefPoints[i].X, RefPoints[i].Y); var cy = new Pen(Brushes.Cyan, 2);
            ctx.DrawEllipse(Brushes.Cyan, new Pen(Brushes.White, 2), s, 5, 5);
            if (i % 2 == 1) ctx.DrawLine(cy, ToScreen(RefPoints[i - 1].X, RefPoints[i - 1].Y), s);
        }
        for (var i = 0; i < Points.Count; i++)
        {
            var s = ToScreen(Points[i].X, Points[i].Y);
            ctx.DrawEllipse(Brushes.Orange, new Pen(Brushes.White, 2), s, 6, 6);
            if (i % 2 == 1) ctx.DrawLine(new Pen(Brushes.Orange, 2), ToScreen(Points[i - 1].X, Points[i - 1].Y), s);
        }
    }
}
