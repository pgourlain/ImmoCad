using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Networks;
using ImmeubleCad.Core.Catalog;
using ImmeubleCad.Core.Tools;
using ImmeubleCad.Core.Geometry;
using ImmeubleCad.Core.IO;
using static ImmeubleCad.Core.Translations;

namespace ImmeubleCad.Desktop;

/// <summary>
/// 2D plan. Select tool: click a wall, Shift+click to add, drag on empty space = box select, drag a selected wall = move
/// (one history step on release), arrows nudge. Draw tool: hybrid click/drag chain. Wheel zooms, middle button pans.
/// </summary>
public class PlanView : Control
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    public double Scale { get; set; } = 0.1;                    // px per mm
    public (double X, double Y) Offset { get; set; } = (50, 0); // screen px of the plan origin (Y flipped)
    public DrawChainTool? DrawTool { get; private set; }
    public Snapper Snapper { get; } = new();
    public event Action<PointMm>? CursorMoved;

    private Point? _pan;
    private bool _fitted;
    private enum Gesture { None, Box, Move }
    private Gesture _gesture; private Point _start; private Point _now; private PointMm _startMm; private (int X, int Y) _delta;
    private bool _addToSelection;

    private readonly Dictionary<string, Bitmap> _bitmaps = new();
    private Bitmap? Background(Level l)
    {
        if (l.BackgroundImage is not { } n || Vm?.Images.TryGetValue(n, out var data) != true) return null;
        if (!_bitmaps.TryGetValue(n, out var bmp)) _bitmaps[n] = bmp = new Bitmap(new MemoryStream(data!));
        return bmp;
    }

    public PlanView() { Focusable = true; ClipToBounds = true; DataContextChanged += (_, _) => { if (Vm != null) { Vm.FocusRequested += CenterOn; Vm.ViewFitRequested += RefitNextRender; } }; }

    private ToolKind? _chainKind;
    private bool IsChain => Vm is { IsDrawTool: true } or { IsPipeTool: true };
    public void RefitNextRender() => _fitted = false;
    public void Invalidate()
    {
        if (Vm is { IsDrawTool: true }) { if (_chainKind != ToolKind.DrawWall) { DrawTool = new DrawChainTool(Vm.CommitWalls); _chainKind = ToolKind.DrawWall; } }
        else if (Vm is { IsPipeTool: true }) { if (_chainKind != ToolKind.DrawPipe) { DrawTool = new DrawChainTool(Vm.CommitPipe); _chainKind = ToolKind.DrawPipe; } }
        else { DrawTool = null; _chainKind = null; }
        InvalidateVisual();
    }
    private readonly List<(Rect Rect, Guid LevelId, PointMm Center)> _arrows = new();
    private PointMm _hover;
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

    private ToolPointer HandlePointer(PointerEventArgs e) { var p = e.GetPosition(this); return new ToolPointer(ToMm(p), p.X, p.Y); }

    public PointMm ToMm(Point p) => new((int)Math.Round((p.X - Offset.X) / Scale), (int)Math.Round((Bounds.Height - p.Y - Offset.Y) / Scale));
    internal Point ToScreenForTest(PointMm p) => ToScreen(p);
    private Point ToScreen(PointMm p) => new(Offset.X + p.X * Scale, Bounds.Height - Offset.Y - p.Y * Scale);

    public void FitAll()
    {
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

    private IEnumerable<IReadOnlyList<PointMm>> Polys() => Vm?.CurrentLevel?.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint) ?? [];

    private Wall? HitWall(PointMm p) => Vm?.CurrentLevel?.Walls.LastOrDefault(w =>
        ImmeubleCad.Core.Geometry.SpaceFinder.Contains(w.Footprint, p) && !w.Holes.Any(h => ImmeubleCad.Core.Geometry.SpaceFinder.Contains(h, p)));

    private ToolPointer TP(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var prev = DrawTool?.Points.Count > 0 ? DrawTool.Points[^1] : (PointMm?)null;
        return new ToolPointer(Snapper.Snap(ToMm(p), Polys(), prev).Point, p.X, p.Y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties; var p = e.GetPosition(this);
        if (props.IsMiddleButtonPressed) { _pan = p; return; }
        if (Vm is not { } vm) return;
        if (props.IsRightButtonPressed)
        {
            if (IsChain && DrawTool is { Points.Count: > 0 } dt) { ShowChainMenu(dt); return; }
            DrawTool?.Cancel(); _gesture = Gesture.None; InvalidateVisual(); return;
        }
        if (vm.IsAlignTool) { var m = ToMm(p); vm.AlignClick(m.X, m.Y); InvalidateVisual(); return; }
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
        if (vm.IsDoorTool) { vm.PlaceOpening(ToMm(p), OpeningKind.Door); return; }
        if (vm.IsWindowTool) { vm.PlaceOpening(ToMm(p), OpeningKind.Window); return; }
        if (vm.IsSpaceTool) { vm.PlaceSpace(ToMm(p)); return; }
        if (vm.IsNumberTool) { vm.NumberClick(ToMm(p)); return; }
        if (vm.IsMeasureTool) { vm.MeasureClick(Snapper.Snap(ToMm(p), Polys()).Point); InvalidateVisual(); return; }
        if (vm.IsRiserTool) { vm.PlaceRiser(ToMm(p)); return; }
        if (vm.IsPlaceTool) { vm.PlaceFromPalette(ToMm(p)); return; }
        foreach (var (rect, lvl, c) in _arrows)
            if (rect.Contains(p)) { vm.SelectLevelCommand.Execute(lvl); CenterOn(c); return; }

        // equipment: port handle starts Connect, body selects/moves
        if (vm.SelectedEquipment is { } se && EquipmentCatalog.Find(se.CatalogId)?.With(se.Parameters) is { } sc && vm.CurrentLevel is { } sl)
            foreach (var (port, at) in Symbol.PortPositions(sc, se.Position, sl.AltitudeMm + se.ElevationMm, se.RotationDeg))
            { var sp = ToScreen(new PointMm(at.X, at.Y)); if (Math.Sqrt((sp.X - p.X) * (sp.X - p.X) + (sp.Y - p.Y) * (sp.Y - p.Y)) <= 9) { _connect = (se.Id, port, at, ToMm(p)); e.Pointer.Capture(this); return; } }
        if (HitEquipment(ToMm(p)) is { } heq) { vm.SelectEquipment(heq.Id); _eqDrag = (heq.Id, heq.Position, ToMm(p)); e.Pointer.Capture(this); InvalidateVisual(); return; }
        // pipe handles and pipe bodies come before walls
        if (_handleTool != null && _handleTool.State == ToolState.Placing) { _handleTool.ForceElbow = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt); _handleTool.Down(HandlePointer(e)); FinishHandle(vm); return; }
        if (HitHandle(p) is { } h) { _handleTool = new PipeHandleTool(vm.Actions, h.Seg.Network.Id, h.Seg.Segment.Id, h.Handle, vm.Snapper_GridMm()) { ForceElbow = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt) }; _typed = ""; _handleTool.Down(HandlePointer(e)); e.Pointer.Capture(this); InvalidateVisual(); return; }
        if (HitPipe(p) is { } pipe)
        {
            vm.SelectSegment(pipe.Network.Id, pipe.Segment.Id);
            _handleTool = new PipeHandleTool(vm.Actions, pipe.Network.Id, pipe.Segment.Id, PipeHandle.Body, vm.Snapper_GridMm()); _typed = ""; _handleTool.Down(HandlePointer(e)); e.Pointer.Capture(this); InvalidateVisual(); return;
        }
        vm.ClearSegment(); if (vm.SelectedEquipmentId != null) vm.SelectEquipment(null);
        _start = _now = p;
        if (vm.HitOpening(ToMm(p)) is { } oh) { _openingDrag = oh; e.Pointer.Capture(this); return; } _startMm = ToMm(p); _delta = (0, 0); _addToSelection = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var hit = HitWall(_startMm);
        if (hit != null && vm.SelectedWalls.Contains(hit.Id) && !_addToSelection) _gesture = Gesture.Move;       // drag the group
        else if (hit != null) { vm.SelectWalls([hit.Id], _addToSelection); _gesture = Gesture.Move; }            // select then drag
        else { if (!_addToSelection) vm.SelectWalls([], false); _gesture = Gesture.Box; }
        e.Pointer.Capture(this); InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_pan is { } l) { Offset = (Offset.X + p.X - l.X, Offset.Y - (p.Y - l.Y)); _pan = p; InvalidateVisual(); return; }
        CursorMoved?.Invoke(ToMm(p));
        if (Vm is not { } vm) return;
        _hover = ToMm(p);
        if (_handleTool != null) { _handleTool.ForceElbow = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt); _handleTool.Move(HandlePointer(e)); InvalidateVisual(); return; }
        if (IsChain) { DrawTool?.Move(TP(e)); InvalidateVisual(); return; }
        if (vm.IsPlaceTool || vm.IsRiserTool) { InvalidateVisual(); return; }
        if (_connect is { } cn0) { _connect = (cn0.EqId, cn0.Port, cn0.At, ToMm(p)); InvalidateVisual(); return; }
        if (_eqDrag is { } ed0) { var m = ToMm(p); var d = (X: m.X - ed0.From.X, Y: m.Y - ed0.From.Y); var orig = Vm.Building.Equipment.First(q => q.Id == ed0.Id).Position; _eqDrag = (ed0.Id, new PointMm(Snap(orig.X + d.X, Snapper.GridMm), Snap(orig.Y + d.Y, Snapper.GridMm)), ed0.From); InvalidateVisual(); return; }
        if (_stretch is { } sh && Vm.BackgroundSize() is { } szm) { var mm = ToMm(p); _stretchLive = ImageStretch.Drag(_stretchStart, szm.W, szm.H, sh, _stretchFrom, (mm.X, mm.Y), keepRatio: e.KeyModifiers.HasFlag(KeyModifiers.Shift)); InvalidateVisual(); return; }
        if (_openingDrag != null) { _now = p; InvalidateVisual(); return; }
        if (vm.IsMeasureTool || vm.IsNumberTool) { InvalidateVisual(); }
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
        _pan = null; e.Pointer.Capture(null);
        if (Vm is not { } vm) return;
        if (IsChain) { DrawTool?.Up(TP(e)); InvalidateVisual(); return; }
        if (_handleTool != null) { _handleTool.Up(HandlePointer(e)); if (_handleTool.State == ToolState.Committed) FinishHandle(vm); else if (_handleTool.State == ToolState.Placing && _handleTool.Handle == PipeHandle.Body) { _handleTool.Cancel(); _handleTool = null; vm.AfterEdit(); } /* a plain click on the body only selects */ InvalidateVisual(); return; }
        if (_connect is { } cr) { _connect = null; vm.ConnectPort(cr.EqId, cr.Port, cr.At, ToMm(e.GetPosition(this))); InvalidateVisual(); return; }
        if (_eqDrag is { } er) { _eqDrag = null; var orig = vm.Building.Equipment.First(q => q.Id == er.Id).Position; if (er.To != orig) vm.MoveEquipmentTo(er.Id, er.To); InvalidateVisual(); return; }
        if (_stretch != null) { var live = _stretchLive; _stretch = null; _stretchLive = null; if (live is { } lt && lt != _stretchStart) vm.SetImageTransform(lt); InvalidateVisual(); return; }
        if (vm.IsRiserTool || vm.IsPlaceTool || vm.IsAlignTool || vm.IsDoorTool || vm.IsWindowTool || vm.IsSpaceTool || vm.IsNumberTool || vm.IsMeasureTool) return;
        if (_openingDrag is { } od) { var to = ToMm(e.GetPosition(this)); _openingDrag = null; if (Math.Abs(_now.X - _start.X) + Math.Abs(_now.Y - _start.Y) > 3) vm.MoveOpeningTo(od.Wall, od.Opening, to); InvalidateVisual(); return; }
        if (_gesture == Gesture.Move && Math.Abs(_now.X - _start.X) + Math.Abs(_now.Y - _start.Y) > 3) vm.MoveSelection(_delta.X, _delta.Y);
        else if (_gesture == Gesture.Box)
        {
            var a = ToMm(_start); var b = ToMm(_now); int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            if (vm.CurrentLevel is { } lv)
                vm.SelectWalls(lv.Walls.Where(w => w.Footprint.Any(q => q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1)).Select(w => w.Id), _addToSelection);
        }
        _gesture = Gesture.None; _delta = (0, 0); InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this); var before = ToMm(p);
        Scale = Math.Clamp(Scale * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.005, 5);
        Offset = (p.X - before.X * Scale, Bounds.Height - p.Y - before.Y * Scale); InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.IsStraightenTool && e.Key == Key.Enter) { vm.StraightenSelectionCommand.Execute(null); vm.SetToolCommand.Execute("Select"); InvalidateVisual(); e.Handled = true; return; }
        if (vm.IsNumberTool && e.Key == Key.Enter) { vm.NumberCommit(); InvalidateVisual(); e.Handled = true; return; }
        if (e.Key == Key.Escape && (vm.IsDoorTool || vm.IsWindowTool || vm.IsSpaceTool || vm.IsMeasureTool || vm.IsNumberTool || vm.IsPlaceTool || vm.IsRiserTool || vm.IsStraightenTool)) { vm.SetToolCommand.Execute("Select"); e.Handled = true; return; }
        if (vm.IsAlignTool)
        {
            if (e.Key == Key.Enter) vm.AlignApply(); else if (e.Key == Key.Back) vm.AlignUndoPair(); else if (e.Key == Key.Escape) { vm.AlignCancel(); vm.SetToolCommand.Execute("Select"); } else return;
            InvalidateVisual(); e.Handled = true; return;
        }
        if (DrawTool != null && IsChain)
        {
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
            ctx.DrawGeometry(new SolidColorBrush(pending ? Color.FromArgb(90, 255, 160, 0) : sp.Stability == Stability.Common ? Color.FromArgb(34, 80, 160, 255) : Color.FromArgb(22, 120, 200, 120)), null, g);
            var cx = (int)sp.Outline.Average(p => p.X); var cy = (int)sp.Outline.Average(p => p.Y); var cs = ToScreen(new PointMm(cx, cy));
            var label = sp.DisplayName; var ft = Txt(label, 12, dark ? Brushes.Gainsboro : Brushes.DimGray);
            ctx.DrawText(ft, new Point(cs.X - ft.Width / 2, cs.Y - ft.Height / 2));
        }
    }

    private void DrawOpenings(DrawingContext ctx, Level l, bool dark)
    {
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
        var c = new Point(Bounds.Width - 46, 56); var r = -site.RotationDeg * Math.PI / 180;     // the model's Y axis points site.RotationDeg° from north: the arrow turns the other way
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), new Pen(Brushes.Gray, 1), c, 22, 22);
        Point Rot(double x, double y) => new(c.X + x * Math.Cos(r) - y * Math.Sin(r), c.Y + x * Math.Sin(r) + y * Math.Cos(r));
        var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(Rot(0, -17), true); cx.LineTo(Rot(7, 9)); cx.LineTo(Rot(0, 4)); cx.LineTo(Rot(-7, 9)); cx.EndFigure(true); }
        ctx.DrawGeometry(Brushes.Black, null, g); ctx.DrawText(Txt("N", 11, Brushes.Black), Rot(-4, -33));
    }

    private void DrawMeasure(DrawingContext ctx)
    {
        if (Vm is not { IsMeasureTool: true } vm || vm.MeasurePoints.Count == 0) return;
        var a = ToScreen(vm.MeasurePoints[0]); var b = vm.MeasurePoints.Count == 2 ? ToScreen(vm.MeasurePoints[1]) : ToScreen(_hover);
        ctx.DrawLine(new Pen(Brushes.DeepPink, 2, DashStyle.Dash), a, b); ctx.DrawEllipse(Brushes.DeepPink, null, a, 4, 4); ctx.DrawEllipse(Brushes.DeepPink, null, b, 4, 4);
        var end = vm.MeasurePoints.Count == 2 ? vm.MeasurePoints[1] : _hover; var d = Math.Sqrt(Math.Pow(end.X - vm.MeasurePoints[0].X, 2) + Math.Pow(end.Y - vm.MeasurePoints[0].Y, 2)) / 1000;
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
            var c = NetworkQueries.ColorOf(s.Network.Type); var w = Math.Max(2.0, s.Segment.DiameterMm * Scale * 1.2);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), w, lineCap: PenLineCap.Round), ToScreen(new PointMm(s.A.X, s.A.Y)), ToScreen(new PointMm(s.B.X, s.B.Y)));
        }
        foreach (var n in b.Networks.Where(n => n.Visible))
            foreach (var node in n.Nodes.Where(n2 => n2.Component != null && n2.Position.Z >= l.AltitudeMm && n2.Position.Z < l.AltitudeMm + l.HeightMm))
            {
                var p = ToScreen(new PointMm(node.Position.X, node.Position.Y));
                ctx.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1.5), p, 6, 6); ctx.DrawLine(new Pen(Brushes.Black, 1.5), p + new Point(-4, -4), p + new Point(4, 4)); ctx.DrawLine(new Pen(Brushes.Black, 1.5), p + new Point(-4, 4), p + new Point(4, -4));
            }
        foreach (var r in NetworkQueries.RisersAt(b, l).Where(x => x.Network.Visible))
        {
            var c = NetworkQueries.ColorOf(r.Network.Type); var p = ToScreen(new PointMm(r.A.X, r.A.Y)); var rad = Math.Max(5, r.Segment.DiameterMm * Scale);
            ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), new Pen(Brushes.Black, 1), p, rad, rad);
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
        // placement ghost: green when valid, red with a hint when not
        if ((Vm.IsPlaceTool || Vm.IsRiserTool) && Bounds.Contains(ToScreen(_hover)))
        {
            var hint = Vm.IsPlaceTool ? Vm.PlacementHint(_hover) : null; var ok = hint == null;
            var col = ok ? Colors.LimeGreen : Colors.Red; var p = ToScreen(_hover);
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, col.R, col.G, col.B)), new Pen(new SolidColorBrush(col), 2), p, 10, 10);
            if (!ok) ctx.DrawText(new FormattedText(hint!, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.Red), p + new Point(14, -8));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        ctx.FillRectangle(Tokens.Brush(this, "PlanBackground", dark ? Color.FromRgb(30, 32, 36) : Color.FromRgb(250, 250, 248)), new Rect(Bounds.Size));
        if (!_fitted) FitAll();
        DrawGrid(ctx, dark);
        if (Vm?.CurrentLevel is not { } l) return;
        if (l.BackgroundVisible && l.ImageTransform is { Length: 6 } tr0 && Background(l) is { } bg)
        {
            var tr = _stretchLive is { } live ? live.ToArray() : tr0;
            // pixels → mm (affine) → screen; axis-aligned or rotated drawing through a matrix
            var m = new Matrix(tr[0] * Scale, -tr[3] * Scale, tr[1] * Scale, -tr[4] * Scale, Offset.X + tr[2] * Scale, Bounds.Height - Offset.Y - tr[5] * Scale);
            using (ctx.PushTransform(m)) using (ctx.PushOpacity(l.BackgroundOpacity))
                ctx.DrawImage(bg, new Rect(bg.PixelSize.ToSize(1)));
            if (Vm.IsStretchTool && Vm.BackgroundSize() is { } bs)
            {
                var a = ImageStretch.FromArray(tr);
                var cs = ImageStretch.Corners(a, bs.W, bs.H).Select(c => ToScreen(new PointMm((int)c.X, (int)c.Y))).ToArray();
                ctx.DrawGeometry(null, new Pen(Brushes.DodgerBlue, 1.5, DashStyle.Dash), Poly(cs));
                foreach (var h in Enum.GetValues<StretchHandle>().Where(h => h != StretchHandle.Move)) { var hp = ImageStretch.HandlePoint(a, bs.W, bs.H, h); var sp = ToScreen(new PointMm((int)hp.X, (int)hp.Y)); ctx.DrawRectangle(h == StretchHandle.Rotate ? Brushes.Orange : Brushes.White, new Pen(Brushes.DodgerBlue, 2), new Rect(sp.X - 5, sp.Y - 5, 10, 10)); }
            }
        }
        var below = Vm.Building.Levels.LastOrDefault(x => x.AltitudeMm < l.AltitudeMm);
        if (below?.ImageTransform is { Length: 6 } bt && below.BackgroundVisible && Background(below) is { } bbg && (Vm.BgDifference || Vm.IsAlignTool || Vm.IsStretchTool))
        {   // the level below's picture in a contrasting colour (blue tint), or as a difference blend, to see how well the two plans overlap
            var bm = new Matrix(bt[0] * Scale, -bt[3] * Scale, bt[1] * Scale, -bt[4] * Scale, Offset.X + bt[2] * Scale, Bounds.Height - Offset.Y - bt[5] * Scale);
            using (ctx.PushTransform(bm)) using (ctx.PushOpacity(Vm.BgDifference ? 1.0 : 0.35))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapBlendingMode = Vm.BgDifference ? BitmapBlendingMode.Difference : BitmapBlendingMode.SourceOver }))
                ctx.DrawImage(bbg, new Rect(bbg.PixelSize.ToSize(1)));
        }
        if (below != null && Vm.GhostBelow) DrawWalls(ctx, below, new SolidColorBrush(Color.FromArgb(55, Tokens.Get(this, "GhostBelow", Color.FromRgb(60, 140, 255)).R, Tokens.Get(this, "GhostBelow", Color.FromRgb(60, 140, 255)).G, Tokens.Get(this, "GhostBelow", Color.FromRgb(60, 140, 255)).B)), null, null);
        var above = Vm.Building.Levels.FirstOrDefault(x => x.AltitudeMm > l.AltitudeMm);
        if (above != null && Vm.GhostAbove) DrawWalls(ctx, above, new SolidColorBrush(Color.FromArgb(45, 255, 150, 40)), null, null);
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
        DrawMeasure(ctx); DrawStraightenPreview(ctx);
        if (_gesture == Gesture.Move && (_delta.X != 0 || _delta.Y != 0))   // ghost of the move
            DrawWalls(ctx, l, new SolidColorBrush(Color.FromArgb(110, 255, 140, 0)), new Pen(Brushes.OrangeRed, 1.2, DashStyle.Dash), _delta, Vm.SelectedWalls, null, onlySelected: true);
        if (_gesture == Gesture.Box)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 120, 255)), new Pen(Brushes.DodgerBlue, 1, DashStyle.Dash), new Rect(_start, _now));
        if (DrawTool is { Points.Count: > 0 } t)
        {
            var pen = new Pen(Brushes.OrangeRed, 2);
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
    }

    private void DrawGrid(DrawingContext ctx, bool dark)
    {
        var step = Scale * 1000; if (step < 8) return;
        var minor = new Pen(Tokens.Brush(this, "PlanGrid", dark ? Color.FromRgb(44, 47, 52) : Color.FromRgb(236, 236, 232)), 1);
        for (var x = Offset.X % step; x < Bounds.Width; x += step) ctx.DrawLine(minor, new(x, 0), new(x, Bounds.Height));
        for (var y = (Bounds.Height - Offset.Y) % step; y < Bounds.Height; y += step) ctx.DrawLine(minor, new(0, y), new(Bounds.Width, y));
    }

    private void DrawWalls(DrawingContext ctx, Level l, IBrush fill, IPen? pen, (int X, int Y)? shift, IReadOnlySet<Guid>? selected = null, IBrush? selFill = null, bool onlySelected = false)
    {
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
            ctx.DrawGeometry(isSel && selFill != null ? selFill : fill, isSel && selFill != null ? new Pen(Brushes.OrangeRed, 1.5) : pen, g);
        }
    }
}
