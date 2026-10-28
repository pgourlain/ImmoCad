using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ImmeubleCad.Core.Geometry;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Networks;

namespace ImmeubleCad.Desktop;

/// <summary>Front view of one wall face: level lines with altitudes, pipes in the plane. Riser tool = vertical pipe (click, or drag to set top/bottom, snapping to level lines); Pipe tool = horizontal pipe along the face.</summary>
public class ElevationView : Control
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private double _scale = 0.1; private double _ox = 40, _oy;               // px per mm; screen origin of (u=0, z=zMin)
    private bool _fit = true;
    private Point? _down, _now; private double _z0;

    public double ToU(double x) => (x - _ox) / _scale;
    public double ToZ(double y) => (Bounds.Height - _oy - y) / _scale;
    private Point S(double u, double z) => new(_ox + u * _scale, Bounds.Height - _oy - z * _scale);

    public void Fit()
    {
        if (Vm?.Plane is not { } p || Vm.Building.Levels.Count == 0 || Bounds.Width < 10) return;
        var top = Vm.Building.Levels[^1].AltitudeMm + Vm.Building.Levels[^1].HeightMm; var bottom = Vm.Building.Levels[0].AltitudeMm;
        _scale = Math.Min((Bounds.Width - 140) / p.Length, (Bounds.Height - 80) / Math.Max(1, top - bottom)); _ox = 40; _oy = 40 - bottom * _scale; _fit = false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus(); if (Vm?.Plane == null) return;
        _down = _now = e.GetPosition(this); _z0 = ElevationPlane.SnapZ(Vm.Building, (int)ToZ(_down.Value.Y), (int)(12 / _scale)); e.Pointer.Capture(this);
    }
    protected override void OnPointerMoved(PointerEventArgs e) { if (_down != null) { _now = e.GetPosition(this); InvalidateVisual(); } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_down is not { } d || _now is not { } n || Vm is not { } vm) return;
        var drag = Math.Abs(n.X - d.X) + Math.Abs(n.Y - d.Y) > 4; _down = null;
        var reach = (int)(12 / _scale);
        if (vm.IsRiserTool) vm.PlaceVerticalPipe(ToU(d.X), drag ? _z0 : null, drag ? ElevationPlane.SnapZ(vm.Building, (int)ToZ(n.Y), reach) : null);
        else if (vm.IsPipeTool && drag) vm.PlaceHorizontalPipe(ToU(d.X), ToU(n.X), _z0);
        InvalidateVisual();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this); var u = ToU(p.X); var z = ToZ(p.Y); _scale = Math.Clamp(_scale * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.01, 3);
        _ox = p.X - u * _scale; _oy = Bounds.Height - p.Y - z * _scale; InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        ctx.FillRectangle(new SolidColorBrush(dark ? Color.FromRgb(30, 32, 36) : Color.FromRgb(250, 250, 248)), new Rect(Bounds.Size));
        if (Vm?.Plane is not { } plane) return;
        if (_fit) Fit();
        var b = Vm.Building; var txt = dark ? Brushes.Gainsboro : Brushes.DimGray;
        // face
        var bottom = b.Levels[0].AltitudeMm; var top = b.Levels[^1].AltitudeMm + b.Levels[^1].HeightMm;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 120, 120, 130)), new Pen(Brushes.Gray, 1), new Rect(S(0, top), S(plane.Length, bottom)));
        // level lines
        foreach (var l in b.Levels)
        {
            var y = S(0, l.AltitudeMm).Y;
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(90, 140, 220)), 1, DashStyle.Dash), new(_ox - 10, y), new(Bounds.Width, y));
            ctx.DrawText(new FormattedText($"{l.Name}  {l.AltitudeMm / 1000.0:0.00} m", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, txt), new Point(Bounds.Width - 130, y - 14));
        }
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(90, 140, 220)), 1, DashStyle.Dash), new(_ox - 10, S(0, top).Y), new(Bounds.Width, S(0, top).Y));
        // pipes in the plane
        foreach (var (seg, u0, u1) in plane.Visible(b).Where(v => v.Seg.Network.Visible))
        {
            var c = NetworkQueries.ColorOf(seg.Network.Type); var w = Math.Max(2.5, seg.Segment.DiameterMm * _scale);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), w, lineCap: PenLineCap.Round), S(u0, seg.A.Z), S(u1, seg.B.Z));
        }
        // rubber band
        if (_down is { } d && _now is { } n) ctx.DrawLine(new Pen(Brushes.OrangeRed, 2, DashStyle.Dash), Vm.IsRiserTool ? new Point(d.X, S(0, _z0).Y) : S(ToU(d.X), _z0), Vm.IsRiserTool ? new Point(d.X, n.Y) : new Point(n.X, S(0, _z0).Y));
        ctx.DrawText(new FormattedText($"Élévation — {plane.Length / 1000.0:0.00} m", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, txt), new Point(12, 8));
    }
}
