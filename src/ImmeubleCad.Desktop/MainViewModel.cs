using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.IO;
using ImmeubleCad.Core.Import;
using ImmeubleCad.Core.Tools;

namespace ImmeubleCad.Desktop;

public enum ViewMode { Plan, Solid, Wireframe }

public partial class MainViewModel : ObservableObject
{
    public Building Building { get; } = new();
    public BuildingHistory History { get; } = new();
    public BuildingActions Actions { get; }
    public ObservableCollection<Level> Levels { get; } = new();

    [ObservableProperty] private Level? _currentLevel;
    [ObservableProperty] private ViewMode _mode = ViewMode.Plan;
    public bool Is3D => Mode != ViewMode.Plan;
    partial void OnModeChanged(ViewMode value) => OnPropertyChanged(nameof(Is3D));
    [ObservableProperty] private string _status = "Ready";

    public MainViewModel()
    {
        Actions = new BuildingActions(Building, History, new BuildingEventsService());
        var rdc = Actions.AddLevel("RDC", 0, 2700, LevelKind.Ground);
        Actions.AddWall(rdc.Id, [new(0, 0), new(6000, 0), new(6000, 200), new(0, 200)]);
        Actions.AddWall(rdc.Id, [new(0, 0), new(200, 0), new(200, 4000), new(0, 4000)]);
        Actions.AddLevel("R+1", 2700);
        History.MarkClean();
        Refresh(); CurrentLevel = Building.Levels.FirstOrDefault();
    }

    private void Refresh() { Levels.Clear(); foreach (var l in Building.Levels.AsEnumerable().Reverse()) Levels.Add(l); }

    [RelayCommand] private void SelectLevel(Level l) { CurrentLevel = l; Status = $"{l.Name} @ {l.AltitudeMm / 1000.0:0.00} m"; }
    [RelayCommand] private void SetMode(string m) => Mode = Enum.Parse<ViewMode>(m);
    [RelayCommand] private void Undo() { History.Undo(Building); Refresh(); OnPropertyChanged(nameof(CurrentLevel)); Changed?.Invoke(); }
    [RelayCommand] private void Redo() { History.Redo(Building); Refresh(); OnPropertyChanged(nameof(CurrentLevel)); Changed?.Invoke(); }
    public int WallThicknessMm { get; set; } = 200;

    /// <summary>Chain of centre points → one rectangle per segment, committed as one history step.</summary>
    public void CommitWalls(IReadOnlyList<PointMm> pts)
    {
        if (CurrentLevel == null) return;
        var rects = new List<IEnumerable<PointMm>>();
        var h = WallThicknessMm / 2;
        for (var i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y; var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) continue;
            int nx = (int)Math.Round(-dy / len * h), ny = (int)Math.Round(dx / len * h);
            rects.Add([new(pts[i - 1].X + nx, pts[i - 1].Y + ny), new(pts[i].X + nx, pts[i].Y + ny), new(pts[i].X - nx, pts[i].Y - ny), new(pts[i - 1].X - nx, pts[i - 1].Y - ny)]);
        }
        if (rects.Count == 0) return;
        Actions.ImportWalls(CurrentLevel.Id, rects);
        Status = $"{rects.Count} wall(s) added"; Changed?.Invoke();
    }

    public event Action? Changed;
    public string? FilePath { get; private set; }
    public void SaveTo(string path) { BuildingFile.Save(Building, path); History.MarkClean(); FilePath = path; Status = "Saved " + Path.GetFileName(path); }
    public void LoadFrom(string path)
    {
        var (b, _) = BuildingFile.Load(path);
        ImmeubleCad.Core.History.BuildingJson.Restore(Building, ImmeubleCad.Core.History.BuildingJson.Serialize(b));
        Refresh(); CurrentLevel = Building.Levels.FirstOrDefault(); FilePath = path; Status = "Opened " + Path.GetFileName(path); Changed?.Invoke();
    }
    public int ImportPlan(string imagePath, double pxPerMm)
    {
        if (CurrentLevel == null) return 0;
        var n = PlanImport.Import(Actions, CurrentLevel.Id, File.ReadAllBytes(imagePath), new Calibration(pxPerMm, pxPerMm), new PlanWallDetectorAdapter());
        Status = $"Imported {n} wall(s)"; Changed?.Invoke(); return n;
    }
    public int Export(string format, string path)
    {
        var e = ImmeubleCad.Core.Export.Exporters.All.First(x => x.Id == format);
        File.WriteAllText(path, e.Export(Building)); Status = "Exported " + Path.GetFileName(path); return 0;
    }
    public string BomCsv() => ImmeubleCad.Core.Bom.ToCsv(Building);

    [RelayCommand] private void AddLevel()
    {
        var top = Building.Levels.Max(l => l.AltitudeMm + l.HeightMm);
        Actions.AddLevel($"R+{Building.Levels.Count}", top); Refresh();
    }
}
