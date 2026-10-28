using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Tools;

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

    public PlanView() { Focusable = true; ClipToBounds = true; }

    public void Invalidate() { if (Vm?.IsDrawTool == true) { if (DrawTool == null) DrawTool = new DrawChainTool(Vm.CommitWalls); } else DrawTool = null; InvalidateVisual(); }

    public PointMm ToMm(Point p) => new((int)Math.Round((p.X - Offset.X) / Scale), (int)Math.Round((Bounds.Height - p.Y - Offset.Y) / Scale));
    private Point ToScreen(PointMm p) => new(Offset.X + p.X * Scale, Bounds.Height - Offset.Y - p.Y * Scale);

    public void FitAll()
    {
        var pts = Vm?.Building.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).ToList();
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
        if (props.IsRightButtonPressed) { DrawTool?.Cancel(); _gesture = Gesture.None; InvalidateVisual(); return; }
        if (vm.IsAlignTool) { var m = ToMm(p); vm.AlignClick(m.X, m.Y); InvalidateVisual(); return; }
        if (vm.IsDrawTool) { DrawTool ??= new DrawChainTool(vm.CommitWalls); DrawTool.Down(TP(e)); e.Pointer.Capture(this); InvalidateVisual(); return; }

        _start = _now = p; _startMm = ToMm(p); _delta = (0, 0); _addToSelection = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
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
        if (vm.IsDrawTool) { DrawTool?.Move(TP(e)); InvalidateVisual(); return; }
        if (_gesture == Gesture.None) return;
        _now = p;
        if (_gesture == Gesture.Move)
        {
            var d = ToMm(p); var dx = d.X - _startMm.X; var dy = d.Y - _startMm.Y;
            var g = Snapper.GridMm; _delta = (Snap(dx, g), Snap(dy, g));  // moves in grid steps
        }
        InvalidateVisual();
    }

    private static int Snap(int v, int g) => g <= 0 ? v : (int)Math.Round((double)v / g) * g;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _pan = null; e.Pointer.Capture(null);
        if (Vm is not { } vm) return;
        if (vm.IsDrawTool) { DrawTool?.Up(TP(e)); InvalidateVisual(); return; }
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
        if (vm.IsAlignTool)
        {
            if (e.Key == Key.Enter) vm.AlignApply(); else if (e.Key == Key.Back) vm.AlignUndoPair(); else if (e.Key == Key.Escape) { vm.AlignCancel(); vm.SetToolCommand.Execute("Select"); } else return;
            InvalidateVisual(); e.Handled = true; return;
        }
        if (DrawTool != null && vm.IsDrawTool)
        {
            if (e.Key == Key.Enter) DrawTool.Finish(); else if (e.Key == Key.Back) DrawTool.Backspace(); else if (e.Key == Key.Escape) { DrawTool.Cancel(); vm.SetToolCommand.Execute("Select"); }
            InvalidateVisual(); return;
        }
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

    public override void Render(DrawingContext ctx)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        ctx.FillRectangle(new SolidColorBrush(dark ? Color.FromRgb(30, 32, 36) : Color.FromRgb(250, 250, 248)), new Rect(Bounds.Size));
        if (!_fitted) FitAll();
        DrawGrid(ctx, dark);
        if (Vm?.CurrentLevel is not { } l) return;
        if (l.BackgroundVisible && l.ImageTransform is { Length: 6 } tr && Background(l) is { } bg)
        {
            // pixels → mm (affine) → screen; axis-aligned or rotated drawing through a matrix
            var m = new Matrix(tr[0] * Scale, -tr[3] * Scale, tr[1] * Scale, -tr[4] * Scale, Offset.X + tr[2] * Scale, Bounds.Height - Offset.Y - tr[5] * Scale);
            using (ctx.PushTransform(m)) using (ctx.PushOpacity(l.BackgroundOpacity))
                ctx.DrawImage(bg, new Rect(bg.PixelSize.ToSize(1)));
        }
        var below = Vm.Building.Levels.LastOrDefault(x => x.AltitudeMm < l.AltitudeMm);
        if (below != null) DrawWalls(ctx, below, new SolidColorBrush(Color.FromArgb(55, 60, 140, 255)), null, null);
        var wallFill = new SolidColorBrush(dark ? Color.FromRgb(190, 192, 198) : Color.FromRgb(70, 72, 78));
        var sel = new SolidColorBrush(Color.FromRgb(255, 140, 0));
        DrawWalls(ctx, l, wallFill, new Pen(new SolidColorBrush(dark ? Colors.Black : Colors.Black), 0.8), null, Vm.SelectedWalls, sel);
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
                ctx.DrawText(new FormattedText($"{len:0.00} m", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.OrangeRed), ToScreen(pv) + new Point(8, -16));
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
        var label = new FormattedText($"{l.Name}  ·  {l.AltitudeMm / 1000.0:0.0} m", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, dark ? Brushes.Gainsboro : Brushes.DimGray);
        ctx.DrawText(label, new Point(12, 8));
    }

    private void DrawGrid(DrawingContext ctx, bool dark)
    {
        var step = Scale * 1000; if (step < 8) return;
        var minor = new Pen(new SolidColorBrush(dark ? Color.FromRgb(44, 47, 52) : Color.FromRgb(236, 236, 232)), 1);
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
