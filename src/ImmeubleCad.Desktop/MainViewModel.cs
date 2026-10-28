using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmeubleCad.Core;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Import;
using ImmeubleCad.Core.IO;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Desktop;

public enum ViewMode { Plan, Solid, Wireframe }
public enum ToolKind { Select, DrawWall }

/// <summary>One button of the level bar.</summary>
public sealed record LevelItem(Guid Id, string Name, string Altitude, bool IsCurrent, int WallCount);

public partial class MainViewModel : ObservableObject
{
    public Building Building { get; private set; } = new();
    public BuildingHistory History { get; private set; } = new();
    public BuildingActions Actions { get; private set; }
    public ObservableCollection<LevelItem> Levels { get; } = new();
    public HashSet<Guid> SelectedWalls { get; } = new();
    /// <summary>Background pictures by name (stored under images/ in the .immeuble file).</summary>
    public Dictionary<string, byte[]> Images { get; private set; } = new();

    [ObservableProperty] private Guid _currentLevelId;
    [ObservableProperty] private ViewMode _mode = ViewMode.Plan;
    [ObservableProperty] private ToolKind _tool = ToolKind.Select;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private string _hint = "Cliquez un mur pour le sélectionner, glissez pour déplacer.";
    public int WallThicknessMm { get; set; } = 200;
    public string? FilePath { get; private set; }
    public event Action? Changed;

    /// <summary>Always resolved by id: undo/redo rebuilds the whole object graph, so references go stale.</summary>
    public Level? CurrentLevel => Building.Levels.FirstOrDefault(l => l.Id == CurrentLevelId) ?? Building.Levels.FirstOrDefault();
    public bool Is3D => Mode != ViewMode.Plan;
    public bool IsSelectTool => Tool == ToolKind.Select;
    public bool IsDrawTool => Tool == ToolKind.DrawWall;
    public string SelectionText => SelectedWalls.Count == 0 ? "Aucune sélection" : $"{SelectedWalls.Count} mur(s) sélectionné(s)";

    public MainViewModel() { Actions = null!; Load(SampleBuilding.Create()); }

    private void Load((Building B, BuildingHistory H) s)
    {
        Building = s.B; History = s.H; Actions = new BuildingActions(Building, History, new BuildingEventsService());
        SelectedWalls.Clear(); CurrentLevelId = Building.Levels.FirstOrDefault()?.Id ?? Guid.Empty; Refresh();
    }

    partial void OnModeChanged(ViewMode value) => OnPropertyChanged(nameof(Is3D));
    partial void OnToolChanged(ToolKind value)
    {
        OnPropertyChanged(nameof(IsSelectTool)); OnPropertyChanged(nameof(IsDrawTool));
        Hint = value == ToolKind.DrawWall ? "Cliquez les points du mur — Entrée pour finir — Échap pour annuler" : "Cliquez un mur pour le sélectionner, glissez pour déplacer.";
        Changed?.Invoke();
    }

    /// <summary>Rebuild derived state after any model change.</summary>
    public void Refresh()
    {
        SelectedWalls.IntersectWith(CurrentLevel?.Walls.Select(w => w.Id) ?? []);
        Levels.Clear();
        foreach (var l in Building.Levels.AsEnumerable().Reverse()) // top level first, like an elevator
            Levels.Add(new LevelItem(l.Id, l.Name, $"{l.AltitudeMm / 1000.0:0.0} m", l.Id == CurrentLevel?.Id, l.Walls.Count));
        OnPropertyChanged(nameof(CurrentLevel)); OnPropertyChanged(nameof(SelectionText));
        Changed?.Invoke();
    }

    public void SelectWalls(IEnumerable<Guid> ids, bool add)
    {
        if (!add) SelectedWalls.Clear();
        foreach (var id in ids) SelectedWalls.Add(id);
        Status = SelectionText; OnPropertyChanged(nameof(SelectionText)); Changed?.Invoke();
    }

    /// <summary>Whole drag = one history step.</summary>
    public void MoveSelection(int dx, int dy)
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0 || (dx == 0 && dy == 0)) return;
        Actions.MoveWalls(l.Id, SelectedWalls.ToList(), dx, dy); Status = $"Déplacé de {dx / 1000.0:0.00} m, {dy / 1000.0:0.00} m"; Refresh();
    }

    [RelayCommand] private void SelectLevel(Guid id) { CurrentLevelId = id; SelectedWalls.Clear(); Status = $"{CurrentLevel?.Name}"; Refresh(); }
    [RelayCommand] private void LevelUp() => StepLevel(+1);
    [RelayCommand] private void LevelDown() => StepLevel(-1);
    private void StepLevel(int d)
    {
        var i = Building.Levels.FindIndex(l => l.Id == CurrentLevel?.Id) + d;
        if (i >= 0 && i < Building.Levels.Count) SelectLevel(Building.Levels[i].Id);
    }

    /// <summary>Moves the selected walls to the level above/below (same XY). Follows them to the target level.</summary>
    [RelayCommand] private void MoveSelectionUp() => MoveSelectionToLevel(+1);
    [RelayCommand] private void MoveSelectionDown() => MoveSelectionToLevel(-1);
    private void MoveSelectionToLevel(int d)
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) { Status = "Sélectionnez d'abord des murs"; return; }
        var i = Building.Levels.FindIndex(x => x.Id == l.Id) + d;
        if (i < 0 || i >= Building.Levels.Count) { Status = "Pas d'étage dans cette direction"; return; }
        var target = Building.Levels[i]; var ids = SelectedWalls.ToList();
        Actions.MoveWalls(l.Id, ids, 0, 0, target.Id);
        CurrentLevelId = target.Id; SelectedWalls.Clear(); foreach (var id in ids) SelectedWalls.Add(id);
        Status = $"{ids.Count} mur(s) déplacé(s) vers {target.Name}"; Refresh();
    }

    [RelayCommand] private void CopySelectionToAllLevels()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) return;
        Actions.CopyWalls(l.Id, SelectedWalls.ToList(), Building.Levels.Select(x => x.Id)); Status = "Copié sur tous les niveaux"; Refresh();
    }

    [RelayCommand] private void DeleteSelection()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) return;
        Actions.DeleteWalls(l.Id, SelectedWalls.ToList()); SelectedWalls.Clear(); Status = "Supprimé"; Refresh();
    }

    [RelayCommand] private void SelectAll() { if (CurrentLevel is { } l) SelectWalls(l.Walls.Select(w => w.Id), false); }
    [RelayCommand] private void SetTool(string t) => Tool = Enum.Parse<ToolKind>(t);
    [RelayCommand] private void SetMode(string m) => Mode = Enum.Parse<ViewMode>(m);
    [RelayCommand] private void Undo() { History.Undo(Building); Refresh(); }
    [RelayCommand] private void Redo() { History.Redo(Building); Refresh(); }

    [RelayCommand] private void AddLevel()
    {
        var top = Building.Levels.Count == 0 ? 0 : Building.Levels.Max(l => l.AltitudeMm + l.HeightMm);
        var lv = Actions.AddLevel(Building.Levels.Count == 0 ? "RDC" : $"R+{Building.Levels.Count}", top);
        CurrentLevelId = lv.Id; Status = $"{Building.Levels.Count} niveau(x)"; Refresh();
    }

    /// <summary>Chain of centre points → one rectangle per segment, committed as one history step.</summary>
    public void CommitWalls(IReadOnlyList<PointMm> pts)
    {
        if (CurrentLevel is not { } lv) return;
        var rects = new List<IEnumerable<PointMm>>(); var h = WallThicknessMm / 2;
        for (var i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y; var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) continue;
            int nx = (int)Math.Round(-dy / len * h), ny = (int)Math.Round(dx / len * h);
            rects.Add([new(pts[i - 1].X + nx, pts[i - 1].Y + ny), new(pts[i].X + nx, pts[i].Y + ny), new(pts[i].X - nx, pts[i].Y - ny), new(pts[i - 1].X - nx, pts[i - 1].Y - ny)]);
        }
        if (rects.Count == 0) return;
        Actions.ImportWalls(lv.Id, rects); Status = $"{rects.Count} mur(s) ajouté(s)"; Refresh();
    }

    public void SaveTo(string path) { BuildingFile.Save(Building, path, Images); History.MarkClean(); FilePath = path; Status = "Enregistré " + Path.GetFileName(path); }
    public void LoadFrom(string path)
    {
        var (b, imgs) = BuildingFile.Load(path); var h = new BuildingHistory();
        Load((b, h)); Images = imgs; FilePath = path; Status = "Ouvert " + Path.GetFileName(path);
    }
    /// <summary>Applies a finished wizard to the current level: walls + background, one history step each.</summary>
    public int ApplyWizard(ImportWizardViewModel w)
    {
        if (CurrentLevel is not { } l) return 0;
        var n = w.Apply(Actions, l, Images); Status = $"{n} groupe(s) de murs importé(s)"; Refresh(); return n;
    }

    public int ImportPlan(string imagePath, double pxPerMm)
    {
        if (CurrentLevel is not { } l) return 0;
        var n = PlanImport.Import(Actions, l.Id, File.ReadAllBytes(imagePath), new Calibration(pxPerMm, pxPerMm), new PlanWallDetectorAdapter());
        Status = $"{n} groupe(s) de murs importé(s)"; Refresh(); return n;
    }
    public void Export(string format, string path)
    {
        File.WriteAllText(path, ImmeubleCad.Core.Export.Exporters.All.First(x => x.Id == format).Export(Building)); Status = "Exporté " + Path.GetFileName(path);
    }
    public string BomCsv() => Bom.ToCsv(Building);
}
