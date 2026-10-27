using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;

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
    [ObservableProperty] private string _status = "Ready";

    public MainViewModel()
    {
        Actions = new BuildingActions(Building, History, new BuildingEventsService());
        var rdc = Actions.AddLevel("RDC", 0, 2700, LevelKind.Ground);
        Actions.AddWall(rdc.Id, [new(0, 0), new(6000, 0), new(6000, 200), new(0, 200)]);
        Actions.AddWall(rdc.Id, [new(0, 0), new(200, 0), new(200, 4000), new(0, 4000)]);
        Actions.AddLevel("R+1", 2700);
        History.MarkClean();
        Refresh(); CurrentLevel = Levels.FirstOrDefault();
    }

    private void Refresh() { Levels.Clear(); foreach (var l in Building.Levels.AsEnumerable().Reverse()) Levels.Add(l); }

    [RelayCommand] private void SelectLevel(Level l) { CurrentLevel = l; Status = $"{l.Name} @ {l.AltitudeMm / 1000.0:0.00} m"; }
    [RelayCommand] private void SetMode(string m) => Mode = Enum.Parse<ViewMode>(m);
    [RelayCommand] private void Undo() { History.Undo(Building); Refresh(); OnPropertyChanged(nameof(CurrentLevel)); }
    [RelayCommand] private void Redo() { History.Redo(Building); Refresh(); OnPropertyChanged(nameof(CurrentLevel)); }
    [RelayCommand] private void AddLevel()
    {
        var top = Building.Levels.Max(l => l.AltitudeMm + l.HeightMm);
        Actions.AddLevel($"R+{Building.Levels.Count}", top); Refresh();
    }
}
