using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImmeubleCad.Plan;

namespace ImmeubleCad.Desktop;

/// <summary>Shows a picture with zoom/pan, click points, a red mask overlay and traced outlines. Used by the import wizard.</summary>
public class ImageCanvas : Control
{
    public Bitmap? Picture { get; private set; }
    public double Fade { get; set; } = 1.0;
    public List<(double X, double Y)> Points { get; } = new();
    public Bitmap? MaskOverlay { get; private set; }
    public IReadOnlyList<WallOutline>? Outlines { get; set; }
    public ISet<int>? Rejected { get; set; }
    public event Action<(double X, double Y)>? PixelClicked;
    public event Action<int>? OutlineClicked;
    private double _scale = 1; private Point _offset; private Point? _pan; private bool _fit = true;

    internal void PixelClicked_ForTest(double x, double y) => PixelClicked?.Invoke((x, y));
    public void SetImage(byte[] png) { Picture = new Bitmap(new MemoryStream(png)); _fit = true; InvalidateVisual(); }

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
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed) { _pan = e.GetPosition(this); return; }
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
        if (_pan is not { } l) return; var p = e.GetPosition(this); _offset += p - l; _pan = p; InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) => _pan = null;
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this); var before = ToPixel(p); _scale = Math.Clamp(_scale * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.02, 40);
        _offset = new Point(p.X - before.X * _scale, p.Y - before.Y * _scale); InvalidateVisual();
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
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(40, 42, 46)), new Rect(Bounds.Size));
        if (Picture == null) return;
        if (_fit && Bounds.Width > 10)
        {
            _scale = Math.Min(Bounds.Width / Picture.PixelSize.Width, Bounds.Height / Picture.PixelSize.Height) * 0.95;
            _offset = new Point((Bounds.Width - Picture.PixelSize.Width * _scale) / 2, (Bounds.Height - Picture.PixelSize.Height * _scale) / 2); _fit = false;
        }
        var dest = new Rect(_offset.X, _offset.Y, Picture.PixelSize.Width * _scale, Picture.PixelSize.Height * _scale);
        using (ctx.PushOpacity(Fade)) ctx.DrawImage(Picture, new Rect(Picture.PixelSize.ToSize(1)), dest);
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
        for (var i = 0; i < Points.Count; i++)
        {
            var s = ToScreen(Points[i].X, Points[i].Y);
            ctx.DrawEllipse(Brushes.Orange, new Pen(Brushes.White, 2), s, 6, 6);
            if (i % 2 == 1) ctx.DrawLine(new Pen(Brushes.Orange, 2), ToScreen(Points[i - 1].X, Points[i - 1].Y), s);
        }
    }
}
