using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmoCad.Core.Model;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

public partial class MainViewModel
{
    // ---- Reshaping detected walls: fuse fragments, cut a wall along a line ----
    public bool IsCutWallTool => Tool == ToolKind.CutWall;
    /// <summary>The cutting line (two clicks), then the side to remove (third click).</summary>
    public List<PointMm> CutPoints { get; } = new();

    /// <summary>Fuses the selected walls into one wall per connected piece (gaps of a few millimetres between fragments are closed). One history step.</summary>
    [RelayCommand] private void MergeSelectedWalls()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count < 2) { Status = T("merge.pick"); return; }
        var first = l.Walls.First(w => SelectedWalls.Contains(w.Id)).Id;
        var n = Actions.MergeWalls(l.Id, SelectedWalls.ToList());
        if (n == 0) { Status = T("merge.pick"); return; }
        SelectedWalls.Clear(); SelectedWalls.Add(first); State.Select(first); Status = T("merge.done", n); Refresh();
    }

    public bool IsMergeRegionTool => Tool == ToolKind.MergeRegion;

    /// <summary>Fuses every wall that reaches into the lassoed region (outline in model millimetres). One history step.</summary>
    public void MergeRegion(IReadOnlyList<PointMm> lasso)
    {
        if (CurrentLevel is not { } l || lasso.Count < 3) return;
        var ids = l.Walls.Where(w => ImmoCad.Core.Geometry.PolygonOps.Touches(new(w.Footprint, w.Holes), lasso)).Select(w => w.Id).ToList();
        SelectedWalls.Clear(); foreach (var id in ids) SelectedWalls.Add(id);
        MergeSelectedWalls();
    }

    /// <summary>Cut mode "split": the line cuts the walls in two or more pieces and nothing is removed.</summary>
    [ObservableProperty] private bool _cutSplit;
    partial void OnCutSplitChanged(bool value) { CutPoints.Clear(); CutAwaitSide = false; Status = T(value ? "cutwall.split.on" : "cutwall.split.off"); Changed?.Invoke(); }

    /// <summary>Select mode "polygon": clicks outline an area, Enter or a double-click closes it and selects what it touches.</summary>
    [ObservableProperty] private bool _selectPolygon;
    partial void OnSelectPolygonChanged(bool value) { Status = T(value ? "select.polygon.on" : "select.polygon.off"); Changed?.Invoke(); }

    /// <summary>Selects the walls and floor markings that reach into the polygon (added to the selection when <paramref name="add"/>).</summary>
    public void SelectInPolygon(IReadOnlyList<PointMm> poly, bool add)
    {
        if (CurrentLevel is not { } l || poly.Count < 3) return;
        var ids = l.Walls.Where(w => ImmoCad.Core.Geometry.PolygonOps.Touches(new(w.Footprint, w.Holes), poly)).Select(w => w.Id)
            .Concat(l.Markings.Where(m => m.Outline.Count >= 3 && ImmoCad.Core.Geometry.PolygonOps.Touches(new(m.Outline, m.Holes), poly)).Select(m => m.Id));
        SelectWalls(WallDisplay == WallDisplay.Hidden ? [] : ids, add);
    }

    /// <summary>The line is set (two points) and the next click names the side to remove.</summary>
    public bool CutAwaitSide { get; private set; }

    /// <summary>Points accumulate: two make a line (then click the side that goes), three or more make a zone whose content is removed.</summary>
    public void CutWallClick(PointMm p)
    {
        if (CurrentLevel is not { } l) return;
        if (CutAwaitSide)
        {
            var (a, b) = (CutPoints[0], CutPoints[1]);
            var side = (long)(b.X - a.X) * (p.Y - a.Y) - (long)(b.Y - a.Y) * (p.X - a.X); if (side == 0) { Status = T("cutwall.side"); return; }
            CutPoints.Clear(); CutAwaitSide = false;
            var n = Actions.CutWalls(l.Id, a, b, keepLeft: side < 0, SelectedWalls.Count > 0 ? SelectedWalls.ToList() : null);
            SelectedWalls.Clear(); Status = n > 0 ? T("cutwall.done", n) : T("cutwall.none"); Refresh(); return;
        }
        if (CutPoints.Count > 0 && Math.Abs(CutPoints[^1].X - p.X) + Math.Abs(CutPoints[^1].Y - p.Y) < 20) return;
        CutPoints.Add(p);
        if (CutSplit && CutPoints.Count == 2) { SplitAlongLine(l); return; }                // split mode: the second point ends the line, no side to choose
        Status = T(CutPoints.Count == 1 ? "cutwall.second" : "cutwall.more");
    }

    /// <summary>Escape: drops the last point (or the side choice, back to the line's second point). False when nothing was started.</summary>
    public bool CutWallBack()
    {
        if (CutPoints.Count == 0) return false;
        CutAwaitSide = false; CutPoints.RemoveAt(CutPoints.Count - 1);
        Status = T(CutPoints.Count == 0 ? "hint.cutwall" : CutPoints.Count == 1 ? "cutwall.second" : "cutwall.more"); Changed?.Invoke(); return true;
    }

    private void SplitAlongLine(ImmoCad.Core.Model.Level l)
    {
        var (a, b) = (CutPoints[0], CutPoints[1]); CutPoints.Clear();
        var n = Actions.SplitWalls(l.Id, a, b, SelectedWalls.Count > 0 ? SelectedWalls.ToList() : null);
        SelectedWalls.Clear(); Status = n > 0 ? T("cutwall.split.done", n) : T("cutwall.none"); Refresh();
    }

    /// <summary>Double-click or Enter: two points end the line (the side comes next), three or more close the zone and cut it out.</summary>
    public void CutWallFinish()
    {
        if (CurrentLevel is not { } l || CutAwaitSide) return;
        if (CutSplit) { if (CutPoints.Count == 2) SplitAlongLine(l); return; }
        if (CutPoints.Count == 2) { CutAwaitSide = true; Status = T("cutwall.side"); return; }
        if (CutPoints.Count < 3) return;
        var zone = CutPoints.ToList(); CutPoints.Clear();
        var n = Actions.CutWallsRegion(l.Id, zone, SelectedWalls.Count > 0 ? SelectedWalls.ToList() : null);
        SelectedWalls.Clear(); Status = n > 0 ? T("cutwall.done", n) : T("cutwall.none"); Refresh();
    }
}
