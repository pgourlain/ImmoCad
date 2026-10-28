using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmeubleCad.Core;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Import;
using ImmeubleCad.Core.IO;
using ImmeubleCad.Core.Export;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Geometry;
using ImmeubleCad.Core.Catalog;
using ImmeubleCad.Core.Networks;
using ImmeubleCad.Core.Tools;

namespace ImmeubleCad.Desktop;

public enum ViewMode { Plan, Solid, Wireframe, Elevation }
public enum ToolKind { Select, DrawWall, Align, Riser, DrawPipe, Place }

/// <summary>One button of the level bar.</summary>
/// <summary>One open building with its own history, pictures and file.</summary>
public sealed class Doc
{
    public Building Building { get; set; } = new();
    public BuildingHistory History { get; set; } = new();
    public BuildingActions Actions { get; set; } = null!;
    public Dictionary<string, byte[]> Images { get; set; } = new();
    public string? FilePath { get; set; }
    public Guid CurrentLevelId { get; set; }
    public string Title => FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : (Building.Name.Length > 0 ? Building.Name : "Sans titre");
}
public sealed record TabItem(int Index, string Title, bool IsActive, bool IsDirty);
public sealed record HistoryRow(int Index, string Name, bool IsCurrent, bool IsFuture);
public sealed record NetworkItem(Guid Id, string Name, string Type, bool Visible, bool Locked, string ColorHex);
public sealed record PaletteItem(string Id, string Category, string Name, string Glyph);
public sealed record LevelItem(Guid Id, string Name, string Altitude, bool IsCurrent, int WallCount);

public partial class MainViewModel : ObservableObject
{
    public List<Doc> Docs { get; } = new();
    public int ActiveIndex { get; private set; } = -1;
    private static Doc MakeEmpty() { var d = new Doc(); d.Actions = new BuildingActions(d.Building, d.History, new BuildingEventsService()); return d; }
    private readonly Doc _empty = MakeEmpty();   // stands in while no building is open (welcome screen), so views never see null
    private Doc Active => ActiveIndex >= 0 && ActiveIndex < Docs.Count ? Docs[ActiveIndex] : _empty;
    public Building Building => Active.Building;
    public BuildingHistory History => Active.History;
    public BuildingActions Actions => Active.Actions;
    public ObservableCollection<TabItem> Tabs { get; } = new();
    public ObservableCollection<HistoryRow> HistoryRows { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();
    public bool HasDocument => ActiveIndex >= 0;
    public bool ShowWelcome => ActiveIndex < 0;
    public IDialogService Dialogs { get; set; } = new FakeDialogService();
    public AppSettings Settings { get; set; } = new();
    public string? SettingsPath { get; set; }
    public ObservableCollection<LevelItem> Levels { get; } = new();
    public ObservableCollection<NetworkItem> Networks { get; } = new();
    public ObservableCollection<PaletteItem> Palette { get; } = new();
    public ObservableCollection<string> Recent { get; } = new();
    public NetworkType[] NetworkTypes { get; } = Enum.GetValues<NetworkType>();
    public string[] RiserTemplates { get; } = ["Simple", "EF + ECS", "Chauffage aller + retour", "EF + ECS + EU"];
    public HashSet<Guid> SelectedWalls { get; } = new();
    /// <summary>Background pictures by name (stored under images/ in the .immeuble file).</summary>
    public Dictionary<string, byte[]> Images => Active.Images;

    [ObservableProperty] private Guid _currentLevelId;
    [ObservableProperty] private ViewMode _mode = ViewMode.Plan;
    [ObservableProperty] private ToolKind _tool = ToolKind.Select;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private string _hint = "Cliquez un mur pour le sélectionner, glissez pour déplacer.";
    public int WallThicknessMm { get; set; } = 200;

    // ---- Pipe selection (handles) ----
    public (Guid NetworkId, Guid SegmentId)? SelectedSegment { get; private set; }
    public void SelectSegment(Guid networkId, Guid segmentId) { SelectedWalls.Clear(); SelectedSegment = (networkId, segmentId); Status = "Tuyau sélectionné — poignées : prolonger, coude (Ctrl), té (milieu)"; OnPropertyChanged(nameof(SelectionText)); Changed?.Invoke(); }
    public void ClearSegment() { SelectedSegment = null; }
    public int Snapper_GridMm() => 50;
    public void AfterEdit(string? message = null) { if (message != null) Status = message; Refresh(); }

    // ---- Network tool options ----
    [ObservableProperty] private NetworkType _riserType = NetworkType.ColdWater;
    [ObservableProperty] private int _riserDiameter = 16;
    [ObservableProperty] private int _riserSpacing = 150;
    [ObservableProperty] private string _riserTemplate = "Simple";
    [ObservableProperty] private bool _riserWholeBuilding = true;
    [ObservableProperty] private int _riserFromIndex;
    [ObservableProperty] private int _riserToIndex = 99;
    [ObservableProperty] private NetworkType _pipeType = NetworkType.HeatingSupply;
    [ObservableProperty] private int _pipeDiameter = 22;
    [ObservableProperty] private int _pipeElevation = 2400;
    [ObservableProperty] private string? _paletteId;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _propLabel = "";
    [ObservableProperty] private int _propHeight;
    partial void OnRiserTypeChanged(NetworkType value) => RiserDiameter = NetworkQueries.DefaultDiameter(value);
    partial void OnPipeTypeChanged(NetworkType value) => PipeDiameter = NetworkQueries.DefaultDiameter(value);
    partial void OnSearchChanged(string value) => RebuildPalette();

    private void RebuildPalette()
    {
        Palette.Clear();
        foreach (var e in EquipmentCatalog.Default.Where(e => Search.Length == 0 || e.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(Search, StringComparison.OrdinalIgnoreCase)))
            Palette.Add(new PaletteItem(e.Id, e.Category, e.Name, e.Inline ? "⊗" : e.Category == "Sanitary" ? "◍" : "▤"));
    }

    // ---- Elevation view ----
    /// <summary>Shows the front view of the longest face of the selected wall, seen from the side facing the building's centre.</summary>
    [RelayCommand] private void ShowElevation()
    {
        var wall = CurrentLevel?.Walls.FirstOrDefault(w => SelectedWalls.Contains(w.Id));
        if (wall == null || wall.Footprint.Count < 2) { Status = "Sélectionnez un mur pour voir son élévation"; return; }
        var fp = wall.Footprint; var best = 0; double bestLen = -1;
        for (var i = 0; i < fp.Count; i++) { var a = fp[i]; var b = fp[(i + 1) % fp.Count]; var len = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)); if (len > bestLen) { bestLen = len; best = i; } }
        var pa = fp[best]; var pb = fp[(best + 1) % fp.Count];
        var all = Building.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).ToList();
        PointMm centre = new((int)all.Average(p => p.X), (int)all.Average(p => p.Y));
        var side = new ElevationPlane(pa, pb, 50, +1).Distance(centre) >= 0 ? +1 : -1;
        Plane = new ElevationPlane(pa, pb, 50, side); Mode = ViewMode.Elevation; Hint = "Colonne : cliquez (ou glissez pour fixer haut/bas). Tuyau : glissez à l'horizontale.";
    }

    /// <summary>Vertical pipe on the face; whole building by default, snapped to level lines when dragged.</summary>
    public void PlaceVerticalPipe(double u, double? zFrom, double? zTo)
    {
        if (Plane == null || Building.Levels.Count == 0) return;
        var lo = (int)(zFrom ?? Building.Levels[0].AltitudeMm); var hi = (int)(zTo ?? Building.Levels[^1].AltitudeMm + Building.Levels[^1].HeightMm);
        Actions.AddVerticalPipe(Plane, Math.Clamp(u, 0, Plane.Length), RiserType, Math.Min(lo, hi), Math.Max(lo, hi), RiserDiameter);
        Status = "Tuyau vertical posé sur le mur"; Refresh();
    }

    public void PlaceHorizontalPipe(double u0, double u1, double z)
    {
        if (Plane == null || Math.Abs(u1 - u0) < 20) return;
        Actions.AddHorizontalAlong(Plane, Math.Clamp(u0, 0, Plane.Length), Math.Clamp(u1, 0, Plane.Length), PipeType, (int)z, PipeDiameter);
        Status = "Tuyau horizontal posé le long du mur"; Refresh();
    }

    // ---- Risers ----
    /// <summary>Places a riser (or a template of several) at the point, snapped 50 mm from wall faces. One history step.</summary>
    public void PlaceRiser(PointMm at)
    {
        if (Building.Levels.Count == 0) return;
        var polys = CurrentLevel?.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint) ?? [];
        var p = RiserSnap.Snap(at, polys);
        var lo = Math.Clamp(RiserFromIndex, 0, Building.Levels.Count - 1); var hi = Math.Clamp(RiserToIndex, 0, Building.Levels.Count - 1);
        var from = RiserWholeBuilding ? Building.Levels[0].AltitudeMm : Building.Levels[Math.Min(lo, hi)].AltitudeMm;
        var to = RiserWholeBuilding ? Building.Levels[^1].AltitudeMm + Building.Levels[^1].HeightMm : Building.Levels[Math.Max(lo, hi)].AltitudeMm + Building.Levels[Math.Max(lo, hi)].HeightMm;
        var (types, spacing) = RiserTemplate switch
        {
            "EF + ECS" => ([NetworkType.ColdWater, NetworkType.DomesticHotWater], RiserSpacing),
            "Chauffage aller + retour" => ([NetworkType.HeatingSupply, NetworkType.HeatingReturn], RiserSpacing),
            "EF + ECS + EU" => ([NetworkType.ColdWater, NetworkType.DomesticHotWater, NetworkType.Drainage], RiserSpacing),
            _ => (new List<NetworkType> { RiserType }, 0),
        };
        Actions.AddRiserTemplate(p, spacing, types, from, to, RiserDiameter);
        Status = $"{types.Count} colonne(s) posée(s)"; Refresh();
    }

    // ---- Horizontal pipes (plan): constrained to X/Y at the chosen height above the floor ----
    public void CommitPipe(IReadOnlyList<PointMm> pts)
    {
        if (CurrentLevel is not { } l || pts.Count < 2) return;
        var z = l.AltitudeMm + PipeElevation;
        var net = Building.Networks.FirstOrDefault(n => n.Type == PipeType && !n.Locked) is { } ex ? ex : Actions.AddNetwork(PipeType.ToString(), PipeType);
        Actions.AddPipeRun(net.Id, pts.Select(p => new PointMm3(p.X, p.Y, z)).ToList(), PipeDiameter);
        Status = $"{pts.Count - 1} tronçon(s)"; Refresh();
    }

    // ---- Palette objects ----
    public string? PlacementHint(PointMm at)
    {
        var e = PaletteId == null ? null : EquipmentCatalog.Find(PaletteId); if (e == null || CurrentLevel is not { } l) return null;
        if (!e.Inline) return null;
        return FindPipeFor(at, l, e) is null ? "Une vanne doit être posée sur un tuyau" : null;
    }

    private (SegmentRef Seg, PointMm3 P)? FindPipeFor(PointMm at, Level l, CatalogEntry e)
    {
        var near = NetworkQueries.Nearest(Building, new PointMm3(at.X, at.Y, l.AltitudeMm + PipeElevation), 250, s => !s.Network.Locked && !NetworkQueries.IsVertical(s) && s.A.Z >= l.AltitudeMm && s.A.Z < l.AltitudeMm + l.HeightMm);
        near ??= NetworkQueries.Nearest(Building, new PointMm3(at.X, at.Y, l.AltitudeMm + 1000), 250, s => !s.Network.Locked && NetworkQueries.IsVertical(s));
        return near is { } n && PlacementRules.Check(e, n.Point, n.Segment.Network).Valid ? (n.Segment, n.Point) : null;
    }

    public void PlaceFromPalette(PointMm at)
    {
        if (PaletteId == null || CurrentLevel is not { } l) { Status = "Choisissez un objet dans la palette"; return; }
        var e = EquipmentCatalog.Find(PaletteId)!;
        if (e.Inline)
        {
            if (FindPipeFor(at, l, e) is not { } hit) { Status = "Une vanne doit être posée sur un tuyau"; return; }
            Actions.InsertComponent(hit.Seg.Network.Id, hit.Seg.Segment.Id, hit.P, e.Id); Status = $"{e.Name} posé(e) sur le tuyau";
        }
        else
        {
            // snap against the nearest wall face, oriented along it; default height from the catalog
            var polys = l.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint); var p = RiserSnap.Snap(at, polys, offsetMm: e.DepthMm / 2, reachMm: 400);
            Actions.AddEquipment(l.Id, e.Id, p, e.DefaultElevationMm); Status = $"{e.Name} posé(e)";
        }
        Recent.Remove(e.Id); Recent.Insert(0, e.Id); if (Recent.Count > 6) Recent.RemoveAt(6);
        Refresh();
    }

    [RelayCommand] private void SelectPalette(string id) { PaletteId = id; Tool = ToolKind.Place; Hint = EquipmentCatalog.Find(id)?.Inline == true ? "Cliquez sur un tuyau pour poser l'élément" : "Cliquez pour poser l'objet"; }
    [RelayCommand] private void ToggleNetwork(Guid id)
    {
        var n = Building.Networks.First(x => x.Id == id); Actions.SetNetworkDisplay(id, !n.Visible, n.Locked); Refresh();
    }
    [RelayCommand] private void LockNetwork(Guid id)
    {
        var n = Building.Networks.First(x => x.Id == id); Actions.SetNetworkDisplay(id, n.Visible, !n.Locked); Refresh();
    }
    [RelayCommand] private void ApplyProperties()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) return;
        Actions.SetWallProperties(l.Id, SelectedWalls.ToList(), PropLabel.Length == 0 ? null : PropLabel, PropHeight <= 0 ? null : PropHeight); Status = "Propriétés appliquées"; Refresh();
    }

    // ---- Align with the level below: pairs (point on this level → same point on the level below) ----
    public List<((double X, double Y) From, (double X, double Y) To)> AlignPairs { get; } = new();
    public (double X, double Y)? AlignPending { get; private set; }
    public double[]? AlignResiduals { get; private set; }
    public bool AlignMoveWalls { get; set; } = true;

    public void AlignClick(double x, double y)
    {
        if (AlignPending is { } from) { AlignPairs.Add((from, (x, y))); AlignPending = null; UpdateAlignPreview(); }
        else AlignPending = (x, y);
        Hint = AlignPending != null ? "Cliquez le MÊME point sur le niveau du dessous (en bleu)" : AlignPairs.Count < 2 ? "Cliquez un point de ce niveau (au moins 2 paires)" : "Entrée pour appliquer — ≥3 paires = étirement X/Y indépendant";
        Changed?.Invoke();
    }
    private void UpdateAlignPreview() => AlignResiduals = ImmeubleCad.Core.Geometry.ImageFit.Fit(AlignPairs) is { } f ? ImmeubleCad.Core.Geometry.ImageFit.Residuals(f, AlignPairs) : null;

    public void AlignUndoPair() { if (AlignPairs.Count > 0) AlignPairs.RemoveAt(AlignPairs.Count - 1); AlignPending = null; UpdateAlignPreview(); Changed?.Invoke(); }
    public void AlignCancel() { AlignPairs.Clear(); AlignPending = null; AlignResiduals = null; Changed?.Invoke(); }

    /// <summary>Applies the fit as one history step; returns false when degenerate (e.g. collinear points).</summary>
    public bool AlignApply()
    {
        if (CurrentLevel is not { } l || AlignPairs.Count < 2) return false;
        var res = Actions.AlignLevel(l.Id, AlignPairs.ToList(), AlignMoveWalls);
        if (res == null) { Status = "Points alignés : choisissez des points non colinéaires"; return false; }
        Status = $"Aligné — erreur max {res.Max():0} mm"; AlignCancel(); Refresh(); return true;
    }
    public string? FilePath => ActiveIndex >= 0 ? Active.FilePath : null;
    public event Action? Changed;

    /// <summary>Always resolved by id: undo/redo rebuilds the whole object graph, so references go stale.</summary>
    public Level? CurrentLevel => Building.Levels.FirstOrDefault(l => l.Id == CurrentLevelId) ?? Building.Levels.FirstOrDefault();
    public bool Is3D => Mode is ViewMode.Solid or ViewMode.Wireframe;
    public bool IsElevation => Mode == ViewMode.Elevation;
    public bool IsPlan => Mode == ViewMode.Plan;
    public string Title => ActiveIndex < 0 ? "ImmeubleCad" : $"{(History.IsDirty ? "• " : "")}{Active.Title} — ImmeubleCad";
    public ElevationPlane? Plane { get; private set; }
    public bool IsSelectTool => Tool == ToolKind.Select;
    public bool IsDrawTool => Tool == ToolKind.DrawWall;
    public bool IsAlignTool => Tool == ToolKind.Align;
    public bool IsRiserTool => Tool == ToolKind.Riser;
    public bool IsPipeTool => Tool == ToolKind.DrawPipe;
    public bool IsPlaceTool => Tool == ToolKind.Place;
    public string SelectionText => SelectedSegment != null ? "1 tuyau sélectionné" : SelectedWalls.Count == 0 ? "Aucune sélection" : $"{SelectedWalls.Count} mur(s) sélectionné(s)";

    public MainViewModel() : this(startWithSample: true) { }

    /// <summary>startWithSample=false shows the welcome screen (New / Open / Recent) until a document exists.</summary>
    public MainViewModel(bool startWithSample)
    {
        RebuildPalette();
        if (startWithSample) { AddDoc(new Doc { Building = SampleBuilding.Create().Building }); }
        else Refresh();
    }

    private void AddDoc(Doc d, bool clean = true)
    {
        if (d.History.Items.Count == 0 && clean) { }
        d.Actions = new BuildingActions(d.Building, d.History, new BuildingEventsService());
        if (d.CurrentLevelId == Guid.Empty) d.CurrentLevelId = d.Building.Levels.FirstOrDefault()?.Id ?? Guid.Empty;
        Docs.Add(d); Activate(Docs.Count - 1);
    }

    private void Activate(int index)
    {
        if (ActiveIndex >= 0 && ActiveIndex < Docs.Count) Active.CurrentLevelId = CurrentLevelId;
        ActiveIndex = index; SelectedWalls.Clear(); SelectedSegment = null;
        _currentLevelId = Active.CurrentLevelId; OnPropertyChanged(nameof(CurrentLevelId));
        OnPropertyChanged(nameof(HasDocument)); OnPropertyChanged(nameof(ShowWelcome)); Refresh();
    }

    partial void OnModeChanged(ViewMode value) { OnPropertyChanged(nameof(Is3D)); OnPropertyChanged(nameof(IsElevation)); OnPropertyChanged(nameof(IsPlan)); Changed?.Invoke(); }
    partial void OnToolChanged(ToolKind value)
    {
        OnPropertyChanged(nameof(IsSelectTool)); OnPropertyChanged(nameof(IsDrawTool)); OnPropertyChanged(nameof(IsAlignTool)); OnPropertyChanged(nameof(IsRiserTool)); OnPropertyChanged(nameof(IsPipeTool)); OnPropertyChanged(nameof(IsPlaceTool));
        Hint = value switch { ToolKind.DrawWall => "Cliquez les points du mur — Entrée pour finir — Échap pour annuler", ToolKind.Riser => "Cliquez pour poser une colonne (collée à 50 mm des murs)", ToolKind.DrawPipe => "Cliquez les points du tuyau — Entrée pour finir — Échap pour annuler", ToolKind.Place => "Cliquez pour poser l'objet de la palette", ToolKind.Align => "Cliquez un point de ce niveau, puis le même point sur le niveau du dessous (au moins 2 paires)", _ => "Cliquez un mur pour le sélectionner, glissez pour déplacer." }; if (value != ToolKind.Align) AlignCancel();
        Changed?.Invoke();
    }

    /// <summary>Rebuild derived state after any model change.</summary>
    public void Refresh()
    {
        Tabs.Clear(); for (var i = 0; i < Docs.Count; i++) Tabs.Add(new TabItem(i, Docs[i].Title, i == ActiveIndex, Docs[i].History.IsDirty));
        OnPropertyChanged(nameof(Title));
        if (ActiveIndex < 0) { Levels.Clear(); Networks.Clear(); HistoryRows.Clear(); Changed?.Invoke(); return; }
        HistoryRows.Clear(); HistoryRows.Add(new HistoryRow(-1, "État initial", History.Pointer == -1, false));
        for (var i = 0; i < History.Items.Count; i++) HistoryRows.Add(new HistoryRow(i, History.Items[i].Name, i == History.Pointer, i > History.Pointer));
        SelectedWalls.IntersectWith(CurrentLevel?.Walls.Select(w => w.Id) ?? []);
        Levels.Clear();
        foreach (var l in Building.Levels.AsEnumerable().Reverse()) // top level first, like an elevator
            Levels.Add(new LevelItem(l.Id, l.Name, $"{l.AltitudeMm / 1000.0:0.0} m", l.Id == CurrentLevel?.Id, l.Walls.Count));
        Networks.Clear();
        foreach (var n in Building.Networks)
        { var c = NetworkQueries.ColorOf(n.Type); Networks.Add(new NetworkItem(n.Id, n.Name, n.Type.ToString(), n.Visible, n.Locked, $"#{c.R:X2}{c.G:X2}{c.B:X2}")); }
        var sel = CurrentLevel?.Walls.FirstOrDefault(w => SelectedWalls.Contains(w.Id));
        if (sel != null) { PropLabel = sel.Label ?? ""; PropHeight = sel.HeightMm ?? 0; }
        OnPropertyChanged(nameof(CurrentLevel)); OnPropertyChanged(nameof(SelectionText));
        Changed?.Invoke();
    }

    public void SelectWalls(IEnumerable<Guid> ids, bool add)
    {
        if (ids.Any()) SelectedSegment = null;
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

    [RelayCommand] private void SelectLevel(Guid id) { CurrentLevelId = id; SelectedWalls.Clear(); SelectedSegment = null; Status = $"{CurrentLevel?.Name}"; Refresh(); }
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

    // ---- Display options ----
    [ObservableProperty] private bool _ghostBelow = true;
    [ObservableProperty] private bool _ghostAbove;
    [ObservableProperty] private bool _commonOnly;
    [RelayCommand] private void ToggleGhostBelow() { GhostBelow = !GhostBelow; Changed?.Invoke(); }
    [RelayCommand] private void ToggleGhostAbove() { GhostAbove = !GhostAbove; Changed?.Invoke(); }
    [RelayCommand] private void ToggleCommonOnly() { CommonOnly = !CommonOnly; Status = CommonOnly ? "Parties communes seulement" : "Tout afficher"; Changed?.Invoke(); }

    /// <summary>Straightens the selected walls (tolerance 30 mm, 5°) as one history step.</summary>
    [RelayCommand] private void StraightenSelection()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) { Status = "Sélectionnez des murs à redresser"; return; }
        var before = l.Walls.Where(w => SelectedWalls.Contains(w.Id)).Sum(w => w.Footprint.Count);
        Actions.StraightenWalls(l.Id, SelectedWalls.ToList()); var after = CurrentLevel!.Walls.Where(w => SelectedWalls.Contains(w.Id)).Sum(w => w.Footprint.Count);
        Status = $"Redressé : {before} → {after} segments"; Refresh();
    }

    // ---- Documents ----
    [RelayCommand] private void NewDocument()
    {
        var d = new Doc(); d.Building.Name = ""; AddDoc(d);
        Actions.AddLevel("RDC", 0, 2700, LevelKind.Ground); CurrentLevelId = Building.Levels[0].Id; History.MarkClean(); Refresh(); Status = "Nouvel immeuble";
    }
    /// <summary>Opens an autosaved building after a crash; it stays dirty so closing asks to save it.</summary>
    public void AdoptRecovered(Building b) { var d = new Doc { Building = b }; AddDoc(d); Actions.AddLevel("(récupéré)", 0); Status = "Travail récupéré après un arrêt inattendu"; }
    [RelayCommand] private void OpenSample() { var d = new Doc { Building = SampleBuilding.Create().Building }; d.Building.Name = "Exemple"; AddDoc(d); Status = "Exemple ouvert — essayez d'importer un plan"; }
    [RelayCommand] private void SwitchTab(int index) { if (index >= 0 && index < Docs.Count) Activate(index); }
    [RelayCommand] private void JumpTo(int index) { History.JumpTo(Building, index); SelectedWalls.Clear(); Refresh(); }

    [RelayCommand] private async Task OpenAsync()
    {
        try { if (await Dialogs.PickOpenAsync("Immeuble", "*.immeuble") is { } p) OpenFile(p); }
        catch (Exception ex) { await Fail("ouvrir", ex); }
    }
    [RelayCommand] private async Task OpenRecentAsync(string path)
    {
        try { OpenFile(path); } catch (Exception ex) { Settings.RecentFiles.Remove(path); RecentFiles.Remove(path); await Fail("ouvrir", ex); }
    }
    [RelayCommand] private async Task SaveAsync() { await SaveActiveAsync(false); }
    [RelayCommand] private async Task SaveAsAsync() { await SaveActiveAsync(true); }
    public async Task<bool> SaveActiveAsync(bool saveAs)
    {
        if (ActiveIndex < 0) return false;
        try
        {
            var path = saveAs ? null : FilePath;
            path ??= await Dialogs.PickSaveAsync("Immeuble", "*.immeuble", Active.Title + ".immeuble"); if (path == null) return false;
            SaveTo(path); return true;
        }
        catch (Exception ex) { await Fail("enregistrer", ex); return false; }
    }

    /// <summary>Closes a tab; asks to save when there are unsaved changes. Returns false when the user cancels.</summary>
    public async Task<bool> CloseDocumentAsync(int index)
    {
        if (index < 0 || index >= Docs.Count) return true;
        var d = Docs[index];
        if (d.History.IsDirty)
        {
            Activate(index);
            switch (await Dialogs.ConfirmSaveAsync(d.Title)) { case SaveChoice.Cancel: return false; case SaveChoice.Save: if (!await SaveActiveAsync(false)) return false; break; }
        }
        Docs.RemoveAt(index);
        ActiveIndex = Docs.Count == 0 ? -1 : Math.Min(index, Docs.Count - 1);
        if (ActiveIndex >= 0) Activate(ActiveIndex); else { OnPropertyChanged(nameof(HasDocument)); OnPropertyChanged(nameof(ShowWelcome)); Refresh(); }
        return true;
    }
    [RelayCommand] private async Task CloseTabAsync(int index) => await CloseDocumentAsync(index);

    /// <summary>Quit: every dirty document gets its save prompt; false when cancelled.</summary>
    public async Task<bool> CanQuitAsync() { for (var i = Docs.Count - 1; i >= 0; i--) if (!await CloseDocumentAsync(i)) return false; return true; }

    [RelayCommand] private async Task ImportPlanAsync()
    {
        if (ActiveIndex < 0) return;
        try
        {
            if (await Dialogs.PickOpenAsync("Plan", "image") is not { } path) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (await Dialogs.RunImportWizardAsync(File.ReadAllBytes(path)) is { } wiz) { var n = ApplyWizard(wiz); OnLog?.Invoke($"Import plan {Path.GetFileName(path)}: {n} shapes in {sw.ElapsedMilliseconds} ms"); }
        }
        catch (Exception ex) { await Fail("importer", ex); }
    }
    [RelayCommand] private async Task ExportAsync(string format)
    {
        if (ActiveIndex < 0) return;
        try
        {
            var ext = format switch { "ifc" => "*.ifc", "freecad" => "*.FCMacro", "dxf" => "*.dxf", "obj" => "*.obj", "glb" => "*.glb", "bom_pdf" => "*.pdf", "svg" => "*.svg", _ => "*.csv" };
            if (await Dialogs.PickSaveAsync("Export", ext, Active.Title + ext.TrimStart('*')) is not { } path) return;
            switch (format)
            {
                case "csv": File.WriteAllText(path, BomFull.ToCsv(Building, true)); break;
                case "bom_pdf": File.WriteAllBytes(path, BomFull.ToPdf(Building)); break;
                case "glb": File.WriteAllBytes(path, GltfExporter.ExportGlb(Building)); break;
                case "svg": File.WriteAllText(path, SvgExporter.Export(CurrentLevel!)); break;
                default: Export(format, path); break;
            }
            Status = "Exporté " + Path.GetFileName(path); OnLog?.Invoke($"Exported {format} to {path}");
        }
        catch (Exception ex) { await Fail("exporter", ex); }
    }
    public event Action<string>? OnLog;
    public event Action<string, Exception>? OnError;
    private async Task Fail(string what, Exception ex) { OnError?.Invoke(what, ex); Status = $"Erreur ({what}) : {ex.Message}"; await Dialogs.ShowErrorAsync("ImmeubleCad", $"Impossible de {what} : {ex.Message}"); }

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

    public void SaveTo(string path)
    {
        BuildingFile.Save(Building, path, Images); History.MarkClean(); Active.FilePath = path; Status = "Enregistré " + Path.GetFileName(path); Remember(path); Refresh();
    }
    private void Remember(string path)
    {
        Settings.AddRecent(path); RecentFiles.Clear(); foreach (var f in Settings.RecentFiles) RecentFiles.Add(f);
        if (SettingsPath != null) try { SettingsStore.Save(Settings, SettingsPath); } catch (IOException) { }
    }
    public void OpenFile(string path)
    {
        var existing = Docs.FindIndex(d => d.FilePath == path); if (existing >= 0) { Activate(existing); return; }   // already open: switch to its tab
        var (b, imgs) = BuildingFile.Load(path);
        AddDoc(new Doc { Building = b, Images = imgs, FilePath = path }); Status = "Ouvert " + Path.GetFileName(path); Remember(path);
    }
    public void LoadFrom(string path) => OpenFile(path);
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
