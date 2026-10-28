using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ImmoCad.Core.Geometry;
using ImmoCad.Core.Model;
using ImmoCad.Core.Networks;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>Front view of one wall face: level lines with altitudes, pipes in the plane. Riser tool = vertical pipe (click, or drag to set top/bottom, snapping to level lines); Pipe tool = horizontal pipe along the face.</summary>
public class ElevationView : Control
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private double _scale = 0.1; private double _ox = 40, _oy;               // px per mm; screen origin of (u=0, z=zMin)
    private bool _fit = true;
    private Point? _down, _now; private double _z0;
    private (Guid NetworkId, Guid NodeId, double U)? _endDrag;

    private (Network Net, Segment Seg, double U, bool Top, Guid NodeId)? HitVerticalHandle(Point p)
    {
        if (Vm?.SelectedSegment is not { } sel || Vm.Plane is not { } plane) return null;
        var net = Vm.Building.Networks.FirstOrDefault(n => n.Id == sel.NetworkId); var seg = net?.Segments.FirstOrDefault(s => s.Id == sel.SegmentId); if (net == null || seg == null) return null;
        var a = net.Nodes.First(n => n.Id == seg.A); var b = net.Nodes.First(n => n.Id == seg.B); if (a.Position.X != b.Position.X || a.Position.Y != b.Position.Y) return null;
        var u = plane.ProjectU(new PointMm(a.Position.X, a.Position.Y)); var topNode = a.Position.Z >= b.Position.Z ? a : b; var botNode = topNode == a ? b : a;
        foreach (var (node, top) in new[] { (topNode, true), (botNode, false) }) { var sp = S(u, node.Position.Z); if (Math.Abs(sp.X - p.X) <= 10 && Math.Abs(sp.Y - p.Y) <= 10) return (net, seg, u, top, node.Id); }
        return null;
    }

    private (Network Net, Segment Seg)? HitPipe(Point p)
    {
        if (Vm?.Plane is not { } plane) return null;
        foreach (var (seg, u0, u1) in plane.Visible(Vm.Building).Where(v => v.Seg.Network.Visible && !v.Seg.Network.Locked))
        {
            Point a = S(u0, seg.A.Z), b = S(u1, seg.B.Z); double dx = b.X - a.X, dy = b.Y - a.Y; var l2 = dx * dx + dy * dy; var t = l2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
            if (Math.Sqrt(Math.Pow(p.X - (a.X + t * dx), 2) + Math.Pow(p.Y - (a.Y + t * dy), 2)) <= 6) return (seg.Network, seg.Segment);
        }
        return null;
    }

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
        var pt = e.GetPosition(this);
        if (HitVerticalHandle(pt) is { } hh) { _endDrag = (hh.Net.Id, hh.NodeId, hh.U); _down = _now = pt; e.Pointer.Capture(this); return; }
        if (!Vm.IsRiserTool && !Vm.IsPipeTool && HitPipe(pt) is { } hp) { Vm.SelectSegment(hp.Net.Id, hp.Seg.Id); InvalidateVisual(); return; }
        _down = _now = e.GetPosition(this); _z0 = ElevationPlane.SnapZ(Vm.Building, (int)ToZ(_down.Value.Y), (int)(12 / _scale)); e.Pointer.Capture(this);
    }
    protected override void OnPointerMoved(PointerEventArgs e) { if (_down != null) { _now = e.GetPosition(this); InvalidateVisual(); } }
    internal void ReleaseForTest(Point p) { if (_endDrag is { } ed) { _endDrag = null; _down = null; Vm?.DragVerticalEnd(ed.NetworkId, ed.NodeId, (int)ToZ(p.Y)); InvalidateVisual(); } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_endDrag != null) { ReleaseForTest(e.GetPosition(this)); return; }
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
        ctx.FillRectangle(Tokens.Brush(this, "PlanBackground", dark ? Color.FromRgb(30, 32, 36) : Color.FromRgb(250, 250, 248)), new Rect(Bounds.Size));
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
            ctx.DrawText(new FormattedText($"{l.DisplayName}  {l.AltitudeMm / 1000.0:0.00} m", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, txt), new Point(Bounds.Width - 130, y - 14));
        }
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(90, 140, 220)), 1, DashStyle.Dash), new(_ox - 10, S(0, top).Y), new(Bounds.Width, S(0, top).Y));
        // pipes in the plane
        foreach (var (seg, u0, u1) in plane.Visible(b).Where(v => v.Seg.Network.Visible))
        {
            var c = NetworkQueries.ColorOf(seg.Network.Type); var w = Math.Max(2.5, seg.Segment.DiameterMm * _scale);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), w, lineCap: PenLineCap.Round), S(u0, seg.A.Z), S(u1, seg.B.Z));
        }
        if (Vm.SelectedSegment is { } sg && Vm.Building.Networks.FirstOrDefault(n => n.Id == sg.NetworkId) is { } sn && sn.Segments.FirstOrDefault(x => x.Id == sg.SegmentId) is { } ss)
        {   // selected pipe: dashed highlight, round handles on the ends of vertical runs
            var na = sn.Nodes.First(n => n.Id == ss.A).Position; var nb = sn.Nodes.First(n => n.Id == ss.B).Position;
            var ua = plane.ProjectU(new PointMm(na.X, na.Y)); var ub = plane.ProjectU(new PointMm(nb.X, nb.Y));
            ctx.DrawLine(new Pen(Brushes.OrangeRed, 3, DashStyle.Dash), S(ua, na.Z), S(ub, nb.Z));
            if (na.X == nb.X && na.Y == nb.Y) foreach (var z in new[] { na.Z, nb.Z }) ctx.DrawEllipse(Brushes.White, new Pen(Brushes.OrangeRed, 2), S(ua, z), 6, 6);
        }
        // rubber band
        if (_endDrag != null && _now is { } en) ctx.DrawLine(new Pen(Brushes.DodgerBlue, 2, DashStyle.Dash), new Point(S(_endDrag.Value.U, 0).X, en.Y), new Point(S(_endDrag.Value.U, 0).X + 30, en.Y));
        else if (_down is { } d && _now is { } n) ctx.DrawLine(new Pen(Brushes.OrangeRed, 2, DashStyle.Dash), Vm.IsRiserTool ? new Point(d.X, S(0, _z0).Y) : S(ToU(d.X), _z0), Vm.IsRiserTool ? new Point(d.X, n.Y) : new Point(n.X, S(0, _z0).Y));
        ctx.DrawText(new FormattedText(ImmoCad.Core.Translations.T("elev.title", (plane.Length / 1000.0).ToString("0.00")), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, txt), new Point(12, 8));
    }
}
