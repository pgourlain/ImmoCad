using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImmoCad.Core.Model;
using ImmoCad.Core.Networks;
using ImmoCad.Core.Catalog;
using ImmoCad.Core.Tools;
using ImmoCad.Core.Geometry;
using ImmoCad.Core.IO;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>
/// 2D plan. Select tool: click a wall / pipe / riser, Shift+click to add to the selection, Space+drag (or middle button) pans, Ctrl(⌘)+drag rotates the view, drag on empty space = box select, drag a selected wall = move
/// (one history step on release), arrows nudge. Draw tool: hybrid click/drag chain. Wheel zooms, middle button pans.
/// </summary>
public partial class PlanView : Control
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    public double Scale { get; set; } = 0.1;                    // px per mm
    public (double X, double Y) Offset { get; set; } = (50, 0); // screen px of the plan origin (Y flipped)
    public DrawChainTool? DrawTool { get; private set; }
    public Snapper Snapper { get; } = new();
    public event Action<PointMm>? CursorMoved;

    private Point? _pan;
    private Point? _rotFrom;
    private bool _space;       // Space held: drag pans
    /// <summary>View rotation chosen by the user (Ctrl/⌘+drag), added to the north-up rotation.</summary>
    public double UserRotationRad { get; set; }
    private bool _fitted;
    private enum Gesture { None, Box, Move }
    private List<Point>? _lasso;                  // the freehand outline being drawn by the merge-region tool
    private Gesture _gesture; private Point _start; private Point _now; private PointMm _startMm; private (int X, int Y) _delta;
    private bool _addToSelection;

    private readonly Dictionary<string, Bitmap> _bitmaps = new();
    private Bitmap? Background(Level l)
    {
        if (l.BackgroundImage is not { } n || Vm?.Images.TryGetValue(n, out var data) != true) return null;
        if (!_bitmaps.TryGetValue(n, out var bmp)) _bitmaps[n] = bmp = new Bitmap(new MemoryStream(data!));
        return bmp;
    }

    public PlanView() { Focusable = true; ClipToBounds = true; DataContextChanged += (_, _) => { if (Vm != null) { Vm.FocusRequested += CenterOn; Vm.ViewFitRequested += RefitNextRender; } };
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) => { ZoomAt(e.GetPosition(this), Math.Exp(e.Delta.X)); e.Handled = true; });   // trackpad pinch
    }

    private ToolKind? _chainKind; private bool _chainSide;
    private bool IsChain => Vm is { IsDrawTool: true } or { IsPipeTool: true };
    public void RefitNextRender() => _fitted = false;
    public void Invalidate()
    {
        if (Vm is { IsDrawTool: true }) { if (_chainKind != ToolKind.DrawWall) { DrawTool = new DrawChainTool(Vm.CommitWalls); _chainKind = ToolKind.DrawWall; } }
        else if (Vm is { IsPipeTool: true }) { if (_chainKind != ToolKind.DrawPipe || _chainSide != Vm.IsSidePlane) { DrawTool = new DrawChainTool(Vm.IsSidePlane ? Vm.CommitPipeSide : Vm.CommitPipe); _chainKind = ToolKind.DrawPipe; _chainSide = Vm.IsSidePlane; } }
        else { DrawTool = null; _chainKind = null; }
        InvalidateVisual();
    }
    private readonly List<(Rect Rect, Guid LevelId, PointMm Center)> _arrows = new();
    private bool _over;
    private PointMm _hover, _spaceFor; private List<PointMm>? _spaceOutline;
    private string _chainTyped = "", _chainAngle = ""; private int _chainField;     // typed length / angle while placing; Tab switches field
    private (Guid Id, PointMm To, PointMm From)? _eqDrag; private (Guid EqId, Port Port, PointMm3 At, PointMm To)? _connect;
    private (Wall Wall, Opening Opening)? _openingDrag;

    private void ShowChainMenu(DrawChainTool t)
    {
        var menu = new ContextMenu();
        MenuItem Item(string h, Action a, bool enabled = true) { var m = new MenuItem { Header = h, IsEnabled = enabled }; m.Click += (_, _) => { a(); InvalidateVisual(); }; return m; }
        menu.Items.Add(Item(T("ctx.finish"), () => t.Finish(), t.Points.Count >= 2));
        menu.Items.Add(Item(T("ctx.undolast"), () => t.Backspace(), t.Points.Count > 1));
        menu.Items.Add(Item(T("ctx.close"), () => t.Close(), t.Points.Count >= 3));
        menu.Items.Add(Item(T("btn.cancel"), () => t.Cancel()));
        menu.Open(this);
    }
    internal StretchHandle? StretchActiveForTest => _stretch;
    internal DrawChainTool? DrawToolForTest => DrawTool;
    private StretchHandle? _stretch; private Affine _stretchStart; private (double X, double Y) _stretchFrom; private Affine? _stretchLive;
    private PipeHandleTool? _handleTool;
    private string _typed = "";

    private Equipment? HitEquipment(PointMm p)
    {
        if (Vm?.CurrentLevel is not { } l) return null;
        foreach (var e in Vm.Building.Equipment.Where(q => q.LevelId == l.Id).Reverse())
        {
            if (EquipmentCatalog.Find(e.CatalogId)?.With(e.Parameters) is not { } c) continue;
            var pts = Symbol.Figures(c, e.Position, e.RotationDeg).Where(f => f.Points.Count > 2).SelectMany(f => f.Points).ToList(); if (pts.Count == 0) continue;
            int x0 = pts.Min(q => q.X) - 40, x1 = pts.Max(q => q.X) + 40, y0 = pts.Min(q => q.Y) - 40, y1 = pts.Max(q => q.Y) + 40;
            if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1) return e;
        }
        return null;
    }

    private (SegmentRef Seg, PipeHandle Handle)? HitHandle(Point p)
    {
        if (Vm?.SelectedSegment is not { } sel || Vm.CurrentLevel is not { } l) return null;
        var seg = NetworkQueries.HorizontalAt(Vm.Building, l).FirstOrDefault(s => s.Network.Id == sel.NetworkId && s.Segment.Id == sel.SegmentId);
        if (seg == null) return null;
        var a = ToScreen(new PointMm(seg.A.X, seg.A.Y)); var b = ToScreen(new PointMm(seg.B.X, seg.B.Y)); var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        double D(Point q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        if (D(a) <= 9) return (seg, PipeHandle.EndA); if (D(b) <= 9) return (seg, PipeHandle.EndB); if (D(mid) <= 9) return (seg, PipeHandle.Mid);
        return null;
    }

    private SegmentRef? HitPipe(Point p)
    {
        if (Vm?.CurrentLevel is not { } l) return null;
        foreach (var s in NetworkQueries.HorizontalAt(Vm.Building, l).Where(x => x.Network.Visible && !x.Network.Locked))
        {
            var a = ToScreen(new PointMm(s.A.X, s.A.Y)); var b = ToScreen(new PointMm(s.B.X, s.B.Y));
            double dx = b.X - a.X, dy = b.Y - a.Y; var l2 = dx * dx + dy * dy; if (l2 < 1e-6) continue;
            var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1); var q = new Point(a.X + t * dx, a.Y + t * dy);
            if (Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)) <= 6) return s;
        }
        return null;
    }

    private SegmentRef? HitRiser(Point p)
    {
        if (Vm?.CurrentLevel is not { } l) return null;
        foreach (var r in NetworkQueries.RisersAt(Vm.Building, l).Where(x => x.Network.Visible && !x.Network.Locked))
        {
            var c = ToScreen(new PointMm(r.A.X, r.A.Y)); var rad = RiserRadiusPx(r.Segment.DiameterMm) + 3;
            if ((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y) <= rad * rad) return r;
        }
        return null;
    }

    private ToolPointer HandlePointer(PointerEventArgs e) { var p = e.GetPosition(this); return new ToolPointer(ToMm(p), p.X, p.Y); }

    /// <summary>"North up": the whole view is turned about the viewport centre so that true north points up (needs a placed building).</summary>
    public double ViewRotationRad => (Vm is { NorthUp: true, Building.Site: { } s } ? s.RotationDeg * Math.PI / 180 : 0) + UserRotationRad;
    private Point Rot(Point p, double angle)
    {
        if (angle == 0) return p;
        double cx = Bounds.Width / 2, cy = Bounds.Height / 2, c = Math.Cos(angle), s = Math.Sin(angle), dx = p.X - cx, dy = p.Y - cy;
        return new(cx + dx * c - dy * s, cy + dx * s + dy * c);
    }
    private Matrix RotationMatrix() => ViewRotationRad == 0 ? Matrix.Identity : Matrix.CreateTranslation(-Bounds.Width / 2, -Bounds.Height / 2) * Matrix.CreateRotation(ViewRotationRad) * Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2);
    public PointMm ToMm(Point p) { var u = Rot(p, -ViewRotationRad); return new((int)Math.Round((u.X - Offset.X) / Scale), (int)Math.Round((Bounds.Height - u.Y - Offset.Y) / Scale)); }
    internal Point ToScreenForTest(PointMm p) => ToScreen(p);
    private Point ToScreen(PointMm p) => Rot(new(Offset.X + p.X * Scale, Bounds.Height - Offset.Y - p.Y * Scale), ViewRotationRad);

    /// <summary>Moves the drawing by a screen vector (accounts for the view rotation).</summary>
    private void PanBy(double dx, double dy)
    {
        var a = ViewRotationRad; double c = Math.Cos(a), s = Math.Sin(a);
        double ux = dx * c + dy * s, uy = -dx * s + dy * c;
        Offset = (Offset.X + ux, Offset.Y - uy);
    }

    /// <summary>Zooms about the point p (screen) keeping the drawing under it fixed.</summary>
    private void ZoomAt(Point p, double factor)
    {
        var before = ToMm(p); Scale = Math.Clamp(Scale * factor, 0.005, 5);
        var after = ToScreen(before); PanBy(p.X - after.X, p.Y - after.Y); InvalidateVisual();
    }

    /// <summary>"in", "out" (about the centre) or "fit" (fit everything, drop the user rotation).</summary>
    public void Zoom(string how)
    {
        if (how == "fit") { UserRotationRad = 0; FitAll(); InvalidateVisual(); return; }
        ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), how == "in" ? 1.25 : 1 / 1.25);
    }

    public void FitAll()
    {
        if (Side) { FitSide(); return; }
        var pts = Vm?.Building.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).ToList();
        // the background pictures count too, so a freshly imported plan is entirely visible
        if (Vm?.CurrentLevel is { ImageTransform: { Length: 6 } m } && Vm.BackgroundSize() is { } sz)
        {
            pts ??= new(); pts.AddRange(ImageStretch.Corners(ImageStretch.FromArray(m), sz.W, sz.H).Select(c => new PointMm((int)c.X, (int)c.Y)));
        }
        if (pts is not { Count: > 0 } || Bounds.Width < 10) return;
        int minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        Scale = Math.Min((Bounds.Width - 80) / Math.Max(1, maxX - minX), (Bounds.Height - 80) / Math.Max(1, maxY - minY));
        Offset = (40 - minX * Scale, 40 - minY * Scale); _fitted = true;
    }

    private IEnumerable<IReadOnlyList<PointMm>> Polys()
    {
        if (Side) return [];
        var walls = Vm?.CurrentLevel?.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint) ?? [];
        return Vm?.ShowFootprint == true && Vm.FootprintModel() is { } f ? walls.Append(f) : walls;      // the cadastral footprint offers its corners and edges' midpoints to the snapper
    }

    private Wall? HitWall(PointMm p) => Vm?.WallDisplay == WallDisplay.Hidden ? null : Vm?.CurrentLevel?.Walls.LastOrDefault(w =>
        ImmoCad.Core.Geometry.SpaceFinder.Contains(w.Footprint, p) && !w.Holes.Any(h => ImmoCad.Core.Geometry.SpaceFinder.Contains(h, p)));

    /// <summary>The snapper follows the visible grid and the Snap-to-grid toggle.</summary>
    private void SyncSnap() { if (Vm is not { } v) return; Snapper.ToleranceMm = Math.Max(1, (int)Math.Round(10 / Scale)); Snapper.SnapToGrid = v.SnapToGrid; Snapper.GridMm = v.SnapToGrid ? Math.Max(1, (int)Math.Round(NiceStepMm(10))) : 0; }

    private ToolPointer TP(PointerEventArgs e)
    {
        SyncSnap();
        var p = e.GetPosition(this);
        var prev = DrawTool?.Points.Count > 0 ? DrawTool.Points[^1] : (PointMm?)null;
        return new ToolPointer(Snapper.Snap(ToMm(p), Polys(), prev).Point, p.X, p.Y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus(); SyncSnap();
        var props = e.GetCurrentPoint(this).Properties; var p = e.GetPosition(this);
        if (props.IsMiddleButtonPressed) { _pan = p; e.Pointer.Capture(this); return; }
        if (Vm is not { } vm) return;
        if (props.IsRightButtonPressed)
        {
            if (IsChain && DrawTool is { Points.Count: > 0 } dt) { ShowChainMenu(dt); return; }
            DrawTool?.Cancel(); _gesture = Gesture.None; InvalidateVisual(); return;
        }
        if (Side) { SidePressed(e, p); return; }
        if (vm.IsAlignTool) { var m = ToMm(p); vm.AlignClick(m.X, m.Y); if (vm.AlignPairs.Count == 2 && vm.AlignPending == null) { vm.AlignApply(); vm.SetToolCommand.Execute("Select"); } InvalidateVisual(); return; }   // two pairs are enough: applied at once (undo reverts it)
        if (IsChain)
        {
            Invalidate(); _chainTyped = ""; _chainAngle = ""; _chainField = 0;
            if (e.ClickCount >= 2 && DrawTool!.Points.Count >= 2) { DrawTool.DoubleClick(); InvalidateVisual(); return; }      // double-click finishes
            DrawTool!.Down(TP(e)); e.Pointer.Capture(this); InvalidateVisual(); return;
        }
        if (vm.IsStretchTool)
        {
            if (vm.CurrentLevel?.ImageTransform is { Length: 6 } m0 && vm.BackgroundSize() is { } sz0)
            {
                var t0 = ImageStretch.FromArray(m0);
                foreach (var hd in Enum.GetValues<StretchHandle>().Where(x => x != StretchHandle.Move))
                { var hp = ImageStretch.HandlePoint(t0, sz0.W, sz0.H, hd); var sp0 = ToScreen(new PointMm((int)hp.X, (int)hp.Y)); if (Math.Sqrt((sp0.X - p.X) * (sp0.X - p.X) + (sp0.Y - p.Y) * (sp0.Y - p.Y)) <= 10) { _stretch = hd; break; } }
                if (_stretch == null) { var cs = ImageStretch.Corners(t0, sz0.W, sz0.H); var poly = cs.Select(c => new PointMm((int)c.X, (int)c.Y)).ToList(); if (SpaceFinder.Contains(poly, ToMm(p))) _stretch = StretchHandle.Move; }
                if (_stretch != null) { _stretchStart = t0; var mm = ToMm(p); _stretchFrom = (mm.X, mm.Y); _stretchLive = t0; e.Pointer.Capture(this); }
            }
            return;
        }
        if (vm.IsMergeRegionTool) { _lasso = [p]; e.Pointer.Capture(this); InvalidateVisual(); return; }
        if (vm.IsDoorTool) { vm.PlaceOpening(ToMm(p), OpeningKind.Door); return; }
        if (vm.IsWindowTool) { vm.PlaceOpening(ToMm(p), OpeningKind.Window); return; }
        if (vm.IsSpaceTool) { vm.PlaceSpace(ToMm(p)); return; }
        if (vm.IsNumberTool) { vm.NumberClick(ToMm(p)); return; }
        if (vm.IsCutWallTool) { if (e.ClickCount >= 2) vm.CutWallFinish(); else vm.CutWallClick(Snapper.Snap(ToMm(p), Polys()).Point); InvalidateVisual(); return; }
        if (vm.IsMeasureTool) { vm.MeasureClick(Snapper.Snap(ToMm(p), Polys()).Point); InvalidateVisual(); return; }
        if (vm.IsRiserTool) { vm.PlaceRiser(ToMm(p)); return; }
        if (vm.IsPlaceTool) { vm.PlaceFromPalette(ToMm(p)); return; }
        foreach (var (rect, lvl, c) in _arrows)
            if (rect.Contains(p)) { vm.SelectLevelCommand.Execute(lvl); CenterOn(c); return; }
        if (_space) { _pan = p; e.Pointer.Capture(this); return; }                                                                                                // Space+drag: pan
        if ((e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && HitHandle(p) == null) { _rotFrom = p; e.Pointer.Capture(this); return; }   // Ctrl/⌘+drag: rotate

        // equipment: port handle starts Connect, body selects/moves
        if (vm.SelectedEquipment is { } se && EquipmentCatalog.Find(se.CatalogId)?.With(se.Parameters) is { } sc && vm.CurrentLevel is { } sl)
            foreach (var (port, at) in Symbol.PortPositions(sc, se.Position, sl.AltitudeMm + se.ElevationMm, se.RotationDeg))
            { var sp = ToScreen(new PointMm(at.X, at.Y)); if (Math.Sqrt((sp.X - p.X) * (sp.X - p.X) + (sp.Y - p.Y) * (sp.Y - p.Y)) <= 9) { _connect = (se.Id, port, at, ToMm(p)); e.Pointer.Capture(this); return; } }
        if (HitEquipment(ToMm(p)) is { } heq) { vm.SelectEquipment(heq.Id); _eqDrag = (heq.Id, heq.Position, ToMm(p)); e.Pointer.Capture(this); InvalidateVisual(); return; }
        // pipe handles and pipe bodies come before walls
        if (_handleTool != null && _handleTool.State == ToolState.Placing) { _handleTool.ForceElbow = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt); _handleTool.Down(HandlePointer(e)); FinishHandle(vm); return; }
        if (HitHandle(p) is { } h) { _handleTool = new PipeHandleTool(vm.Actions, h.Seg.Network.Id, h.Seg.Segment.Id, h.Handle, Snapper.GridMm) { ForceElbow = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt) }; _typed = ""; _handleTool.Down(HandlePointer(e)); e.Pointer.Capture(this); InvalidateVisual(); return; }
        if (HitRiser(p) is { } riser) { vm.SelectSegment(riser.Network.Id, riser.Segment.Id); InvalidateVisual(); return; }
        if (HitPipe(p) is { } pipe)
        {
            vm.SelectSegment(pipe.Network.Id, pipe.Segment.Id);
            _handleTool = new PipeHandleTool(vm.Actions, pipe.Network.Id, pipe.Segment.Id, PipeHandle.Body, Snapper.GridMm); _typed = ""; _handleTool.Down(HandlePointer(e)); e.Pointer.Capture(this); InvalidateVisual(); return;
        }
        vm.ClearSegment(); if (vm.SelectedEquipmentId != null) vm.SelectEquipment(null);
        _start = _now = p;
        if (vm.HitOpening(ToMm(p)) is { } oh) { vm.SelectOpening(oh.Wall.Id, oh.Opening.Id); _openingDrag = oh; e.Pointer.Capture(this); return; } _startMm = ToMm(p); _delta = (0, 0); _addToSelection = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var hit = HitWall(_startMm);
        if (hit != null && vm.SelectedWalls.Contains(hit.Id) && !_addToSelection) _gesture = Gesture.Move;       // drag the group
        else if (hit != null) { vm.SelectWalls([hit.Id], _addToSelection); _gesture = Gesture.Move; }            // select then drag
        else if (!_addToSelection && vm.SpaceAt(_startMm) is { } room) { vm.SelectWalls([], false); vm.ClearOpening(); vm.SelectSpace(room.Id); _gesture = Gesture.Box; }
        else { if (!_addToSelection) { vm.SelectWalls([], false); vm.ClearOpening(); vm.ClearSpace(); } _gesture = Gesture.Box; }
        e.Pointer.Capture(this); InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this); SyncSnap();
        if (_pan is { } l) { PanBy(p.X - l.X, p.Y - l.Y); _pan = p; InvalidateVisual(); return; }
        if (_rotFrom is { } rf)
        {
            double cx = Bounds.Width / 2, cy = Bounds.Height / 2;
            var d = Math.Atan2(p.Y - cy, p.X - cx) - Math.Atan2(rf.Y - cy, rf.X - cx);
            if (d > Math.PI) d -= 2 * Math.PI; else if (d < -Math.PI) d += 2 * Math.PI;
            UserRotationRad += d; _rotFrom = p; InvalidateVisual(); return;
        }
        if (Vm is not { } vm) { CursorMoved?.Invoke(ToMm(p)); return; }
        CursorMoved?.Invoke(vm.Tool == ToolKind.Select ? ToMm(p) : MarkerSnap(ToMm(p)).Point);   // the readout follows the cross, not the mouse
        _hover = ToMm(p); _over = true;
        if (_sideDrag != null) { SideMoved(p, e.KeyModifiers); return; }
        if (vm.Tool != ToolKind.Select) { if (!_space) Cursor = new Cursor(StandardCursorType.Cross); InvalidateVisual(); } else if (!_space && Cursor != null) Cursor = null;
        if (_handleTool != null) { _handleTool.ForceElbow = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt); _handleTool.Move(HandlePointer(e)); InvalidateVisual(); return; }
        if (IsChain) { DrawTool?.Move(TP(e)); InvalidateVisual(); return; }
        if (vm.IsPlaceTool || vm.IsRiserTool || vm.IsDoorTool || vm.IsWindowTool || vm.IsSpaceTool) { InvalidateVisual(); return; }
        if (_connect is { } cn0) { _connect = (cn0.EqId, cn0.Port, cn0.At, ToMm(p)); InvalidateVisual(); return; }
        if (_eqDrag is { } ed0) { var m = ToMm(p); var d = (X: m.X - ed0.From.X, Y: m.Y - ed0.From.Y); var orig = Vm.Building.Equipment.First(q => q.Id == ed0.Id).Position; _eqDrag = (ed0.Id, new PointMm(Snap(orig.X + d.X, Snapper.GridMm), Snap(orig.Y + d.Y, Snapper.GridMm)), ed0.From); InvalidateVisual(); return; }
        if (_stretch is { } sh && Vm.BackgroundSize() is { } szm) { var mm = ToMm(p); _stretchLive = ImageStretch.Drag(_stretchStart, szm.W, szm.H, sh, _stretchFrom, (mm.X, mm.Y), keepRatio: e.KeyModifiers.HasFlag(KeyModifiers.Shift)); InvalidateVisual(); return; }
        if (_openingDrag != null) { _now = p; InvalidateVisual(); return; }
        if (_lasso != null) { if (Math.Abs(p.X - _lasso[^1].X) + Math.Abs(p.Y - _lasso[^1].Y) > 3) _lasso.Add(p); InvalidateVisual(); return; }
        if (vm.IsMeasureTool || vm.IsNumberTool || vm.IsCutWallTool) { InvalidateVisual(); }
        if (_gesture == Gesture.None) return;
        _now = p;
        if (_gesture == Gesture.Move)
        {
            var d = ToMm(p); var dx = d.X - _startMm.X; var dy = d.Y - _startMm.Y;
            var g = Snapper.GridMm; _delta = (Snap(dx, g), Snap(dy, g));  // moves in grid steps
        }
        InvalidateVisual();
    }

    private void FinishHandle(MainViewModel vm)
    {
        var t = _handleTool!; _handleTool = null; _typed = "";
        vm.AfterEdit(t.Committed ? T("pipe.modified") : t.Refused ?? T("nochange")); InvalidateVisual();
    }

    private static int Snap(int v, int g) => g <= 0 ? v : (int)Math.Round((double)v / g) * g;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var wasView = _pan != null || _rotFrom != null; _pan = null; _rotFrom = null; e.Pointer.Capture(null);
        if (_sideDrag != null) { SideReleased(e); return; }
        if (wasView) return;
        if (Vm is not { } vm) return;
        if (IsChain) { DrawTool?.Up(TP(e)); InvalidateVisual(); return; }
        if (_handleTool != null) { _handleTool.Up(HandlePointer(e)); if (_handleTool.State == ToolState.Committed) FinishHandle(vm); else if (_handleTool.State == ToolState.Placing && _handleTool.Handle == PipeHandle.Body) { _handleTool.Cancel(); _handleTool = null; vm.AfterEdit(); } /* a plain click on the body only selects */ InvalidateVisual(); return; }
        if (_connect is { } cr) { _connect = null; vm.ConnectPort(cr.EqId, cr.Port, cr.At, ToMm(e.GetPosition(this))); InvalidateVisual(); return; }
        if (_eqDrag is { } er) { _eqDrag = null; var orig = vm.Building.Equipment.First(q => q.Id == er.Id).Position; if (er.To != orig) vm.MoveEquipmentTo(er.Id, er.To); InvalidateVisual(); return; }
        if (_stretch != null) { var live = _stretchLive; _stretch = null; _stretchLive = null; if (live is { } lt && lt != _stretchStart) vm.SetImageTransform(lt); InvalidateVisual(); return; }
        if (_lasso is { } ls) { _lasso = null; vm.MergeRegion(ls.Select(ToMm).ToList()); InvalidateVisual(); return; }
        if (vm.IsRiserTool || vm.IsPlaceTool || vm.IsAlignTool || vm.IsDoorTool || vm.IsWindowTool || vm.IsSpaceTool || vm.IsNumberTool || vm.IsMeasureTool || vm.IsCutWallTool) return;
        if (_openingDrag is { } od) { var to = ToMm(e.GetPosition(this)); _openingDrag = null; if (Math.Abs(_now.X - _start.X) + Math.Abs(_now.Y - _start.Y) > 3) vm.MoveOpeningTo(od.Wall, od.Opening, to); InvalidateVisual(); return; }
        if (_gesture == Gesture.Move && Math.Abs(_now.X - _start.X) + Math.Abs(_now.Y - _start.Y) > 3) vm.MoveSelection(_delta.X, _delta.Y);
        else if (_gesture == Gesture.Box)
        {
            var a = ToMm(_start); var b = ToMm(_now); int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            if (vm.CurrentLevel is { } lv)
                vm.SelectWalls(vm.WallDisplay == WallDisplay.Hidden ? [] : lv.Walls.Where(w => w.Footprint.Any(q => q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1)).Select(w => w.Id), _addToSelection);
        }
        _gesture = Gesture.None; _delta = (0, 0); InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var cmd = e.KeyModifiers.HasFlag(MenuFactory.CommandModifiers);
        if (OperatingSystem.IsMacOS() && !cmd)
        {   // trackpad: two-finger scroll pans (pinch zooms, ⌘+scroll zooms)
            PanBy(e.Delta.X * 40, e.Delta.Y * 40); InvalidateVisual(); e.Handled = true; return;
        }
        ZoomAt(e.GetPosition(this), Math.Pow(1.15, Math.Clamp(e.Delta.Y, -3, 3)));
    }

    protected override void OnPointerExited(PointerEventArgs e) { _over = false; InvalidateVisual(); base.OnPointerExited(e); }

    /// <summary>The point the next click will use for the active tool: snapped for the tools that snap, the raw pointer position otherwise.</summary>
    private SnapResult MarkerSnap(PointMm raw) => Side && Vm is { Tool: ToolKind.Measure } ? new SnapResult(new PointMm(Snap(raw.X, Snapper.GridMm), Snap(raw.Y, Snapper.GridMm)), SnapKind.None) : Vm is { Tool: ToolKind.DrawWall or ToolKind.DrawPipe or ToolKind.Measure or ToolKind.CutWall or ToolKind.Align or ToolKind.Straighten or ToolKind.Stretch }
        ? Snapper.Snap(raw, Polys(), IsChain && DrawTool?.Points.Count > 0 ? DrawTool.Points[^1] : null) : new SnapResult(raw, SnapKind.None);

    /// <summary>Marker under the pointer while a tool is active: green ring + cross at the point the next click will use (snapped), with the tool name.</summary>
    private void DrawToolCursor(DrawingContext ctx, bool dark)
    {
        var vm = Vm!; if (!_over || vm.Tool == ToolKind.Select || _space || !Bounds.Contains(ToScreen(_hover))) return;
        var snapped = MarkerSnap(_hover);
        var p = ToScreen(snapped.Point); var green = new SolidColorBrush(Color.FromRgb(40, 190, 70));
        var halo = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 4); var pen = new Pen(green, 1.8);
        foreach (var pn in new[] { halo, pen })
        {
            ctx.DrawLine(pn, p + new Point(-14, 0), p + new Point(14, 0)); ctx.DrawLine(pn, p + new Point(0, -14), p + new Point(0, 14));
        }
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 40, 190, 70)), pen, p, 6, 6);
        if (snapped.Kind is SnapKind.Corner or SnapKind.Midpoint) ctx.DrawRectangle(null, new Pen(Brushes.OrangeRed, 2), new Rect(p.X - 9, p.Y - 9, 18, 18));
        var ft = Txt(T("toolname." + vm.Tool), 11, dark ? Brushes.White : Brushes.Black);
        var at = p + new Point(16, 14); ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, dark ? (byte)40 : (byte)255, dark ? (byte)42 : (byte)255, dark ? (byte)46 : (byte)255)), null, new Rect(at.X - 3, at.Y - 1, ft.Width + 6, ft.Height + 2), 3, 3);
        ctx.DrawText(ft, at);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e) { _space = false; Cursor = null; base.OnLostFocus(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.Key == Key.Space) { _space = false; Cursor = null; e.Handled = true; } base.OnKeyUp(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Space && vm.IsRiserTool) { vm.TurnRiser(); InvalidateVisual(); e.Handled = true; return; }                                      // turns a multiple riser about the cursor
        if (e.Key == Key.Space && !IsChain && _handleTool == null) { _space = true; Cursor = new Cursor(StandardCursorType.Hand); e.Handled = true; return; }
        if (vm.IsStraightenTool && e.Key == Key.Enter) { vm.StraightenSelectionCommand.Execute(null); vm.SetToolCommand.Execute("Select"); InvalidateVisual(); e.Handled = true; return; }
        if (vm.IsCutWallTool && e.Key == Key.Enter) { vm.CutWallFinish(); InvalidateVisual(); e.Handled = true; return; }
        if (vm.IsMeasureTool && !vm.IsSidePlane && e.Key == Key.Enter && vm.MeasurePoints.Count == 2) { vm.MeasureScaleCommand.Execute(null); e.Handled = true; return; }
        if (vm.IsNumberTool && e.Key == Key.Enter) { vm.NumberCommit(); InvalidateVisual(); e.Handled = true; return; }
        if (e.Key == Key.Escape && (vm.IsDoorTool || vm.IsWindowTool || vm.IsSpaceTool || vm.IsMeasureTool || vm.IsCutWallTool || vm.IsMergeRegionTool || vm.IsNumberTool || vm.IsPlaceTool || vm.IsRiserTool || vm.IsStraightenTool)) { vm.SetToolCommand.Execute("Select"); e.Handled = true; return; }
        if (vm.IsAlignTool)
        {
            if (e.Key == Key.Enter) vm.AlignApply(); else if (e.Key == Key.Back) vm.AlignUndoPair(); else if (e.Key == Key.Escape) { vm.AlignCancel(); vm.SetToolCommand.Execute("Select"); } else return;
            InvalidateVisual(); e.Handled = true; return;
        }
        if (DrawTool != null && IsChain)
        {
            if (e.Key == Key.Space && vm.IsDrawTool) { vm.CycleWallAlign(); InvalidateVisual(); e.Handled = true; return; }
            var ch = e.Key >= Key.D0 && e.Key <= Key.D9 ? (char)('0' + (e.Key - Key.D0)) : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 ? (char)('0' + (e.Key - Key.NumPad0)) : e.Key is Key.OemPeriod or Key.OemComma or Key.Decimal ? '.' : e.Key == Key.OemMinus ? '-' : '\0';
            if (ch != '\0' && DrawTool.Points.Count > 0) { if (_chainField == 0) _chainTyped += ch; else _chainAngle += ch; InvalidateVisual(); e.Handled = true; return; }
            if (e.Key == Key.Tab && DrawTool.Points.Count > 0 && _chainTyped.Length > 0) { _chainField = 1 - _chainField; InvalidateVisual(); e.Handled = true; return; }
            if (e.Key == Key.Enter)
            {
                if (_chainTyped.Length > 0 && DrawChainTool.TryParseLength(_chainTyped, out var mm))
                {
                    double? angle = double.TryParse(_chainAngle.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : null;
                    DrawTool.ApplyLength(mm, angle); _chainTyped = ""; _chainAngle = ""; _chainField = 0;
                }
                else DrawTool.Finish();
            }
            else if (e.Key == Key.Back) { if (_chainField == 1 && _chainAngle.Length > 0) _chainAngle = _chainAngle[..^1]; else if (_chainTyped.Length > 0) _chainTyped = _chainTyped[..^1]; else DrawTool.Backspace(); }
            else if (e.Key == Key.Escape) { if (_chainTyped.Length > 0) { _chainTyped = ""; _chainAngle = ""; _chainField = 0; } else if (!DrawTool.EscapeStep()) vm.SetToolCommand.Execute("Select"); }
            else return;
            InvalidateVisual(); e.Handled = true; return;
        }
        if (_handleTool != null && _handleTool.State is ToolState.Dragging or ToolState.Placing)
        {   // typed length while a handle gesture is running: digits, comma/point, Enter applies, Esc cancels
            if (e.Key == Key.Escape) { _handleTool.Cancel(); _handleTool = null; _typed = ""; InvalidateVisual(); e.Handled = true; return; }
            var ch = e.Key >= Key.D0 && e.Key <= Key.D9 ? (char)('0' + (e.Key - Key.D0)) : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 ? (char)('0' + (e.Key - Key.NumPad0)) : e.Key is Key.OemPeriod or Key.OemComma or Key.Decimal ? '.' : '\0';
            if (ch != '\0') { _typed += ch; if (DrawChainTool.TryParseLength(_typed, out var mm)) _handleTool.TypedLengthMm = mm; InvalidateVisual(); e.Handled = true; return; }
            if (e.Key == Key.Back && _typed.Length > 0) { _typed = _typed[..^1]; _handleTool.TypedLengthMm = DrawChainTool.TryParseLength(_typed, out var m2) ? m2 : null; InvalidateVisual(); e.Handled = true; return; }
            if (e.Key == Key.Enter) { if (_handleTool.State == ToolState.Placing) _handleTool.Finish(); FinishHandle(vm); e.Handled = true; return; }
        }
        if (vm.IsStretchTool && vm.CurrentLevel?.ImageTransform != null)
        {
            var st = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;       // arrow keys nudge the picture by 1 mm (Shift: 10 mm)
            switch (e.Key) { case Key.Left: vm.NudgeImage(-st, 0); break; case Key.Right: vm.NudgeImage(st, 0); break; case Key.Up: vm.NudgeImage(0, st); break; case Key.Down: vm.NudgeImage(0, -st); break; default: goto normal; }
            e.Handled = true; return;
        }
        normal:
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 100 : 10;
        switch (e.Key)
        {
            case Key.Left: vm.MoveSelection(-step, 0); break;
            case Key.Right: vm.MoveSelection(step, 0); break;
            case Key.Up: vm.MoveSelection(0, step); break;
            case Key.Down: vm.MoveSelection(0, -step); break;
            case Key.Delete or Key.Back: vm.DeleteSelectionCommand.Execute(null); break;
            case Key.Escape: vm.SelectWalls([], false); break;
            case Key.PageUp: if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) vm.MoveSelectionUpCommand.Execute(null); else vm.LevelUpCommand.Execute(null); break;
            case Key.PageDown: if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) vm.MoveSelectionDownCommand.Execute(null); else vm.LevelDownCommand.Execute(null); break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta): vm.SelectAllCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

        /// <summary>The point at the centre of the viewport (mm): the look-at point shared with the 3D view.</summary>
    public PointMm LookAtMm => ToMm(new Point(Bounds.Width / 2, Bounds.Height / 2));
    public void CenterOn(PointMm c) => Offset = (Bounds.Width / 2 - c.X * Scale, Bounds.Height / 2 - c.Y * Scale);

    private static StreamGeometry Poly(Point[] pts) { var g = new StreamGeometry(); using var c = g.Open(); c.BeginFigure(pts[0], false); foreach (var p in pts.Skip(1)) c.LineTo(p); c.EndFigure(true); return g; }
    private static FormattedText Txt(string t, double size, IBrush b) => new(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, b);

    private void DrawSpaces(DrawingContext ctx, Level l, bool dark)
    {
        foreach (var sp in l.Spaces)
        {
            if (Vm!.CommonOnly && sp.Stability == Stability.Private) continue;
            var pending = Vm!.NumberPending.Contains(sp.Id);
            var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(ToScreen(sp.Outline[0]), true); foreach (var p in sp.Outline.Skip(1)) c.LineTo(ToScreen(p)); c.EndFigure(true); }
            if (Vm.SelectedSpaceId == sp.Id) ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, 255, 120, 0)), new Pen(Brushes.OrangeRed, 2), g);
            else ctx.DrawGeometry(new SolidColorBrush(pending ? Color.FromArgb(90, 255, 160, 0) : sp.Stability == Stability.Common ? Color.FromArgb(34, 80, 160, 255) : Color.FromArgb(22, 120, 200, 120)), null, g);
            var cx = (int)sp.Outline.Average(p => p.X); var cy = (int)sp.Outline.Average(p => p.Y); var cs = ToScreen(new PointMm(cx, cy));
            var label = sp.DisplayName; var ft = Txt(label, 12, dark ? Brushes.Gainsboro : Brushes.DimGray);
            ctx.DrawText(ft, new Point(cs.X - ft.Width / 2, cs.Y - ft.Height / 2));
        }
    }

    /// <summary>Dashed ghost of the door/window that a click would place (cut, leaf and swing arc for a door).</summary>
    private void DrawOpeningPreview(DrawingContext ctx, OpeningKind kind)
    {
        if (Vm!.OpeningPreview(_hover, kind) is not { } pv || WallGeometry.Axis(pv.Wall) is not { } ax) return;
        double dx = ax.B.X - ax.A.X, dy = ax.B.Y - ax.A.Y; var len = Math.Sqrt(dx * dx + dy * dy); double nx = -dy / len, ny = dx / len;
        var c0 = ax.PointAt(pv.Pos - pv.Width / 2.0); var c1 = ax.PointAt(pv.Pos + pv.Width / 2.0); var h = ax.ThicknessMm / 2.0;
        PointMm Q(PointMm c, double side) => new((int)(c.X + nx * h * side), (int)(c.Y + ny * h * side));
        var dash = new Pen(Brushes.OrangeRed, 1.6, DashStyle.Dash); var fill = new SolidColorBrush(Color.FromArgb(50, 255, 120, 0));
        var cut = new StreamGeometry(); using (var cx = cut.Open()) { cx.BeginFigure(ToScreen(Q(c0, 1)), true); cx.LineTo(ToScreen(Q(c1, 1))); cx.LineTo(ToScreen(Q(c1, -1))); cx.LineTo(ToScreen(Q(c0, -1))); cx.EndFigure(true); }
        ctx.DrawGeometry(fill, dash, cut);
        if (kind == OpeningKind.Window) { ctx.DrawLine(dash, ToScreen(c0), ToScreen(c1)); return; }
        var leafEnd = new PointMm((int)(c1.X + nx * pv.Width), (int)(c1.Y + ny * pv.Width));   // default swing: hinge at the far end, leaf towards the left of the wall
        ctx.DrawLine(dash, ToScreen(c1), ToScreen(leafEnd));
        var arc = new StreamGeometry(); using (var ac = arc.Open())
        {
            ac.BeginFigure(ToScreen(c0), false);
            for (var k = 1; k <= 12; k++)
            {
                var a = Math.PI / 2 * k / 12; var vx = (c0.X - c1.X) * Math.Cos(a) + nx * pv.Width * Math.Sin(a); var vy = (c0.Y - c1.Y) * Math.Cos(a) + ny * pv.Width * Math.Sin(a);
                ac.LineTo(ToScreen(new PointMm((int)(c1.X + vx), (int)(c1.Y + vy))));
            }
            ac.EndFigure(false);
        }
        ctx.DrawGeometry(null, dash, arc);
    }

    private void DrawOpenings(DrawingContext ctx, Level l, bool dark)
    {
        if (Vm?.WallDisplay == WallDisplay.Hidden) return;
        var bg = new SolidColorBrush(dark ? Color.FromRgb(30, 32, 36) : Color.FromRgb(250, 250, 248));
        foreach (var w in l.Walls)
        {
            if (Vm!.CommonOnly && w.Stability == Stability.Private) continue;
            if (WallGeometry.Axis(w) is not { } ax) continue;
            double dx = ax.B.X - ax.A.X, dy = ax.B.Y - ax.A.Y; var len = Math.Sqrt(dx * dx + dy * dy); double ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
            foreach (var o in w.Openings)
            {
                var c0 = ax.PointAt(o.PositionMm - o.WidthMm / 2.0); var c1 = ax.PointAt(o.PositionMm + o.WidthMm / 2.0); var h = ax.ThicknessMm / 2.0;
                PointMm Q(PointMm c, double side) => new((int)(c.X + nx * h * side), (int)(c.Y + ny * h * side));
                var cut = new StreamGeometry(); using (var cx = cut.Open()) { cx.BeginFigure(ToScreen(Q(c0, 1)), true); cx.LineTo(ToScreen(Q(c1, 1))); cx.LineTo(ToScreen(Q(c1, -1))); cx.LineTo(ToScreen(Q(c0, -1))); cx.EndFigure(true); }
                ctx.DrawGeometry(bg, null, cut);                                                                                       // hole in the wall
                var pen = new Pen(dark ? Brushes.Gainsboro : Brushes.Black, 1.2);
                if (Vm.SelectedOpening?.OpeningId == o.Id) ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 255, 120, 0)), new Pen(Brushes.OrangeRed, 2), cut);
                if (o.Kind == OpeningKind.Window) { foreach (var side in new[] { 1.0, 0.0, -1.0 }) ctx.DrawLine(pen, ToScreen(Q(c0, side)), ToScreen(Q(c1, side))); }
                else if (o.Kind == OpeningKind.Door)
                {   // leaf perpendicular to the wall at the hinge, arc swept by the free end
                    var hinge = o.SwingLeft ? c0 : c1; var free = o.SwingLeft ? c1 : c0; var dir = o.SwingOut ? -1 : 1;
                    var leafEnd = new PointMm((int)(hinge.X + nx * o.WidthMm * dir), (int)(hinge.Y + ny * o.WidthMm * dir));
                    ctx.DrawLine(pen, ToScreen(hinge), ToScreen(leafEnd));
                    var arc = new StreamGeometry(); using (var ac = arc.Open())
                    {
                        ac.BeginFigure(ToScreen(free), false);
                        for (var k = 1; k <= 12; k++)
                        {
                            var a = Math.PI / 2 * k / 12; var vx = (free.X - hinge.X) * Math.Cos(a) + nx * dir * o.WidthMm * Math.Sin(a); var vy = (free.Y - hinge.Y) * Math.Cos(a) + ny * dir * o.WidthMm * Math.Sin(a);
                            ac.LineTo(ToScreen(new PointMm((int)(hinge.X + vx), (int)(hinge.Y + vy))));
                        }
                        ac.EndFigure(false);
                    }
                    ctx.DrawGeometry(null, new Pen(dark ? Brushes.Gainsboro : Brushes.Black, 0.8, DashStyle.Dash), arc);
                }
                if (!string.IsNullOrEmpty(o.Label)) { var cm = ax.PointAt(o.PositionMm); var ft = Txt(o.Label, 10, Brushes.SteelBlue); var sp = ToScreen(cm); ctx.DrawText(ft, new Point(sp.X - ft.Width / 2, sp.Y - 18)); }
            }
        }
    }

    /// <summary>Dashed outline of the wall being drawn (its real thickness, on the chosen side of the drawing line), for the segments placed so far and the one under the cursor.</summary>
    private void DrawWallPreview(DrawingContext ctx, DrawChainTool t)
    {
        var vm = Vm!; var fill = new SolidColorBrush(Color.FromArgb(40, 255, 120, 0)); var dash = new Pen(Brushes.OrangeRed, 1.3, DashStyle.Dash);
        void One(PointMm a, PointMm b)
        {
            var r = ImmoCad.Core.Geometry.WallGeometry.Rectangle(a, b, vm.WallThicknessMm, vm.WallAlign).Select(ToScreen).ToList(); if (r.Count < 4) return;
            ctx.DrawGeometry(fill, dash, Poly(r.ToArray()));
        }
        for (var i = 1; i < t.Points.Count; i++) One(t.Points[i - 1], t.Points[i]);
        if (t.Preview is { } pv && t.Points.Count > 0) One(t.Points[^1], pv);
        var label = Txt(T("align." + vm.WallAlign.ToString().ToLowerInvariant()) + "  ·  " + T("align.hint"), 12, Brushes.OrangeRed);
        ctx.DrawText(label, new Point(12, Bounds.Height - 44));
    }

    /// <summary>The plan's origin (0, 0): X axis in red, Y axis in green.</summary>
    private void DrawOrigin(DrawingContext ctx, bool dark)
    {
        var o = ToScreen(new PointMm(0, 0)); if (o.X < -50 || o.Y < -50 || o.X > Bounds.Width + 50 || o.Y > Bounds.Height + 50) return;
        var ax = ToScreen(new PointMm(1, 0)) - o; var ay = ToScreen(new PointMm(0, 1)) - o;
        Point Dir(Point v) { var l = Math.Sqrt(v.X * v.X + v.Y * v.Y); return l < 1e-9 ? new Point(1, 0) : new Point(v.X / l * 46, v.Y / l * 46); }
        var dx = Dir(ax); var dy = Dir(ay); var px = new Pen(Brushes.IndianRed, 2, lineCap: PenLineCap.Round); var py = new Pen(Brushes.SeaGreen, 2, lineCap: PenLineCap.Round);
        ctx.DrawLine(px, o, o + dx); ctx.DrawLine(py, o, o + dy);
        void Head(Point tip, Point d, IPen pen) { var n = new Point(-d.Y / 46 * 4, d.X / 46 * 4); var back = new Point(d.X / 46 * 8, d.Y / 46 * 8); ctx.DrawLine(pen, tip, tip - back + n); ctx.DrawLine(pen, tip, tip - back - n); }
        Head(o + dx, dx, px); Head(o + dy, dy, py);
        var tb = dark ? Brushes.Gainsboro : Brushes.DimGray;
        ctx.DrawText(Txt(Vm?.ViewPlane == ViewPlane.YZ ? "Y" : "X", 12, Brushes.IndianRed), o + dx + new Point(4, -8)); ctx.DrawText(Txt(Side ? "Z" : "Y", 12, Brushes.SeaGreen), o + dy + new Point(4, -8));
        ctx.DrawEllipse(dark ? Brushes.Black : Brushes.White, new Pen(tb, 1.6), o, 4, 4);
        ctx.DrawText(Txt(T("plan.origin") + " (0, 0)", 11, tb), o + new Point(-6, 6));
    }

    private void DrawStraightenPreview(DrawingContext ctx)
    {
        if (Vm is not { IsStraightenTool: true } vm || vm.StraightenPreview() is not { } pv) return;
        foreach (var (_, outline) in pv.Shapes)
        {
            var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(ToScreen(outline[0]), false); foreach (var p in outline.Skip(1)) c.LineTo(ToScreen(p)); c.EndFigure(true); }
            ctx.DrawGeometry(null, new Pen(Brushes.LimeGreen, 2.5), g);                 // straightened outline over the original
            foreach (var p in outline) ctx.DrawRectangle(Brushes.White, new Pen(Brushes.LimeGreen, 1.5), new Rect(ToScreen(p).X - 3, ToScreen(p).Y - 3, 6, 6));
        }
        var first = ToScreen(pv.Shapes[0].Outline[0]); ctx.DrawText(Txt(pv.Info + T("press.enter.apply"), 13, Brushes.ForestGreen), new Point(first.X + 10, first.Y - 22));
    }

    /// <summary>Cadastral footprint as a dashed reference outline (party walls drawn thicker and labelled), plus a north arrow rotated by the site rotation.</summary>
    private void DrawSiteOverlay(DrawingContext ctx, bool dark)
    {
        if (Vm?.Building.Site is not { } site) return;
        if (Vm.ShowFootprint && Vm.FootprintModel() is { } f && f.Count >= 3)
        {
            var shared = Vm.PartyWallEdges().ToHashSet(); var pen = new Pen(Brushes.MediumVioletRed, 1.6, DashStyle.Dash);
            for (var i = 0; i < f.Count; i++)
            {
                var a = ToScreen(f[i]); var b = ToScreen(f[(i + 1) % f.Count]);
                if (shared.Contains(i)) { ctx.DrawLine(new Pen(Brushes.MediumVioletRed, 4), a, b); ctx.DrawText(Txt(T("party.wall"), 11, Brushes.MediumVioletRed), new Point((a.X + b.X) / 2 + 6, (a.Y + b.Y) / 2 - 16)); }
                else ctx.DrawLine(pen, a, b);
            }
        }
        if (Vm.ShowFootprint) foreach (var x in Vm.ExtraFootprintModels().Where(x => x.Count >= 3))
            for (var i = 0; i < x.Count; i++) ctx.DrawLine(new Pen(Brushes.MediumVioletRed, 1.6, DashStyle.Dash), ToScreen(x[i]), ToScreen(x[(i + 1) % x.Count]));
        var c = new Point(Bounds.Width - 70, 156); var r = (Vm.NorthUp ? 0 : site.RotationDeg) * Math.PI / 180;     // the model's Y axis is turned site.RotationDeg° counter-clockwise from north, so north is that far clockwise from screen-up
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), new Pen(Brushes.Gray, 1), c, 22, 22);
        Point Rot(double x, double y) => new(c.X + x * Math.Cos(r) - y * Math.Sin(r), c.Y + x * Math.Sin(r) + y * Math.Cos(r));
        var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(Rot(0, -17), true); cx.LineTo(Rot(7, 9)); cx.LineTo(Rot(0, 4)); cx.LineTo(Rot(-7, 9)); cx.EndFigure(true); }
        ctx.DrawGeometry(Brushes.Black, null, g); ctx.DrawText(Txt("N", 11, Brushes.Black), Rot(-4, -33));
    }

    /// <summary>The cutting line or zone (dashed). Two points and the side to choose: the band on the side under the pointer, the one that goes, is shaded. Three points or more: the zone is shaded, its content goes.</summary>
    private void DrawCutLine(DrawingContext ctx)
    {
        if (Vm is not { IsCutWallTool: true } vm || vm.CutPoints.Count == 0) return;
        var pen = new Pen(Brushes.OrangeRed, 2, DashStyle.Dash); var pts = vm.CutPoints.Select(ToScreen).ToList(); var h = ToScreen(_hover);
        if (!vm.CutAwaitSide) pts.Add(h);
        for (var i = 1; i < pts.Count; i++) ctx.DrawLine(pen, pts[i - 1], pts[i]);
        foreach (var q in pts) ctx.DrawEllipse(Brushes.OrangeRed, null, q, 4, 4);
        if (pts.Count >= 3 && !vm.CutAwaitSide)
        {   // zone: closing edge and shading
            var z = new StreamGeometry(); using (var cx = z.Open()) { cx.BeginFigure(pts[0], true); foreach (var q in pts.Skip(1)) cx.LineTo(q); cx.EndFigure(true); }
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, 255, 60, 0)), pen, z); return;
        }
        if (!vm.CutAwaitSide) return;
        Point a = pts[0], b = pts[1]; double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy); if (len < 1) return;
        double nx = -dy / len, ny = dx / len; var s = Math.Sign(nx * (h.X - a.X) + ny * (h.Y - a.Y)); if (s == 0) return;
        Point Off(Point p) => new(p.X + s * nx * len, p.Y + s * ny * len);
        var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(a, true); cx.LineTo(b); cx.LineTo(Off(b)); cx.LineTo(Off(a)); cx.EndFigure(true); }
        ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, 255, 60, 0)), null, g);
    }

    private void DrawMeasure(DrawingContext ctx)
    {
        if (Vm is not { IsMeasureTool: true } vm || vm.MeasurePoints.Count == 0) return;
        var hover = Side ? MarkerSnap(_hover).Point : _hover;                                   // side views: snapped to the grid, in (along, height)
        var a = ToScreen(vm.MeasurePoints[0]); var b = vm.MeasurePoints.Count == 2 ? ToScreen(vm.MeasurePoints[1]) : ToScreen(hover);
        ctx.DrawLine(new Pen(Brushes.DeepPink, 2, DashStyle.Dash), a, b); ctx.DrawEllipse(Brushes.DeepPink, null, a, 4, 4); ctx.DrawEllipse(Brushes.DeepPink, null, b, 4, 4);
        var end = vm.MeasurePoints.Count == 2 ? vm.MeasurePoints[1] : hover; var d = Math.Sqrt(Math.Pow(end.X - vm.MeasurePoints[0].X, 2) + Math.Pow(end.Y - vm.MeasurePoints[0].Y, 2)) / 1000;
        ctx.DrawText(Txt($"{d:0.00} m", 13, Brushes.DeepPink), new Point((a.X + b.X) / 2 + 8, (a.Y + b.Y) / 2 - 18));
    }

    private void DrawHandles(DrawingContext ctx, Level l)
    {
        if (Vm?.SelectedSegment is not { } sel) return;
        var seg = NetworkQueries.HorizontalAt(Vm.Building, l).FirstOrDefault(s => s.Network.Id == sel.NetworkId && s.Segment.Id == sel.SegmentId);
        if (seg == null) return;
        var a = ToScreen(new PointMm(seg.A.X, seg.A.Y)); var b = ToScreen(new PointMm(seg.B.X, seg.B.Y));
        ctx.DrawLine(new Pen(Brushes.OrangeRed, 3, DashStyle.Dash), a, b);
        foreach (var q in new[] { a, b }) ctx.DrawRectangle(Brushes.White, new Pen(Brushes.OrangeRed, 2), new Rect(q.X - 6, q.Y - 6, 12, 12));
        ctx.DrawEllipse(Brushes.White, new Pen(Brushes.OrangeRed, 2), new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), 6, 6);
        if (_handleTool?.Proposal is { } pr)
        {
            var f = ToScreen(new PointMm(pr.From.X, pr.From.Y)); var t = ToScreen(new PointMm(pr.To.X, pr.To.Y));
            ctx.DrawLine(new Pen(Brushes.DodgerBlue, 3), f, t);
            var text = _typed.Length > 0 ? $"{_typed} m ⏎" : $"{pr.LengthMm / 1000.0:0.00} m";
            ctx.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.DodgerBlue), t + new Point(10, -18));
        }
    }

    private void DrawNetworks(DrawingContext ctx, Level l, bool dark)
    {
        _arrows.Clear();
        var b = Vm!.Building;
        foreach (var s in NetworkQueries.HorizontalAt(b, l).Where(x => x.Network.Visible))
        {
            var c = NetworkQueries.ColorOf(s.Network.Type);
            DrawTube(ctx, ToScreen(new PointMm(s.A.X, s.A.Y)), ToScreen(new PointMm(s.B.X, s.B.Y)), s.Segment.DiameterMm, Color.FromRgb(c.R, c.G, c.B), dark);
        }
        DrawJoints(ctx, NetworkQueries.HorizontalAt(b, l).Where(x => x.Network.Visible), q => ToScreen(new PointMm(q.X, q.Y)));
        foreach (var n in b.Networks.Where(n => n.Visible))
            foreach (var node in n.Nodes.Where(n2 => n2.Component != null && n2.Position.Z >= l.AltitudeMm && n2.Position.Z < l.AltitudeMm + l.HeightMm))
            {
                var p = ToScreen(new PointMm(node.Position.X, node.Position.Y));
                ctx.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1.5), p, 6, 6); ctx.DrawLine(new Pen(Brushes.Black, 1.5), p + new Point(-4, -4), p + new Point(4, 4)); ctx.DrawLine(new Pen(Brushes.Black, 1.5), p + new Point(-4, 4), p + new Point(4, -4));
            }
        foreach (var r in NetworkQueries.RisersAt(b, l).Where(x => x.Network.Visible))
        {
            var c = NetworkQueries.ColorOf(r.Network.Type); var p = ToScreen(new PointMm(r.A.X, r.A.Y)); var rad = RiserRadiusPx(r.Segment.DiameterMm);
            DrawRiserTube(ctx, p, r.Segment.DiameterMm, Color.FromRgb(c.R, c.G, c.B), dark);
            if (Vm.SelectedSegment?.SegmentId == r.Segment.Id) ctx.DrawEllipse(null, new Pen(Brushes.OrangeRed, 2.5), p, rad + 3, rad + 3);
            foreach (var dir in new[] { +1, -1 })
                if (NetworkQueries.NextLevelReached(b, r, l, dir) is { } next)
                {
                    var tip = p + new Point(rad + 6 + (dir > 0 ? 0 : 10), 0); var up = dir > 0;
                    var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(tip + new Point(0, up ? -5 : 5), true); cx.LineTo(tip + new Point(-4, up ? 3 : -3)); cx.LineTo(tip + new Point(4, up ? 3 : -3)); cx.EndFigure(true); }
                    ctx.DrawGeometry(dark ? Brushes.Gainsboro : Brushes.DimGray, null, g);
                    _arrows.Add((new Rect(tip.X - 7, tip.Y - 8, 14, 16), next.Id, new PointMm(r.A.X, r.A.Y)));
                }
        }
        foreach (var e in b.Equipment.Where(q => q.LevelId == l.Id))
        {
            var cat = EquipmentCatalog.Find(e.CatalogId)?.With(e.Parameters); var sel = Vm.SelectedEquipmentId == e.Id; var origin = _eqDrag is { } ed && ed.Id == e.Id ? ed.To : e.Position;
            var pen = new Pen(sel ? Brushes.OrangeRed : Brushes.SlateBlue, sel ? 2.2 : 1.4);
            if (cat == null) { var p0 = ToScreen(origin); ctx.DrawRectangle(Brushes.White, pen, new Rect(p0.X, p0.Y - 12, 40, 12)); continue; }
            var figs = Symbol.Figures(cat, origin, e.RotationDeg);
            foreach (var fig in figs)
            {
                if (fig.Centre is { } c) { ctx.DrawEllipse(null, pen, ToScreen(c), fig.Radius * Scale, fig.Radius * Scale); continue; }
                var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(ToScreen(fig.Points[0]), fig.Closed); foreach (var p in fig.Points.Skip(1)) cx.LineTo(ToScreen(p)); cx.EndFigure(fig.Closed); }
                ctx.DrawGeometry(fig.Closed && ReferenceEquals(fig, figs[0]) ? Brushes.White : null, pen, g);
            }
            if (!string.IsNullOrEmpty(e.Label)) ctx.DrawText(Txt(e.Label, 11, dark ? Brushes.Gainsboro : Brushes.DimGray), ToScreen(origin) + new Point(0, 4));
            if (sel)
                foreach (var (port, at) in Symbol.PortPositions(cat, origin, l.AltitudeMm + e.ElevationMm, e.RotationDeg))
                {
                    var c = NetworkQueries.ColorOf(port.Network); var sp = ToScreen(new PointMm(at.X, at.Y));
                    ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), new Pen(Brushes.White, 1.5), sp, 6, 6);
                }
        }
        if (_connect is { } cn) { var c = NetworkQueries.ColorOf(cn.Port.Network); ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), 2.5, DashStyle.Dash), ToScreen(new PointMm(cn.At.X, cn.At.Y)), ToScreen(cn.To)); }
        if ((Vm.IsDoorTool || Vm.IsWindowTool) && Bounds.Contains(ToScreen(_hover))) DrawOpeningPreview(ctx, Vm.IsDoorTool ? OpeningKind.Door : OpeningKind.Window);
        // dashed ghosts of what the next click places
        if (Vm.IsRiserTool && Bounds.Contains(ToScreen(_hover)))
            foreach (var (at, type, dia) in Vm.RiserPreview(_hover))
            {   // each tube at its real footprint (what it will take on the plan), in its network colour; a dashed ring keeps it visible when zoomed out
                var c = NetworkQueries.ColorOf(type); var col = Color.FromRgb(c.R, c.G, c.B); var sp = ToScreen(at); var real = dia / 2.0 * Scale;
                ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, col.R, col.G, col.B)), new Pen(new SolidColorBrush(Shade(col, 0.5)), 1), sp, real, real);
                if (real < 9) ctx.DrawEllipse(null, new Pen(new SolidColorBrush(col), 1.5, DashStyle.Dash), sp, 9, 9);
                var cross = new Pen(Brushes.Black, 1); ctx.DrawLine(cross, sp + new Point(-3, 0), sp + new Point(3, 0)); ctx.DrawLine(cross, sp + new Point(0, -3), sp + new Point(0, 3));
                if (real >= 11) DrawDiameterLabel(ctx, sp + new Point(0, real + 9), dia, false);
            }
        if (Vm.IsSpaceTool && Bounds.Contains(ToScreen(_hover)))
        {   // the room outline is only recomputed when the cursor moves
            if (_spaceFor != _hover) { _spaceFor = _hover; _spaceOutline = Vm.SpacePreview(_hover); }
            if (_spaceOutline is { Count: > 2 } so)
            {
                var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(ToScreen(so[0]), true); foreach (var q in so.Skip(1)) cx.LineTo(ToScreen(q)); cx.EndFigure(true); }
                ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, 255, 120, 0)), new Pen(Brushes.OrangeRed, 1.6, DashStyle.Dash), g);
            }
        }
        if (Vm.IsPlaceTool && Vm.PalettePreview(_hover) is { } pp && Bounds.Contains(ToScreen(_hover)))
        {   // dashed symbol of the object at its snapped position: orange when it can be placed, red with a hint when not
            var ok = pp.Valid; var col = ok ? Colors.OrangeRed : Colors.Red; var dash = new Pen(new SolidColorBrush(col), 1.6, DashStyle.Dash);
            foreach (var fig in Symbol.Figures(pp.Entry, pp.At))
            {
                if (fig.Centre is { } c) { ctx.DrawEllipse(null, dash, ToScreen(c), fig.Radius * Scale, fig.Radius * Scale); continue; }
                var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(ToScreen(fig.Points[0]), fig.Closed); foreach (var q in fig.Points.Skip(1)) cx.LineTo(ToScreen(q)); cx.EndFigure(fig.Closed); }
                ctx.DrawGeometry(null, dash, g);
            }
            if (!ok && Vm.PlacementHint(_hover) is { } hint)
                ctx.DrawText(new FormattedText(hint, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.Red), ToScreen(pp.At) + new Point(14, -8));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        ctx.FillRectangle(Tokens.Brush(this, "PlanBackground", dark ? Color.FromRgb(30, 32, 36) : Color.FromRgb(250, 250, 248)), new Rect(Bounds.Size));
        if (!_fitted) FitAll();
        KeepViewAcrossPlanes();
        SyncSnap();
        using (ctx.PushTransform(RotationMatrix())) DrawGrid(ctx, dark);
        DrawOrigin(ctx, dark);
        if (Vm?.CurrentLevel is not { } l) return;
        if (Side) { RenderSide(ctx, dark, l); return; }
        if (l.BackgroundVisible && l.ImageTransform is { Length: 6 } tr0 && Background(l) is { } bg)
        {
            var tr = _stretchLive is { } live ? live.ToArray() : tr0;
            // pixels → mm (affine) → screen; axis-aligned or rotated drawing through a matrix
            var m = new Matrix(tr[0] * Scale, -tr[3] * Scale, tr[1] * Scale, -tr[4] * Scale, Offset.X + tr[2] * Scale, Bounds.Height - Offset.Y - tr[5] * Scale);
            using (ctx.PushTransform(m * RotationMatrix())) using (ctx.PushOpacity(l.BackgroundOpacity))
                ctx.DrawImage(bg, new Rect(bg.PixelSize.ToSize(1)));
            if (Vm.IsStretchTool && Vm.BackgroundSize() is { } bs)
            {
                var a = ImageStretch.FromArray(tr);
                var cs = ImageStretch.Corners(a, bs.W, bs.H).Select(c => ToScreen(new PointMm((int)c.X, (int)c.Y))).ToArray();
                ctx.DrawGeometry(null, new Pen(Brushes.DodgerBlue, 1.5, DashStyle.Dash), Poly(cs));
                foreach (var h in Enum.GetValues<StretchHandle>().Where(h => h != StretchHandle.Move)) { var hp = ImageStretch.HandlePoint(a, bs.W, bs.H, h); var sp = ToScreen(new PointMm((int)hp.X, (int)hp.Y)); ctx.DrawRectangle(h == StretchHandle.Rotate ? Brushes.Orange : h == StretchHandle.Shear ? Brushes.LimeGreen : Brushes.White, new Pen(Brushes.DodgerBlue, 2), new Rect(sp.X - 5, sp.Y - 5, 10, 10)); }
            }
        }
        var below = Vm.Building.Levels.LastOrDefault(x => x.AltitudeMm < l.AltitudeMm);
        if (below?.ImageTransform is { Length: 6 } bt && below.BackgroundVisible && Background(below) is { } bbg && (Vm.BgDifference || Vm.IsAlignTool || Vm.IsStretchTool))
        {   // the level below's picture in a contrasting colour (blue tint), or as a difference blend, to see how well the two plans overlap
            var bm = new Matrix(bt[0] * Scale, -bt[3] * Scale, bt[1] * Scale, -bt[4] * Scale, Offset.X + bt[2] * Scale, Bounds.Height - Offset.Y - bt[5] * Scale);
            using (ctx.PushTransform(bm * RotationMatrix())) using (ctx.PushOpacity(Vm.BgDifference ? 1.0 : 0.35))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapBlendingMode = Vm.BgDifference ? BitmapBlendingMode.Difference : BitmapBlendingMode.SourceOver }))
                ctx.DrawImage(bbg, new Rect(bbg.PixelSize.ToSize(1)));
        }
        var ghostBlue = Tokens.Get(this, "GhostBelow", Color.FromRgb(60, 140, 255));
        foreach (var (g, k) in Vm.Building.Levels.Where(x => x.AltitudeMm < l.AltitudeMm && !Vm.HiddenLevels.Contains(x.Id)).OrderByDescending(x => x.AltitudeMm).Select((x, k) => (x, k)).Reverse())
            if (Vm.GhostBelow) DrawWalls(ctx, g, new SolidColorBrush(Color.FromArgb((byte)(k == 0 ? 55 : 28), ghostBlue.R, ghostBlue.G, ghostBlue.B)), null, null);      // every level with its eye open, the nearest strongest
        foreach (var (g, k) in Vm.Building.Levels.Where(x => x.AltitudeMm > l.AltitudeMm && !Vm.HiddenLevels.Contains(x.Id)).OrderBy(x => x.AltitudeMm).Select((x, k) => (x, k)).Reverse())
            if (Vm.GhostAbove) DrawWalls(ctx, g, new SolidColorBrush(Color.FromArgb((byte)(k == 0 ? 45 : 24), 255, 150, 40)), null, null);
        var wallFill = Tokens.Brush(this, "WallFill", dark ? Color.FromRgb(190, 192, 198) : Color.FromRgb(70, 72, 78));
        var sel = new SolidColorBrush(Color.FromRgb(255, 140, 0));
        DrawWalls(ctx, l, wallFill, new Pen(new SolidColorBrush(dark ? Colors.Black : Colors.Black), 0.8), null, Vm.SelectedWalls, sel);
        foreach (var rm in l.References)   // locked reference mesh: horizontal section 1 m above the floor, to trace over
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 160, 90, 200)), 1.2, DashStyle.Dash);
            foreach (var (a, c) in ReferenceImport.Slice(rm, l.AltitudeMm + 1000)) ctx.DrawLine(pen, ToScreen(a), ToScreen(c));
        }
        DrawSiteOverlay(ctx, dark);
        DrawSpaces(ctx, l, dark);
        DrawOpenings(ctx, l, dark);
        DrawNetworks(ctx, l, dark);
        DrawHandles(ctx, l);
        if (_lasso is { Count: > 1 } lz)
        {
            var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(lz[0], true); foreach (var q in lz.Skip(1)) cx.LineTo(q); cx.EndFigure(true); }
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, 0, 120, 255)), new Pen(Brushes.DodgerBlue, 1.5, DashStyle.Dash), g);
        }
        DrawMeasure(ctx); DrawCutLine(ctx); DrawStraightenPreview(ctx);
        if (_gesture == Gesture.Move && (_delta.X != 0 || _delta.Y != 0))   // ghost of the move
            DrawWalls(ctx, l, new SolidColorBrush(Color.FromArgb(110, 255, 140, 0)), new Pen(Brushes.OrangeRed, 1.2, DashStyle.Dash), _delta, Vm.SelectedWalls, null, onlySelected: true);
        if (_gesture == Gesture.Box)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 120, 255)), new Pen(Brushes.DodgerBlue, 1, DashStyle.Dash), new Rect(new Point(Math.Min(_start.X, _now.X), Math.Min(_start.Y, _now.Y)), new Point(Math.Max(_start.X, _now.X), Math.Max(_start.Y, _now.Y))));
        if (DrawTool is { Points.Count: > 0 } t)
        {
            var pen = new Pen(Brushes.OrangeRed, 2);
            if (Vm.IsDrawTool) DrawWallPreview(ctx, t);
            for (var i = 1; i < t.Points.Count; i++) ctx.DrawLine(pen, ToScreen(t.Points[i - 1]), ToScreen(t.Points[i]));
            if (t.Preview is { } pv)
            {
                var a = t.Points[^1];
                ctx.DrawLine(new Pen(Brushes.OrangeRed, 1, DashStyle.Dash), ToScreen(a), ToScreen(pv));
                var len = Math.Sqrt(Math.Pow(pv.X - a.X, 2) + Math.Pow(pv.Y - a.Y, 2)) / 1000;
                var live = t.Live(); var segLabel = $"{len:0.00} m  {(live?.AngleDeg ?? 0):0}°";
                ctx.DrawText(new FormattedText(segLabel, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.OrangeRed), ToScreen(pv) + new Point(8, -16));
                if (_chainTyped.Length > 0 || _chainAngle.Length > 0)
                {   // input box near the cursor: length (Tab → angle), Enter applies
                    var box = new Rect(ToScreen(pv).X + 12, ToScreen(pv).Y + 8, 150, 46);
                    ctx.DrawRectangle(Brushes.White, new Pen(Brushes.OrangeRed, 1.5), box, 4, 4);
                    ctx.DrawText(new FormattedText($"{(_chainField == 0 ? "▶ " : "  ")}{T("typed.length", _chainTyped)}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.Black), box.TopLeft + new Point(6, 5));
                    ctx.DrawText(new FormattedText($"{(_chainField == 1 ? "▶ " : "  ")}{T("typed.angle", _chainAngle)}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.Black), box.TopLeft + new Point(6, 24));
                }
            }
        }
        if (Vm.IsAlignTool)
        {
            for (var i = 0; i < Vm.AlignPairs.Count; i++)
            {
                var (pf, pt) = Vm.AlignPairs[i]; var bad = Vm.AlignResiduals != null && Vm.AlignResiduals[i] > 50;
                var pen = new Pen(bad ? Brushes.Red : Brushes.LimeGreen, 2);
                var a = ToScreen(new PointMm((int)pf.X, (int)pf.Y)); var b2 = ToScreen(new PointMm((int)pt.X, (int)pt.Y));
                ctx.DrawLine(pen, a, b2); ctx.DrawEllipse(Brushes.Orange, null, a, 5, 5); ctx.DrawEllipse(Brushes.DodgerBlue, null, b2, 5, 5);
                if (Vm.AlignResiduals != null)
                    ctx.DrawText(new FormattedText($"{Vm.AlignResiduals[i]:0} mm", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, bad ? Brushes.Red : Brushes.ForestGreen), b2 + new Point(8, -14));
            }
            if (Vm.AlignPending is { } pd) ctx.DrawEllipse(Brushes.Orange, new Pen(Brushes.White, 2), ToScreen(new PointMm((int)pd.X, (int)pd.Y)), 6, 6);
        }
        var label = new FormattedText($"{l.DisplayName}  ·  {l.AltitudeMm / 1000.0:0.0} m", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, dark ? Brushes.Gainsboro : Brushes.DimGray);
        ctx.DrawText(label, new Point(12, 8));
        DrawScaleBar(ctx, dark);
        DrawToolCursor(ctx, dark);
    }

    /// <summary>Smallest "nice" step (1, 2, 5 × 10ⁿ mm) that is at least <paramref name="minPx"/> pixels wide at the current zoom.</summary>
    private double NiceStepMm(double minPx)
    {
        var raw = minPx / Scale; var pow = Math.Pow(10, Math.Floor(Math.Log10(raw))); var m = raw / pow;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * pow;
    }

    private static string Length(double mm) => mm >= 1000 ? $"{mm / 1000:0.##} m" : mm >= 10 ? $"{mm / 10:0.##} cm" : $"{mm:0.##} mm";

    /// <summary>Grid spacing adapts to the zoom (minor lines ≥ 10 px apart, a stronger line every 5th); the scale bar and grid size tell the spacing.</summary>
    private void DrawGrid(DrawingContext ctx, bool dark)
    {
        var minorMm = NiceStepMm(10); var step = minorMm * Scale; var major = step * 5;
        var minor = new Pen(Tokens.Brush(this, "PlanGrid", dark ? Color.FromRgb(44, 47, 52) : Color.FromRgb(236, 236, 232)), 1);
        var strong = new Pen(dark ? new SolidColorBrush(Color.FromRgb(62, 66, 72)) : new SolidColorBrush(Color.FromRgb(218, 218, 212)), 1);
        // the view may be rotated: cover the circle that contains the viewport
        double cx = Bounds.Width / 2, cy = Bounds.Height / 2, r = Math.Sqrt(cx * cx + cy * cy);
        var k0 = (int)Math.Floor((cx - r - Offset.X) / step); var k1 = (int)Math.Ceiling((cx + r - Offset.X) / step);
        for (var k = k0; k <= k1; k++) { var x = Offset.X + k * step; ctx.DrawLine(k % 5 == 0 ? strong : minor, new(x, cy - r), new(x, cy + r)); }
        var y0 = Bounds.Height - Offset.Y;
        var j0 = (int)Math.Floor((y0 - cy - r) / step); var j1 = (int)Math.Ceiling((y0 - cy + r) / step);
        for (var k = j0; k <= j1; k++) { var y = y0 - k * step; ctx.DrawLine(k % 5 == 0 ? strong : minor, new(cx - r, y), new(cx + r, y)); }
        _ = major;
    }

    /// <summary>Scale bar (bottom left) with end ticks and half-way tick, plus the grid spacing.</summary>
    private void DrawScaleBar(DrawingContext ctx, bool dark)
    {
        var barMm = NiceStepMm(90); var w = barMm * Scale; var brush = dark ? Brushes.Gainsboro : Brushes.DimGray; var pen = new Pen(brush, 1.5);
        double x = 14, y = Bounds.Height - 22;
        ctx.DrawLine(pen, new(x, y), new(x + w, y));
        for (var i = 0; i <= 4; i++) { var tx = x + w * i / 4; ctx.DrawLine(pen, new(tx, y), new(tx, y - (i % 2 == 0 ? 8 : 4))); }
        ctx.DrawText(Txt("0", 11, brush), new Point(x - 3, y + 2));
        var end = Txt(Length(barMm), 11, brush); ctx.DrawText(end, new Point(x + w - end.Width / 2, y + 2));
        var grid = Txt(T(Vm?.SnapToGrid == true ? "plan.grid.snap" : "plan.grid", Length(NiceStepMm(10))), 11, brush); ctx.DrawText(grid, new Point(x + w + 18, y - 6));
    }

    private void DrawWalls(DrawingContext ctx, Level l, IBrush fill, IPen? pen, (int X, int Y)? shift, IReadOnlySet<Guid>? selected = null, IBrush? selFill = null, bool onlySelected = false)
    {
        if (Vm?.WallDisplay == WallDisplay.Hidden) return;
        if (Vm?.WallDisplay == WallDisplay.Wireframe) { fill = Brushes.Transparent; pen ??= new Pen(Brushes.DimGray, 1.2); selFill = null; }
        foreach (var w in l.Walls)
        {
            var isSel = selected?.Contains(w.Id) == true;
            if (onlySelected && !isSel) continue;
            if (w.Footprint.Count < 3) continue;
            if (Vm?.CommonOnly == true && w.Stability == Stability.Private) continue;                       // "Common parts only"
            var (dx, dy) = shift ?? (0, 0);
            Point S(PointMm p) => ToScreen(new PointMm(p.X + dx, p.Y + dy));
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.SetFillRule(FillRule.EvenOdd);
                c.BeginFigure(S(w.Footprint[0]), true);
                foreach (var p in w.Footprint.Skip(1)) c.LineTo(S(p));
                c.EndFigure(true);
                foreach (var hole in w.Holes.Where(h => h.Count >= 3))
                { c.BeginFigure(S(hole[0]), true); foreach (var p in hole.Skip(1)) c.LineTo(S(p)); c.EndFigure(true); }
            }
            ctx.DrawGeometry(isSel && selFill != null ? selFill : fill, isSel && (selFill != null || Vm?.WallDisplay == WallDisplay.Wireframe) ? new Pen(Brushes.OrangeRed, 1.5) : pen, g);
        }
    }
}
