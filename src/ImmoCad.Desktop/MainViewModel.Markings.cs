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
}
