using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ImmoCad.Core.Model;
using ImmoCad.Core.Networks;
using ImmoCad.Core.Walk;
using ImmoCad.Scene;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>Everything drawn over the first-person view: where you are, the ghost and leave buttons, the map, the colours of the networks, the first-use hint and the label of what is under the cursor.</summary>
public sealed class WalkOverlay : Grid
{
    private readonly MainViewModel _vm; private readonly Action _moved;
    private readonly TextBlock _where = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _ghost = new(); private readonly Button _leave = new();
    private readonly MiniMapView _map; private readonly Button _mapToggle = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _legend = new() { Spacing = 3 }; private readonly Border _legendBox = new();
    private readonly Border _hint = new(); private readonly TextBlock _hintText = new() { TextWrapping = TextWrapping.Wrap }; private readonly TextBlock _hintKeys = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 };
    private readonly Button _hintOk = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _hover = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly TextBlock _hoverText = new();
    private bool _mapShown = true;

    public WalkOverlay(MainViewModel vm, Action moved)
    {
        _vm = vm; _moved = moved; IsVisible = false; ZIndex = 11; Margin = new Thickness(10);
        _map = new MiniMapView(vm, moved) { Width = 220, Height = 170, HorizontalAlignment = HorizontalAlignment.Right };

        var top = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8) };
        top.Classes.Add("overlay");
        _ghost.Click += (_, _) => _vm.ToggleWalkGhostCommand.Execute(null); _leave.Click += (_, _) => _vm.LeaveWalkCommand.Execute(null);
        top.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _where, _ghost, _leave } };
        Children.Add(top);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Spacing = 4, Children = { _mapToggle, _map } };
        _mapToggle.Click += (_, _) => { _mapShown = !_mapShown; Refresh(); };
        Children.Add(right);

        _legendBox.Classes.Add("overlay"); _legendBox.Padding = new Thickness(10, 6); _legendBox.CornerRadius = new CornerRadius(8); _legendBox.HorizontalAlignment = HorizontalAlignment.Left; _legendBox.VerticalAlignment = VerticalAlignment.Bottom; _legendBox.Child = _legend;
        Children.Add(_legendBox);

        _hint.Classes.Add("overlay"); _hint.Padding = new Thickness(14, 10); _hint.CornerRadius = new CornerRadius(8); _hint.HorizontalAlignment = HorizontalAlignment.Center; _hint.VerticalAlignment = VerticalAlignment.Bottom; _hint.MaxWidth = 520; _hint.Margin = new Thickness(0, 0, 0, 24);
        _hintOk.Click += (_, _) => { _vm.Settings.WalkHintShown = true; Refresh(); };
        _hint.Child = new StackPanel { Spacing = 6, Children = { _hintText, _hintKeys, _hintOk } };
        Children.Add(_hint);

        _hover.Classes.Add("overlay"); _hover.Padding = new Thickness(8, 4); _hover.CornerRadius = new CornerRadius(6); _hover.Child = _hoverText; Children.Add(_hover);

        vm.WalkChanged += Refresh; vm.Changed += Refresh; vm.LanguageRefreshed += Refresh;
        Refresh();
    }

    /// <summary>The label of the object under the cursor, next to the cursor.</summary>
    public void ShowHover(string? text, Point at)
    {
        if (string.IsNullOrEmpty(text)) { _hover.IsVisible = false; return; }
        _hoverText.Text = text; _hover.Margin = new Thickness(Math.Max(0, at.X + 16), Math.Max(0, at.Y + 20), 0, 0); _hover.IsVisible = true;
    }

    public void Refresh()
    {
        _ghost.Content = T("walk.ghost"); ToolTip.SetTip(_ghost, T("walk.ghost.tip")); _ghost.IsChecked = _vm.Walk?.Ghost == true;
        _leave.Content = T("walk.leave"); ToolTip.SetTip(_leave, T("walk.leave.tip"));
        _where.Text = _vm.WalkHud();
        _mapToggle.Content = T(_mapShown ? "walk.minimap.hide" : "walk.minimap.show"); _map.IsVisible = _mapShown; _map.InvalidateVisual();
        ToolTip.SetTip(_map, T("walk.minimap.tip"));
        var first = !_vm.Settings.WalkHintShown; _hint.IsVisible = first; _hintText.Text = T("walk.hint"); _hintKeys.Text = T("walk.hint.keys"); _hintOk.Content = T("walk.dismiss");
        _legend.Children.Clear();
        foreach (var type in _vm.Building.Networks.Where(n => n.Visible).Select(n => n.Type).Distinct())          // one line per kind of network, not per riser
        {
            var c = NetworkQueries.ColorOf(type);
            _legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = Enum_(type), FontSize = 12 } } });
        }
        _legendBox.IsVisible = _legend.Children.Count > 0;
    }
}

/// <summary>
/// "You are here": the plan of the current level, same orientation as the plan view, with the walker and the view cone. Click to go there.
/// A light control drawn with a few geometries, not a PlanView.
/// </summary>
public sealed class MiniMapView : Control
{
    private readonly MainViewModel _vm; private readonly Action _moved;
    private static readonly IBrush Back = new SolidColorBrush(Color.FromArgb(215, 28, 30, 36)), WallFill = new SolidColorBrush(Color.FromRgb(196, 196, 190)), RoomFill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    private static readonly IBrush CorridorFill = new SolidColorBrush(Color.FromArgb(70, 120, 190, 255)), Cone = new SolidColorBrush(Color.FromArgb(90, 255, 200, 60)), Me = new SolidColorBrush(Color.FromRgb(255, 190, 40));

    public MiniMapView(MainViewModel vm, Action moved) { _vm = vm; _moved = moved; ClipToBounds = true; }

    /// <summary>Plan millimetres ↔ control pixels for the current level (Y up on the plan, down on screen).</summary>
    public (double Scale, double OX, double OY, double MinY, double MaxY)? Transform()
    {
        if (_vm.Walk is not { } w) return null;
        var pts = w.Level.Walls.SelectMany(x => x.Footprint).ToList(); double x0 = w.X, y0 = w.Y, x1 = w.X, y1 = w.Y;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        double bw = Math.Max(x1 - x0, 1000), bh = Math.Max(y1 - y0, 1000); const double pad = 8;
        var scale = Math.Min((Bounds.Width - 2 * pad) / bw, (Bounds.Height - 2 * pad) / bh);
        return (scale, pad + (Bounds.Width - 2 * pad - bw * scale) / 2 - x0 * scale, pad + (Bounds.Height - 2 * pad - bh * scale) / 2, y0, y1);
    }

    private Point ToScreen(double x, double y, (double Scale, double OX, double OY, double MinY, double MaxY) t) => new(t.OX + x * t.Scale, t.OY + (t.MaxY - y) * t.Scale);
    public PointMm? ToMm(Point p)
    {
        if (Transform() is not { } t) return null;
        return new PointMm((int)Math.Round((p.X - t.OX) / t.Scale), (int)Math.Round(t.MaxY - (p.Y - t.OY) / t.Scale));
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_vm.Walk is { } w && ToMm(e.GetPosition(this)) is { } mm) { w.Jump(new WalkPoint(mm.X, mm.Y)); _moved(); e.Handled = true; }
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Back, new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1), new Rect(Bounds.Size), 8, 8);
        if (_vm.Walk is not { } w || Transform() is not { } t) return;
        StreamGeometry Poly(IEnumerable<PointMm> pts) { var g = new StreamGeometry(); using var c = g.Open(); var first = true; foreach (var p in pts) { var s = ToScreen(p.X, p.Y, t); if (first) { c.BeginFigure(s, true); first = false; } else c.LineTo(s); } if (!first) c.EndFigure(true); return g; }
        foreach (var sp in w.Level.Spaces.Where(s => s.Outline.Count >= 3)) ctx.DrawGeometry(sp.Kind == SpaceKind.Corridor ? CorridorFill : RoomFill, null, Poly(sp.Outline));
        foreach (var wall in w.Level.Walls.Where(x => x.Footprint.Count >= 3))
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.SetFillRule(FillRule.EvenOdd);
                void Ring(IReadOnlyList<PointMm> ring) { c.BeginFigure(ToScreen(ring[0].X, ring[0].Y, t), true); for (var i = 1; i < ring.Count; i++) c.LineTo(ToScreen(ring[i].X, ring[i].Y, t)); c.EndFigure(true); }
                Ring(wall.Footprint); foreach (var h in wall.Holes.Where(h => h.Count >= 3)) Ring(h);
            }
            ctx.DrawGeometry(WallFill, null, g);
        }
        var band0 = w.Level.AltitudeMm - SceneBuilder.WalkBandMarginMm; var band1 = w.Level.AltitudeMm + w.Level.HeightMm + SceneBuilder.WalkBandMarginMm;
        foreach (var n in _vm.Building.Networks.Where(n => n.Visible))
        {
            var c = NetworkQueries.ColorOf(n.Type); var pen = new Pen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), 1.6);
            foreach (var s in n.Segments)
            {
                var a = n.Nodes.FirstOrDefault(x => x.Id == s.A)?.Position; var b = n.Nodes.FirstOrDefault(x => x.Id == s.B)?.Position; if (a == null || b == null) continue;
                if (Math.Max(a.Value.Z, b.Value.Z) < band0 || Math.Min(a.Value.Z, b.Value.Z) > band1) continue;
                if (a.Value.X == b.Value.X && a.Value.Y == b.Value.Y) ctx.DrawEllipse(pen.Brush, null, ToScreen(a.Value.X, a.Value.Y, t), 2.2, 2.2); else ctx.DrawLine(pen, ToScreen(a.Value.X, a.Value.Y, t), ToScreen(b.Value.X, b.Value.Y, t));
            }
        }
        if (_vm.Building.WorldFrame is { } f)
        {
            var dash = new Pen(Brushes.LightSkyBlue, 1, DashStyle.Dash); ctx.DrawRectangle(null, dash, new Rect(ToScreen(f.X0, f.Y1, t), ToScreen(f.X1, f.Y0, t)));
        }
        var me = ToScreen(w.X, w.Y, t); var fwd = w.Forward; var ang = Math.Atan2(-fwd.Y, fwd.X);          // screen Y points down
        var half = Math.PI / 5; var cone = new StreamGeometry(); using (var cc = cone.Open())
        {
            cc.BeginFigure(me, true);
            for (var k = -4; k <= 4; k++) { var a = ang + half * k / 4; cc.LineTo(new Point(me.X + Math.Cos(a) * 34, me.Y + Math.Sin(a) * 34)); }
            cc.EndFigure(true);
        }
        ctx.DrawGeometry(Cone, null, cone); ctx.DrawEllipse(Me, new Pen(Brushes.Black, 1), me, 4.5, 4.5);
    }
}
