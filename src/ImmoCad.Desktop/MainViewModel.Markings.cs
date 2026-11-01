using CommunityToolkit.Mvvm.Input;
using ImmoCad.Core.Geometry;
using ImmoCad.Core.Model;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>
/// Floor markings: paint on the floor (parking bay lines, hatching) that comes from the scan as thin walls. The "Paint on the floor" tool takes a zone (rectangle or freehand),
/// previews what will change, and applies it with Enter: thin material becomes paint, paint goes back to walls, or paint is erased. Nothing changes before Enter.
/// </summary>
public partial class MainViewModel
{
    public bool IsPaintTool => Tool == ToolKind.PaintZone;
    /// <summary>Modes of the tool, in the order of <see cref="PaintMode"/>.</summary>
    public string[] PaintModes => [T("paint.mode.tomark"), T("paint.mode.towall"), T("paint.mode.erase")];

    private int _paintMode;
    public int PaintMode { get => _paintMode; set { if (_paintMode == value) return; _paintMode = value; OnPropertyChanged(); PaintRecompute(); } }
    /// <summary>Material thinner than this (mm) is what becomes paint.</summary>
    public int PaintMaxThicknessMm
    {
        get => Settings.PaintMaxThicknessMm;
        set { value = Math.Clamp(value, 20, 1000); if (value == Settings.PaintMaxThicknessMm) return; Settings.PaintMaxThicknessMm = value; OnPropertyChanged(); if (_paintMode == 0) PaintRecompute(); }
    }
    private bool _paintLasso;
    /// <summary>Freehand zone instead of a rectangle.</summary>
    public bool PaintLasso { get => _paintLasso; set { if (_paintLasso == value) return; _paintLasso = value; OnPropertyChanged(); } }

    public IReadOnlyList<PointMm>? PaintZone { get; private set; }
    /// <summary>What the zone would do (mode 0: the walls with thin parts) or touch (modes 1 and 2: the markings), shown in orange until applied.</summary>
    public List<WallSplit>? PaintSplits { get; private set; }
    public List<FloorMarking>? PaintTouched { get; private set; }
    public bool HasPaintPlan => (PaintSplits?.Count ?? 0) + (PaintTouched?.Count ?? 0) > 0;
    /// <summary>The preview being computed (tests await it).</summary>
    public Task PaintTask { get; private set; } = Task.CompletedTask;
    private CancellationTokenSource? _paintCts;

    public void PaintSetZone(IReadOnlyList<PointMm> zone) { PaintZone = zone; PaintRecompute(); }

    private void PaintRecompute()
    {
        _paintCts?.Cancel(); _paintCts = null;
        if (PaintZone is not { } zone || CurrentLevel is not { } l) { PaintSplits = null; PaintTouched = null; PaintChanged(); PaintTask = Task.CompletedTask; return; }
        var cts = _paintCts = new CancellationTokenSource(); var mode = _paintMode; var thick = PaintMaxThicknessMm; var levelId = l.Id;
        // the model is copied first: the preview runs off the UI thread while the user may go on editing
        var snap = new Level
        {
            Walls = l.Walls.Select(w => new Wall { Id = w.Id, Footprint = w.Footprint.ToList(), Holes = w.Holes.Select(h => h.ToList()).ToList() }).ToList(),
            Markings = l.Markings.Select(m => new FloorMarking { Id = m.Id, Outline = m.Outline.ToList(), Holes = m.Holes.Select(h => h.ToList()).ToList() }).ToList(),
        };
        PaintTask = PaintComputeAsync(snap, zone.ToList(), mode, thick, levelId, cts);
    }

    private async Task PaintComputeAsync(Level snap, List<PointMm> zone, int mode, int thick, Guid levelId, CancellationTokenSource cts)
    {
        try
        {
            var (splits, touched) = await Task.Run(() => mode == 0 ? (FloorMarkings.PlanPaint(snap, zone, thick, cts.Token), (List<FloorMarking>?)null) : (null, FloorMarkings.InZone(snap, zone)), cts.Token);
            if (cts.IsCancellationRequested || CurrentLevelId != levelId) return;
            PaintSplits = splits; PaintTouched = touched; var n = (splits?.Sum(s => s.Thin.Count) ?? 0) + (touched?.Count ?? 0);
            Status = n == 0 ? T("paint.preview.none") : T(mode == 0 ? "paint.preview" : mode == 1 ? "paint.preview.towall" : "paint.preview.erase", n); PaintChanged();
        }
        catch (OperationCanceledException) { }
    }

    private void PaintChanged() { OnPropertyChanged(nameof(HasPaintPlan)); PaintApplyCommand.NotifyCanExecuteChanged(); Changed?.Invoke(); }

    /// <summary>Forgets the zone and its preview.</summary>
    public void PaintClear() { _paintCts?.Cancel(); _paintCts = null; PaintZone = null; PaintSplits = null; PaintTouched = null; PaintTask = Task.CompletedTask; OnPropertyChanged(nameof(HasPaintPlan)); PaintApplyCommand?.NotifyCanExecuteChanged(); }

    [RelayCommand] private void PaintCancel() { PaintClear(); Changed?.Invoke(); }

    [RelayCommand(CanExecute = nameof(HasPaintPlan))]
    private void PaintApply()
    {
        if (CurrentLevel is not { } l || !HasPaintPlan) return; int n;
        switch (_paintMode)
        {
            case 0: n = Actions.PaintMarkings(l.Id, PaintSplits!); break;
            case 1: n = Actions.MarkingsToWalls(l.Id, PaintTouched!.Select(m => m.Id).ToList()); break;
            default: n = Actions.EraseMarkings(l.Id, PaintTouched!.Select(m => m.Id).ToList()); break;
        }
        PaintClear(); SelectedWalls.Clear(); Status = T(_paintMode == 0 ? "paint.done" : _paintMode == 1 ? "paint.done.towall" : "paint.done.erase", n); Refresh();
    }

    /// <summary>The selected walls become floor markings (whole walls). One history step.</summary>
    [RelayCommand]
    private void ConvertSelectionToMarkings()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) return;
        var n = Actions.ConvertWallsToMarkings(l.Id, SelectedWalls.ToList()); SelectedWalls.Clear(); Status = T("paint.converted", n); Refresh();
    }

    /// <summary>The selected floor markings become walls again (the way back when the paint was a mistake). One history step.</summary>
    [RelayCommand]
    private void ConvertSelectionToWalls()
    {
        if (CurrentLevel is not { } l) return;
        var ids = l.Markings.Where(m => SelectedWalls.Contains(m.Id)).Select(m => m.Id).ToList(); if (ids.Count == 0) return;
        var n = Actions.MarkingsToWalls(l.Id, ids); SelectedWalls.Clear(); Status = T("paint.done.towall", n); Refresh();
    }

    // ---- Show in plan / Show in 3D: frame what is selected in the other view ----

    /// <summary>The middle of the selection on the plan (mm) and the level it is on: a wall or a painted line, a pipe, an equipment, a room, an opening.</summary>
    public (PointMm At, Level Level)? SelectionCenter()
    {
        if (CurrentLevel is not { } cur) return null;
        if (SelectedEquipment is { } eq) return (eq.Position, Building.Levels.FirstOrDefault(l => l.Id == eq.LevelId) ?? cur);
        if (SelectedSegment is { } sg && Building.Networks.FirstOrDefault(n => n.Id == sg.NetworkId) is { } net && net.Segments.FirstOrDefault(s => s.Id == sg.SegmentId) is { } seg
            && net.Nodes.FirstOrDefault(n => n.Id == seg.A) is { } a && net.Nodes.FirstOrDefault(n => n.Id == seg.B) is { } b)
        {
            var z = (a.Position.Z + b.Position.Z) / 2; var lvl = Building.Levels.FirstOrDefault(l => z >= l.AltitudeMm && z < l.AltitudeMm + l.HeightMm) ?? cur;
            return (new PointMm((a.Position.X + b.Position.X) / 2, (a.Position.Y + b.Position.Y) / 2), lvl);
        }
        if (SelectedSpaceModel is { } sp && sp.Outline.Count > 0) return (new PointMm((sp.Outline.Min(p => p.X) + sp.Outline.Max(p => p.X)) / 2, (sp.Outline.Min(p => p.Y) + sp.Outline.Max(p => p.Y)) / 2), cur);
        if (SelectedOpening is { } so && cur.Walls.FirstOrDefault(w => w.Id == so.WallId) is { } sw && sw.Openings.FirstOrDefault(o => o.Id == so.OpeningId) is { } op && WallGeometry.SpanOf(sw, op, exactThickness: true) is { } span) return (span.Center, cur);
        var pts = cur.Walls.Where(w => SelectedWalls.Contains(w.Id)).SelectMany(w => w.Footprint).Concat(cur.Markings.Where(m => SelectedWalls.Contains(m.Id)).SelectMany(m => m.Outline)).ToList();
        return pts.Count == 0 ? null : (new PointMm((pts.Min(p => p.X) + pts.Max(p => p.X)) / 2, (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2), cur);
    }

    /// <summary>Leaves the walk (when walking) and makes the plan show the selection, on its level.</summary>
    [RelayCommand]
    private void ShowSelectionInPlan()
    {
        if (SelectionCenter() is not { } c) return;
        if (IsWalk) EndWalkRequest(null); if (c.Level.Id != CurrentLevelId) { CurrentLevelId = c.Level.Id; Refresh(); }       // the level changes, the selection stays
        Mode = ViewMode.Plan; FocusRequested?.Invoke(c.At);
    }

    /// <summary>The same for the 3D view: the plan is centred on the selection first, so the animated transition to 3D lands on it.</summary>
    [RelayCommand]
    private void ShowSelectionIn3D()
    {
        if (SelectionCenter() is not { } c) return;
        if (IsWalk) EndWalkRequest(null); if (c.Level.Id != CurrentLevelId) { CurrentLevelId = c.Level.Id; Refresh(); }       // the level changes, the selection stays
        if (Mode != ViewMode.Plan && !Is3D) Mode = ViewMode.Plan;
        FocusRequested?.Invoke(c.At); if (Mode == ViewMode.Plan) RequestMode(ViewMode.Solid);
    }
}
