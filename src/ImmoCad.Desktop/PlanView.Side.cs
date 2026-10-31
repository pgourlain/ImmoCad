using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using ImmoCad.Core.Catalog;
using ImmoCad.Core.Geometry;
using ImmoCad.Core.Model;
using ImmoCad.Core.Networks;
using ImmoCad.Core.Tools;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>The plan canvas on the XZ / YZ planes: same zoom, pan, grid, snap, selection and pipe tools; the canvas vertical axis is Z (always up) and the horizontal one is u (X or Y).</summary>
public partial class PlanView
{
    private bool Side => Vm?.IsSidePlane == true;
    private (ViewPlane, bool, bool, Guid) _sideKey;

    private static PointMm UV(double u, double z) => new((int)Math.Round(u), (int)Math.Round(z));

    // ---- changing the view plane (view cube, menu) keeps the zoom and turns the view about a pivot ----
    private (ViewPlane, bool)? _planeSeen; private ElevationPlane? _planeModelSeen; private (double X, double Y) _centreXY; private double _centreZ;

    /// <summary>
    /// When the view plane changes, the zoom stays and the building turns like a model on an axis. The axis goes through the middle of the building (footprints × all levels) while that point is on screen:
    /// it stays at the same pixel and the rest turns about it. When it is off screen (zoomed on a corner) the axis goes through the middle of the canvas instead, so what you look at stays in view.
    /// </summary>
    private void KeepViewAcrossPlanes()
    {
        if (Vm is not { } vm || Bounds.Width < 10 || Bounds.Height < 10 || vm.CurrentLevel is not { } cur) return;
        var now = (vm.ViewPlane, vm.PlaneFlip);
        if (_planeSeen is { } was && was != now && _fitted)
        {
            ReadCentre(was.Item1, _planeModelSeen, ToMm(new Point(Bounds.Width / 2, Bounds.Height / 2)), cur);                      // still expressed in the old plane
            var newModel = now.Item1 == ViewPlane.XY ? null : vm.SidePlaneModel(); var oldModel = _planeModelSeen;
            (double X, double Y, double Z) pivot = (_centreXY.X, _centreXY.Y, _centreZ); var onScreen = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var pts = vm.Building.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).ToList();
            if (pts.Count > 0)
            {
                var mx = (pts.Min(q => q.X) + pts.Max(q => q.X)) / 2.0; var my = (pts.Min(q => q.Y) + pts.Max(q => q.Y)) / 2.0;
                var mz = (vm.Building.Levels.Min(l => l.AltitudeMm) + vm.Building.Levels.Max(l => l.AltitudeMm + l.HeightMm)) / 2.0;
                PointMm? oldAt = was.Item1 == ViewPlane.XY ? new PointMm((int)Math.Round(mx), (int)Math.Round(my))
                    : oldModel is { } om ? new PointMm((int)Math.Round(om.ProjectU(new((int)Math.Round(mx), (int)Math.Round(my)))), (int)Math.Round(mz)) : null;
                if (oldAt is { } oa && new Rect(Bounds.Size).Deflate(24).Contains(ToScreen(oa))) { pivot = (mx, my, mz); onScreen = ToScreen(oa); }
            }
            var at = now.Item1 == ViewPlane.XY ? new PointMm((int)Math.Round(pivot.X), (int)Math.Round(pivot.Y))
                : new PointMm((int)Math.Round(newModel!.ProjectU(new((int)Math.Round(pivot.X), (int)Math.Round(pivot.Y)))), (int)Math.Round(pivot.Z));
            var u = Rot(onScreen, -ViewRotationRad); Offset = (u.X - at.X * Scale, Bounds.Height - u.Y - at.Y * Scale);                // the pivot keeps its pixel
            _sideKey = (vm.ViewPlane, vm.PlaneFlip, vm.ElevationLevelOnly, cur.Id);                                                 // no automatic fit for this change
            ReadCentre(now.Item1, newModel, ToMm(new Point(Bounds.Width / 2, Bounds.Height / 2)), cur, keepZFromTop: true);
        }
        _planeSeen = now; _planeModelSeen = now.Item1 == ViewPlane.XY ? null : vm.SidePlaneModel();
    }

    /// <summary>Remembers the world point under the middle of the canvas (<paramref name="c"/> is that point in the given plane's coordinates).</summary>
    private void ReadCentre(ViewPlane plane, ElevationPlane? model, PointMm c, Level cur, bool keepZFromTop = false)
    {
        if (plane == ViewPlane.XY) { _centreXY = (c.X, c.Y); if (!keepZFromTop) _centreZ = cur.AltitudeMm; }
        else if (model is { } m)
        {
            var u0 = m.ProjectU(new PointMm((int)Math.Round(_centreXY.X), (int)Math.Round(_centreXY.Y))); var d = c.X - u0;
            _centreXY = (_centreXY.X + d * (m.B.X - m.A.X) / m.Length, _centreXY.Y + d * (m.B.Y - m.A.Y) / m.Length); _centreZ = c.Y;
        }
    }

    private void FitSide()
    {
        if (Vm is not { } vm || Bounds.Width < 10 || vm.Building.Levels.Count == 0) return;
        var plane = vm.SidePlaneModel(); var (bottom, top) = vm.ElevationRange();
        double u0 = 0, u1 = plane.Length; Scale = Math.Min((Bounds.Width - 80) / Math.Max(1, u1 - u0), (Bounds.Height - 80) / Math.Max(1, top - bottom));
        Offset = (40 - u0 * Scale, 40 - bottom * Scale); _fitted = true;
    }

    private Rect WallRect(ElevationPlane plane, Level l, Wall w)
    {
        var us = w.Footprint.Select(q => plane.ProjectU(q)).ToList(); var h = w.HeightMm ?? l.HeightMm;
        return new Rect(ToScreen(UV(us.Min(), l.AltitudeMm + h)), ToScreen(UV(us.Max(), l.AltitudeMm)));
    }
    private static Rect Norm(Rect r) => new(Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom), Math.Abs(r.Width), Math.Abs(r.Height));

    private Rect EquipmentRect(ElevationPlane plane, Level l, Equipment e)
    {
        var cat = EquipmentCatalog.Find(e.CatalogId)?.With(e.Parameters); var u = plane.ProjectU(e.Position); double w = cat?.WidthMm ?? 300, h = cat?.HeightMm ?? 300; var z = l.AltitudeMm + e.ElevationMm;
        return Norm(new Rect(ToScreen(UV(u - w / 2, z + h)), ToScreen(UV(u + w / 2, z))));
    }

    private void RenderSide(DrawingContext ctx, bool dark, Level cur)
    {
        var vm = Vm!; var b = vm.Building; var plane = vm.SidePlaneModel(); var shown = vm.ElevationLevels().ToList();
        var key = (vm.ViewPlane, vm.PlaneFlip, vm.ElevationLevelOnly, cur.Id); if (_sideKey != key) { _sideKey = key; FitSide(); }
        var txt = dark ? Brushes.Gainsboro : Brushes.DimGray; const double far = 1_000_000;
        // level lines (floors and ceilings of the shown levels)
        foreach (var l in shown)
        {
            var lp = new Pen(new SolidColorBrush(Color.FromRgb(90, 140, 220)), 1, DashStyle.Dash);
            foreach (var z in new[] { l.AltitudeMm, l.AltitudeMm + l.HeightMm }) ctx.DrawLine(lp, ToScreen(UV(-far, z)), ToScreen(UV(far, z)));
            ctx.DrawText(Txt($"{l.DisplayName}  {l.AltitudeMm / 1000.0:0.00} m", 11, txt), ToScreen(UV(0, l.AltitudeMm)) + new Point(8, -16));
        }
        // walls and their openings
        foreach (var l in shown)
            foreach (var w in l.Walls)
            {
                if (vm.CommonOnly && w.Stability == Stability.Private || w.Footprint.Count < 2 || vm.WallDisplay == WallDisplay.Hidden) continue;
                var sel = vm.SelectedWalls.Contains(w.Id); var r = Norm(WallRect(plane, l, w));
                ctx.DrawRectangle(vm.WallDisplay == WallDisplay.Wireframe ? Brushes.Transparent : new SolidColorBrush(sel ? Color.FromArgb(120, 255, 140, 0) : Color.FromArgb(70, 120, 120, 135)), new Pen(sel ? Brushes.OrangeRed : Brushes.Gray, sel ? 2 : 1), r);
                if (WallGeometry.Axis(w) is not { } ax) continue;
                foreach (var o in w.Openings)
                {
                    var u0 = plane.ProjectU(ax.PointAt(o.PositionMm - o.WidthMm / 2.0)); var u1 = plane.ProjectU(ax.PointAt(o.PositionMm + o.WidthMm / 2.0));
                    var or = Norm(new Rect(ToScreen(UV(Math.Min(u0, u1), l.AltitudeMm + o.SillMm + o.HeightMm)), ToScreen(UV(Math.Max(u0, u1), l.AltitudeMm + o.SillMm))));
                    var picked = vm.SelectedOpening?.OpeningId == o.Id;
                    ctx.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(30, 32, 36) : Color.FromRgb(250, 250, 248)), new Pen(picked ? Brushes.OrangeRed : Brushes.SteelBlue, picked ? 2 : 1.2), or);
                }
            }
        // equipment
        foreach (var e in b.Equipment.Where(q => shown.Any(l => l.Id == q.LevelId)))
        {
            var l = b.Levels.First(x => x.Id == e.LevelId); var sel = vm.SelectedEquipmentId == e.Id;
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 106, 90, 205)), new Pen(sel ? Brushes.OrangeRed : Brushes.SlateBlue, sel ? 2.2 : 1.4), EquipmentRect(plane, l, e));
        }
        // pipes (every network, projected on the plane)
        DrawJoints(ctx, NetworkQueries.All(b).Where(x => x.Network.Visible), q => ToScreen(UV(plane.ProjectU(new PointMm(q.X, q.Y)), q.Z)));
        // painter's algorithm: farthest from the viewer first, the nearest tubes drawn last cover the others
        foreach (var s in NetworkQueries.All(b).Where(x => x.Network.Visible).OrderBy(x => plane.Distance(new PointMm((x.A.X + x.B.X) / 2, (x.A.Y + x.B.Y) / 2))))
        {
            var c = NetworkQueries.ColorOf(s.Network.Type); var wpx = TubeWidthPx(s.Segment.DiameterMm);
            var a = ToScreen(UV(plane.ProjectU(new PointMm(s.A.X, s.A.Y)), s.A.Z)); var d = ToScreen(UV(plane.ProjectU(new PointMm(s.B.X, s.B.Y)), s.B.Z));
            if (vm.SelectedSegment?.SegmentId == s.Segment.Id) ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 140, 0)), wpx + 5, lineCap: PenLineCap.Flat), a, d);
            DrawTube(ctx, a, d, s.Segment.DiameterMm, Color.FromRgb(c.R, c.G, c.B), dark);
        }
        DrawSideHandles(ctx, plane); DrawMeasure(ctx);
        // pipe being drawn
        if (DrawTool is { Points.Count: > 0 } t)
        {
            var pen = new Pen(Brushes.OrangeRed, 2);
            for (var i = 1; i < t.Points.Count; i++) ctx.DrawLine(pen, ToScreen(t.Points[i - 1]), ToScreen(t.Points[i]));
            if (t.Preview is { } pv) ctx.DrawLine(new Pen(Brushes.OrangeRed, 1, DashStyle.Dash), ToScreen(t.Points[^1]), ToScreen(pv));
        }
        ctx.DrawText(Txt($"{T("enum.ViewPlane." + vm.ViewPlane)} — {T("elev.side", T("enum.ViewSide." + vm.PlaneSide), vm.ElevationDepth)}", 13, txt), new Point(12, 8));
        DrawScaleBar(ctx, dark);
        DrawToolCursor(ctx, dark);
    }

    // ---- Handles on the selected pipe (same gestures as in the plan): ends extend / elbow, middle makes a tee, the body moves the run ----
    private enum SideAxis { None, AlongU, Vertical, Oblique }
    private bool _sideElbow;
    private (SegmentRef Seg, PipeHandle Handle, PointMm Start)? _sideDrag; private PointMm _sideNow;

    private SideAxis AxisOf(ElevationPlane plane, SegmentRef s)
    {
        var a = new PointMm(s.A.X, s.A.Y); var b = new PointMm(s.B.X, s.B.Y); double du = plane.ProjectU(b) - plane.ProjectU(a), dd = plane.Distance(b) - plane.Distance(a); int dz = s.B.Z - s.A.Z;
        if (dz == 0 && Math.Abs(du) >= 1 && Math.Abs(dd) < 1) return SideAxis.AlongU;
        if (Math.Abs(du) < 1 && Math.Abs(dd) < 1 && dz != 0) return SideAxis.Vertical;
        if (Math.Abs(dd) < 1 && Math.Abs(du) >= 1 && dz != 0) return SideAxis.Oblique;
        return SideAxis.None;
    }
    private Point ScreenOf(ElevationPlane plane, PointMm3 q) => ToScreen(UV(plane.ProjectU(new PointMm(q.X, q.Y)), q.Z));
    private SegmentRef? SelectedSideSegment() => Vm?.SelectedSegment is { } sel ? NetworkQueries.All(Vm.Building).FirstOrDefault(x => x.Segment.Id == sel.SegmentId) : null;
    private PointMm SnapUV(Point p) { SyncSnap(); var m = ToMm(p); var g = Snapper.GridMm; return new PointMm(Snap(m.X, g), Snap(m.Y, g)); }

    private (SegmentRef Seg, PipeHandle Handle)? HitSideHandle(Point p, ElevationPlane plane)
    {
        if (SelectedSideSegment() is not { } s || s.Network.Locked || AxisOf(plane, s) == SideAxis.None) return null;
        var a = ScreenOf(plane, s.A); var b = ScreenOf(plane, s.B); var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        double D(Point q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        if (D(a) <= 9) return (s, PipeHandle.EndA); if (D(b) <= 9) return (s, PipeHandle.EndB); if (D(mid) <= 9) return (s, PipeHandle.Mid);
        return null;
    }

    /// <summary>What releasing the pointer would do, as a line from the handle to its target (for the overlay), and the action to run.</summary>
    private (PointMm3 From, PointMm3 To, Func<bool> Do)? SideProposal(ElevationPlane plane, SegmentRef s, PipeHandle h, PointMm start, PointMm cur)
    {
        var vm = Vm!; var axis = AxisOf(plane, s); if (axis == SideAxis.None) return null; const int elbow = 200;
        PointMm3 AtU(PointMm3 from, double uTo) { var d1 = plane.ToPlan(uTo); var d0 = plane.ToPlan(plane.ProjectU(new PointMm(from.X, from.Y))); return new(from.X + d1.X - d0.X, from.Y + d1.Y - d0.Y, from.Z); }
        double U(PointMm3 q) => plane.ProjectU(new PointMm(q.X, q.Y));
        var act = vm.Actions;
        switch (h)
        {
            case PipeHandle.EndA or PipeHandle.EndB:
            {
                var endId = h == PipeHandle.EndA ? s.Segment.A : s.Segment.B; var end = h == PipeHandle.EndA ? s.A : s.B;
                var tol = Snapper.GridMm > 0 ? Snapper.GridMm / 2.0 : 30;
                var onAxis = axis == SideAxis.AlongU ? Math.Abs(cur.Y - end.Z) <= tol : axis == SideAxis.Vertical && Math.Abs(cur.X - U(end)) <= tol;
                var elbowing = _sideElbow && axis != SideAxis.Oblique;                                // Ctrl/Alt: elbow along the other axis
                PointMm3 to;
                if (elbowing) to = axis == SideAxis.AlongU ? end with { Z = cur.Y } : AtU(end, cur.X);
                else if (onAxis) to = axis == SideAxis.AlongU ? AtU(end, cur.X) : end with { Z = cur.Y };   // on the axis: extend / shorten
                else to = AtU(end, cur.X) with { Z = cur.Y };                                              // anywhere else: the pipe turns oblique
                return to == end ? null : (end, to, () => elbowing ? act.AddElbow(s.Network.Id, endId, to) != null : onAxis ? act.ExtendEnd(s.Network.Id, endId, to) : act.MoveEnd(s.Network.Id, endId, to));
            }
            case PipeHandle.Mid:
            {
                var at = new PointMm3((s.A.X + s.B.X) / 2, (s.A.Y + s.B.Y) / 2, (s.A.Z + s.B.Z) / 2);
                var to = axis != SideAxis.Vertical ? at with { Z = cur.Y } : AtU(at, cur.X);
                return to == at ? null : (at, to, () => act.AddTeeBranch(s.Network.Id, s.Segment.Id, at, to) != null);
            }
            default:
            {
                int dz = axis != SideAxis.Vertical ? cur.Y - start.Y : 0; double du = axis != SideAxis.AlongU ? cur.X - start.X : 0;
                var d1 = plane.ToPlan(du); var d0 = plane.ToPlan(0); int dx = d1.X - d0.X, dy = d1.Y - d0.Y;
                if (dx == 0 && dy == 0 && dz == 0) return null;
                var mid = new PointMm3((s.A.X + s.B.X) / 2, (s.A.Y + s.B.Y) / 2, (s.A.Z + s.B.Z) / 2);
                return (mid, new PointMm3(mid.X + dx, mid.Y + dy, mid.Z + dz), () => act.MoveRun(s.Network.Id, s.Segment.Id, dx, dy, dz));
            }
        }
    }

    private void SideMoved(Point p, KeyModifiers mods) { _sideNow = SnapUV(p); _sideElbow = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt); InvalidateVisual(); }

    private void SideReleased(PointerReleasedEventArgs e)
    {
        var drag = _sideDrag!.Value; _sideDrag = null; var vm = Vm!; var cur = SnapUV(e.GetPosition(this)); _sideElbow = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (cur == drag.Start && drag.Handle == PipeHandle.Body) return;                                  // a plain click only selects
        var ok = SideProposal(vm.SidePlaneModel(), drag.Seg, drag.Handle, drag.Start, cur) is { } pr && pr.Do();
        vm.AfterEdit(ok ? T("pipe.modified") : T("nochange")); InvalidateVisual();
    }

    private void DrawSideHandles(DrawingContext ctx, ElevationPlane plane)
    {
        if (SelectedSideSegment() is not { } s || s.Network.Locked || AxisOf(plane, s) == SideAxis.None) return;
        var a = ScreenOf(plane, s.A); var b = ScreenOf(plane, s.B); var pen = new Pen(Brushes.OrangeRed, 2);
        foreach (var q in new[] { a, b }) ctx.DrawRectangle(Brushes.White, pen, new Rect(q.X - 5, q.Y - 5, 10, 10));
        ctx.DrawEllipse(Brushes.White, pen, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), 5, 5);
        if (_sideDrag is { } d && SideProposal(plane, d.Seg, d.Handle, d.Start, _sideNow) is { } pr)
            ctx.DrawLine(new Pen(Brushes.DodgerBlue, 2.2, DashStyle.Dash), ScreenOf(plane, pr.From), ScreenOf(plane, pr.To));
    }

    private void SidePressed(PointerPressedEventArgs e, Point p)
    {
        var vm = Vm!; var plane = vm.SidePlaneModel();
        if (_space) { _pan = p; e.Pointer.Capture(this); return; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) { _rotFrom = p; e.Pointer.Capture(this); return; }
        if (vm.IsPipeTool)
        {
            Invalidate(); _chainTyped = ""; _chainAngle = ""; _chainField = 0;
            if (e.ClickCount >= 2 && DrawTool!.Points.Count >= 2) { DrawTool.DoubleClick(); InvalidateVisual(); return; }
            DrawTool!.Down(TP(e)); e.Pointer.Capture(this); InvalidateVisual(); return;
        }
        if (vm.IsMeasureTool) { vm.MeasureClick(SnapUV(p)); InvalidateVisual(); return; }                         // distance in the plane of the view (along × height)
        if (vm.IsRiserTool) { vm.PlaceRiserSide(MarkerSnap(ToMm(p)).Point.X); return; }
        if (vm.IsPlaceTool) { vm.PlaceFromPaletteSide(ToMm(p)); return; }
        if (!vm.IsSelectTool) { vm.Status = T("side.unavailable"); return; }
        if (HitSideHandle(p, plane) is { } hh) { _sideDrag = (hh.Seg, hh.Handle, SnapUV(p)); _sideNow = _sideDrag.Value.Start; e.Pointer.Capture(this); return; }
        var add = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        foreach (var s in NetworkQueries.All(vm.Building).Where(x => x.Network.Visible && !x.Network.Locked))
        {
            var a = ToScreen(UV(plane.ProjectU(new PointMm(s.A.X, s.A.Y)), s.A.Z)); var d = ToScreen(UV(plane.ProjectU(new PointMm(s.B.X, s.B.Y)), s.B.Z));
            double dx = d.X - a.X, dy = d.Y - a.Y, l2 = dx * dx + dy * dy; var t = l2 < 1e-6 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
            if (Math.Sqrt(Math.Pow(p.X - (a.X + t * dx), 2) + Math.Pow(p.Y - (a.Y + t * dy), 2)) <= 7)
            {   // select, and a drag from here moves the run (a plain click only selects)
                vm.SelectSegment(s.Network.Id, s.Segment.Id);
                if (AxisOf(plane, s) != SideAxis.None && !s.Network.Locked) { _sideDrag = (s, PipeHandle.Body, SnapUV(p)); _sideNow = _sideDrag.Value.Start; e.Pointer.Capture(this); }
                InvalidateVisual(); return;
            }
        }
        if (vm.CurrentLevel is { } cl)
        {
            foreach (var eq in vm.Building.Equipment.Where(q => q.LevelId == cl.Id).Reverse())
                if (EquipmentRect(plane, cl, eq).Inflate(3).Contains(p)) { vm.SelectEquipment(eq.Id); InvalidateVisual(); return; }
            foreach (var w in cl.Walls.AsEnumerable().Reverse().Where(x => x.Footprint.Count >= 2 && vm.WallDisplay != WallDisplay.Hidden))
                if (Norm(WallRect(plane, cl, w)).Contains(p)) { vm.SelectWalls([w.Id], add); InvalidateVisual(); return; }
        }
        if (!add) { vm.SelectWalls([], false); vm.ClearOpening(); vm.ClearSpace(); vm.ClearSegment(); if (vm.SelectedEquipmentId != null) vm.SelectEquipment(null); }
        InvalidateVisual();
    }
}
