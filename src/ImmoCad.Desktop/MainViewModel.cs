using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmoCad.Core;
using ImmoCad.Core.History;
using ImmoCad.Core.Import;
using ImmoCad.Core.IO;
using ImmoCad.Core.Export;
using ImmoCad.Core.Model;
using static ImmoCad.Core.Translations;
using ImmoCad.Core.Geometry;
using ImmoCad.Core.Catalog;
using ImmoCad.Core.Networks;
using ImmoCad.Core.Tools;
using ImmoCad.Core.Site;

namespace ImmoCad.Desktop;

public enum ViewPlane { XY, XZ, YZ }
public enum WallDisplay { Solid, Wireframe, Hidden }
public enum ViewMode { Plan, Solid, Wireframe, Elevation, Site }
public enum ToolKind { Select, DrawWall, Align, Riser, DrawPipe, Place, Door, Window, Space, Measure, NumberCellars, Straighten, Stretch, CutWall, MergeRegion }

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
    public string Title => FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : (Building.Name.Length > 0 ? Building.DisplayName : T("untitled"));
}
public sealed record TabItem(int Index, string Title, bool IsActive, bool IsDirty);
public sealed record HistoryRow(int Index, string Name, bool IsCurrent, bool IsFuture);
public sealed record SpaceItem(Guid LevelId, Guid Id, string Name, string Number, string Kind, string Lot, string Area, bool IsCommon, PointMm Center);
public sealed record NetworkItem(Guid Id, string Name, string Type, bool Visible, bool Locked, string ColorHex, string Where = "");
public sealed record PaletteItem(string Id, string Category, string Name, string Glyph, string Description = "");
public sealed record LevelItem(Guid Id, string Name, string Altitude, bool IsCurrent, int WallCount, string Tooltip = "", bool Hidden = false)
{
    public string BasementSpec => $"{Id}:Basement"; public string GroundSpec => $"{Id}:Ground"; public string StandardSpec => $"{Id}:Standard";
}

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
    public ObservableCollection<SpaceItem> SpaceItems { get; } = new();
    public SpaceKind[] SpaceKinds { get; } = Enum.GetValues<SpaceKind>();
    public LevelKind[] LevelKinds { get; } = Enum.GetValues<LevelKind>();
    public ObservableCollection<PaletteItem> Palette { get; } = new();
    public ObservableCollection<string> Recent { get; } = new();
    public NetworkType[] NetworkTypes { get; } = Enum.GetValues<NetworkType>();
    public string[] RiserTemplates { get; } = ["tpl.simple", "tpl.dhw", "tpl.loop", "tpl.heating", "tpl.all", "tpl.drain"];   // keys: shown translated
    public HashSet<Guid> SelectedWalls { get; } = new();
    /// <summary>Background pictures by name (stored under images/ in the .immeuble file).</summary>
    public Dictionary<string, byte[]> Images => Active.Images;

    [ObservableProperty] private Guid _currentLevelId;
    [ObservableProperty] private ViewMode _mode = ViewMode.Plan;
    [ObservableProperty] private ToolKind _tool = ToolKind.Select;
    [ObservableProperty] private string _status = T("ready");
    [ObservableProperty] private string _hint = T("hint.select");
    public int WallThicknessMm { get; set; } = 200;
    /// <summary>Side of the drawn line the new wall lies on (Space cycles it while drawing).</summary>
    public ImmoCad.Core.Geometry.WallAlign WallAlign { get; private set; } = ImmoCad.Core.Geometry.WallAlign.Center;
    public void CycleWallAlign()
    {
        WallAlign = (ImmoCad.Core.Geometry.WallAlign)(((int)WallAlign + 1) % 3);
        Status = T("align." + WallAlign.ToString().ToLowerInvariant()); OnPropertyChanged(nameof(WallAlign)); Changed?.Invoke();
    }

    // ---- 3D ----
    [ObservableProperty] private bool _xRay;
    [ObservableProperty] private bool _cutAbove = true;
    [ObservableProperty] private int _levelFilter;                 // 0 whole building, 1 current + below, 2 current only
    public ViewState State { get; } = new();

    /// <summary>A click in the 3D view selects the model object, and the plan shows it (level + selection are shared).</summary>
    public void Pick3D(Guid id, ImmoCad.Scene.SceneKind kind)
    {
        if (ActiveIndex < 0) return;
        foreach (var l in Building.Levels)
        {
            if (kind == ImmoCad.Scene.SceneKind.Wall && l.Walls.Any(w => w.Id == id)) { CurrentLevelId = l.Id; SelectedWalls.Clear(); SelectedWalls.Add(id); SelectedSegment = null; State.Select(id); Status = T("walls.selected", 1); Refresh(); return; }
        }
        if (kind == ImmoCad.Scene.SceneKind.Pipe)
            foreach (var n in Building.Networks.Where(n => n.Segments.Any(s => s.Id == id))) { SelectSegment(n.Id, id); State.Select(id); return; }
        State.Select(id); Status = kind.ToString();
    }

    /// <summary>"Show in 3D" / "Show in plan" on the selection.</summary>
    [RelayCommand] private void ShowInPlan() { RequestMode(ViewMode.Plan); }
    [RelayCommand] private void ToggleXRay() { XRay = !XRay; Changed?.Invoke(); }
    [RelayCommand] private void ToggleCut() { CutAbove = !CutAbove; Changed?.Invoke(); }
    [RelayCommand] private void CycleLevelFilter() { LevelFilter = (LevelFilter + 1) % 3; Status = LevelFilter switch { 0 => T("filter.all"), 1 => T("filter.below"), _ => T("filter.current") }; Changed?.Invoke(); }
    [RelayCommand] private void CycleView() { RequestMode(Mode switch { ViewMode.Plan => ViewMode.Solid, ViewMode.Solid => ViewMode.Wireframe, _ => ViewMode.Plan }); }

    // ---- Equipment: selection, properties, connect ----
    public Guid? SelectedEquipmentId { get; private set; }
    public Equipment? SelectedEquipment => SelectedEquipmentId is { } id ? Building.Equipment.FirstOrDefault(e => e.Id == id) : null;
    [ObservableProperty] private string _eqLabel = ""; [ObservableProperty] private int _eqElevation; [ObservableProperty] private int _eqRotation;
    [ObservableProperty] private int _eqWidth; [ObservableProperty] private int _eqDepth; [ObservableProperty] private int _eqHeight; [ObservableProperty] private string _eqInfo = "";

    public void SelectEquipment(Guid? id)
    {
        SelectedEquipmentId = id; if (id != null) { SelectedWalls.Clear(); SelectedSegment = null; SelectedOpening = null; SelectedSpaceId = null; }
        if (SelectedEquipment is { } e)
        {
            var cat = EquipmentCatalog.Find(e.CatalogId)?.With(e.Parameters); EqLabel = e.Label ?? ""; EqElevation = e.ElevationMm; EqRotation = e.RotationDeg;
            EqWidth = cat?.WidthMm ?? 0; EqDepth = cat?.DepthMm ?? 0; EqHeight = cat?.HeightMm ?? 0;
            EqInfo = T("eq.info", cat?.Name ?? e.CatalogId, Building.Levels.FirstOrDefault(l => l.Id == e.LevelId)?.Name ?? "", string.Join(", ", cat?.Ports.Select(p => $"{Enum_(p.Network)} DN{p.DiameterMm}") ?? []));
        }
        OnPropertyChanged(nameof(SelectionText)); Changed?.Invoke();
    }
    [RelayCommand] private void ApplyEquipment()
    {
        if (SelectedEquipment is not { } e) return;
        var p = new Dictionary<string, double> { ["width"] = EqWidth, ["depth"] = EqDepth, ["height"] = EqHeight };
        Actions.SetEquipmentProperties(e.Id, EqLabel.Length == 0 ? null : EqLabel, EqElevation, EqRotation, p); Status = T("eq.modified"); var id = e.Id; Refresh(); SelectEquipment(id);
    }
    public void MoveEquipmentTo(Guid id, PointMm to) { Actions.MoveEquipment(id, to); Status = T("eq.moved"); Refresh(); SelectEquipment(id); }

    /// <summary>Connect gesture: port → pipe/riser of the same network type under the release point. One history step.</summary>
    public bool ConnectPort(Guid equipmentId, Port port, PointMm3 portAt, PointMm release)
    {
        var l = CurrentLevel; if (l == null) return false;
        SegmentRef? target = null; double best = 400;
        foreach (var s in NetworkQueries.All(Building).Where(s => s.Network.Type == port.Network && !s.Network.Locked))
        {
            double D(PointMm a, PointMm b2)
            {
                double dx = b2.X - a.X, dy = b2.Y - a.Y; var l2 = dx * dx + dy * dy; var t = l2 == 0 ? 0 : Math.Clamp(((release.X - a.X) * dx + (release.Y - a.Y) * dy) / l2, 0, 1);
                return Math.Sqrt(Math.Pow(release.X - (a.X + t * dx), 2) + Math.Pow(release.Y - (a.Y + t * dy), 2));
            }
            var d = D(new PointMm(s.A.X, s.A.Y), new PointMm(s.B.X, s.B.Y));
            if (d < best && (NetworkQueries.IsVertical(s) ? Math.Min(s.A.Z, s.B.Z) <= portAt.Z && Math.Max(s.A.Z, s.B.Z) >= portAt.Z : s.A.Z == s.B.Z)) { best = d; target = s; }
        }
        if (target == null) { Status = T("connect.release", Enum_(port.Network)); return false; }
        var segs = Actions.ConnectPortToSegment(target.Network.Id, target.Segment.Id, portAt, port.DiameterMm);
        Status = segs == null ? T("connect.diagonal") : T("connect.done", segs.Count); Refresh(); SelectEquipment(equipmentId); return segs != null;
    }

    // ---- Riser extent (from level X to level Y) ----
    public ObservableCollection<string> LevelNames { get; } = new();
    [ObservableProperty] private int _extentFrom; [ObservableProperty] private int _extentTo; [ObservableProperty] private string _extentInfo = "";
    /// <summary>Fills the extent fields from the selected vertical run (levels it spans).</summary>
    private void RefreshExtent()
    {
        LevelNames.Clear(); foreach (var l in Building.Levels) LevelNames.Add(l.DisplayName);
        if (SelectedSegment is not { } sel) { ExtentInfo = ""; return; }
        var n = Building.Networks.FirstOrDefault(x => x.Id == sel.NetworkId); if (n == null || n.Nodes.Count == 0) return;
        var lo = n.Nodes.Min(x => x.Position.Z); var hi = n.Nodes.Max(x => x.Position.Z);
        ExtentFrom = Math.Max(0, Building.Levels.FindLastIndex(l => l.AltitudeMm <= lo)); ExtentTo = Math.Max(0, Building.Levels.FindLastIndex(l => l.AltitudeMm < hi));
        ExtentInfo = T("extent.info", n.Name, (lo / 1000.0).ToString("0.00"), (hi / 1000.0).ToString("0.00"));
    }
    [RelayCommand] private void ApplyExtent()
    {
        if (SelectedSegment is not { } sel || Building.Levels.Count == 0) return;
        var a = Math.Clamp(ExtentFrom, 0, Building.Levels.Count - 1); var b = Math.Clamp(ExtentTo, 0, Building.Levels.Count - 1); var (lo, hi) = (Math.Min(a, b), Math.Max(a, b));
        Actions.SetRiserExtent(sel.NetworkId, Building.Levels[lo].AltitudeMm, Building.Levels[hi].AltitudeMm + Building.Levels[hi].HeightMm); Status = T("extent.changed");
        var keep = sel; Refresh(); SelectSegment(keep.NetworkId, keep.SegmentId);
    }
    /// <summary>Top/bottom handle of a vertical pipe (elevation view): snaps to the level lines. One history step.</summary>
    public bool DragVerticalEnd(Guid networkId, Guid nodeId, int zMm)
    {
        var ok = Actions.SetNodeAltitude(networkId, nodeId, ElevationPlane.SnapZ(Building, zMm, 150)); if (ok) { var s = SelectedSegment; Refresh(); if (s != null) SelectSegment(s.Value.NetworkId, s.Value.SegmentId); }
        return ok;
    }

    // ---- Tools in the 3D view: same view-model entry points as the plan ----
    public bool IsPlaneTool => Tool is ToolKind.Riser or ToolKind.Place or ToolKind.Door or ToolKind.Window or ToolKind.Space or ToolKind.Measure or ToolKind.NumberCellars;
    public void Click3D(PointMm at)
    {
        switch (Tool)
        {
            case ToolKind.Riser: PlaceRiser(at); break;
            case ToolKind.Place: PlaceFromPalette(at); break;
            case ToolKind.Door: PlaceOpening(at, OpeningKind.Door); break;
            case ToolKind.Window: PlaceOpening(at, OpeningKind.Window); break;
            case ToolKind.Space: PlaceSpace(at); break;
            case ToolKind.Measure: MeasureClick(at); break;
            case ToolKind.NumberCellars: NumberClick(at); break;
        }
    }

    // ---- Pipe handles in the 3D view (drag constrained to the pipe's axis) ----
    public ViewportController.PipeHandle3D[] Handles3D()
    {
        if (SelectedSegment is not { } sel || Building.Networks.FirstOrDefault(n => n.Id == sel.NetworkId) is not { } net || net.Segments.FirstOrDefault(s => s.Id == sel.SegmentId) is not { } seg) return [];
        var a = net.Nodes.First(n => n.Id == seg.A); var b = net.Nodes.First(n => n.Id == seg.B);
        ViewportController.PipeHandle3D H(Node end, Node other)
        {
            double dx = end.Position.X - other.Position.X, dy = end.Position.Y - other.Position.Y, dz = end.Position.Z - other.Position.Z; var l = Math.Sqrt(dx * dx + dy * dy + dz * dz); l = l < 1e-9 ? 1 : l;
            return new(net.Id, end.Id, (end.Position.X / 1000.0, end.Position.Y / 1000.0, end.Position.Z / 1000.0), (dx / l, dy / l, dz / l), (other.Position.X / 1000.0, other.Position.Y / 1000.0, other.Position.Z / 1000.0));
        }
        var freeA = net.Segments.Count(s => s.A == a.Id || s.B == a.Id) == 1; var freeB = net.Segments.Count(s => s.A == b.Id || s.B == b.Id) == 1;
        return new[] { freeA ? H(a, b) : null, freeB ? H(b, a) : null }.Where(h => h != null).Select(h => h!).ToArray();
    }

    /// <summary>The handle was dropped: extend/shorten along the axis (one history step); altitudes snap to level lines.</summary>
    public void DropPipeEnd3D(Guid networkId, Guid nodeId, (int X, int Y, int Z) mm)
    {
        var net = Building.Networks.First(n => n.Id == networkId); var node = net.Nodes.First(n => n.Id == nodeId); var z = node.Position.Z != mm.Z ? ElevationPlane.SnapZ(Building, mm.Z, 150) : mm.Z;
        var ok = Actions.ExtendEnd(networkId, nodeId, new PointMm3(Snap50(mm.X), Snap50(mm.Y), z)); Status = ok ? T("pipe.modified") : T("nochange");
        var s = SelectedSegment; Refresh(); if (s != null) SelectSegment(s.Value.NetworkId, s.Value.SegmentId);
    }
    private static int Snap50(int v) => (int)Math.Round(v / 50.0) * 50;

    // ---- Pipe selection (handles) ----
    public (Guid NetworkId, Guid SegmentId)? SelectedSegment { get; private set; }
    public void SelectSegment(Guid networkId, Guid segmentId) { SelectedSpaceId = null; SelectedOpening = null; SelectedWalls.Clear(); SelectedEquipmentId = null; SelectedSegment = (networkId, segmentId); RefreshExtent(); Status = T("pipe.selected.hint"); OnPropertyChanged(nameof(SelectionText)); Changed?.Invoke(); }
    public void ClearSegment() { if (SelectedSegment == null) return; SelectedSegment = null; OnPropertyChanged(nameof(SelectionText)); }
    public int Snapper_GridMm() => 50;
    /// <summary>Snap to the visible grid (View › Snap to grid, F9).</summary>
    public bool SnapToGrid { get; private set; } = true;
    [RelayCommand] private void ToggleSnapToGrid() { SnapToGrid = !SnapToGrid; Status = T(SnapToGrid ? "snap.on" : "snap.off"); OnPropertyChanged(nameof(SnapToGrid)); Changed?.Invoke(); }
    public void AfterEdit(string? message = null) { if (message != null) Status = message; Refresh(); }

    // ---- Network tool options ----
    [ObservableProperty] private NetworkType _riserType = NetworkType.ColdWater;
    [ObservableProperty] private int _riserDiameter = 16;
    [ObservableProperty] private int _riserSpacing = 150;
    [ObservableProperty] private string _riserTemplate = "tpl.simple";
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
        // the objects of the current step come first: valves while connecting, equipment and sanitary while equipping
        int Rank(string category) => Step == WorkStep.Connect ? (category == "Valves" ? 0 : 1) : category == "Valves" ? 1 : 0;
        foreach (var e in EquipmentCatalog.Default.Where(e => Search.Length == 0 || e.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || T("cat." + e.Category).Contains(Search, StringComparison.OrdinalIgnoreCase)).OrderBy(e => Rank(e.Category)))
            Palette.Add(new PaletteItem(e.Id, T("cat." + e.Category), e.Name, e.Inline ? "⊗" : e.Category == "Sanitary" ? "◍" : "▤", Has("desc." + e.Id, Language) ? T("desc." + e.Id) : ""));
    }

    // ---- Elevation view ----
    /// <summary>Shows the front view of the longest face of the selected wall, seen from the side facing the building's centre.</summary>
    [RelayCommand] private void ShowElevation()
    {
        var wall = CurrentLevel?.Walls.FirstOrDefault(w => SelectedWalls.Contains(w.Id));
        if (wall == null || wall.Footprint.Count < 2) { Status = T("elev.select"); return; }
        var fp = wall.Footprint; var best = 0; double bestLen = -1;
        for (var i = 0; i < fp.Count; i++) { var a = fp[i]; var b = fp[(i + 1) % fp.Count]; var len = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)); if (len > bestLen) { bestLen = len; best = i; } }
        var pa = fp[best]; var pb = fp[(best + 1) % fp.Count];
        var all = Building.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).ToList();
        PointMm centre = new((int)all.Average(p => p.X), (int)all.Average(p => p.Y));
        var side = new ElevationPlane(pa, pb, 50, +1).Distance(centre) >= 0 ? +1 : -1;
        Side = null; Plane = new ElevationPlane(pa, pb, 50, side); Mode = ViewMode.Elevation; Hint = T("elev.hint");
    }

    // ---- The plan canvas seen on another plane (XY = plan, XZ / YZ = side views, Z always up). Same tools and selection as the plan. ----
    [ObservableProperty] private ViewPlane _viewPlane = ViewPlane.XY;
    [ObservableProperty] private bool _planeFlip;                       // look from the opposite side (XZ: from the north, YZ: from the west)
    public bool IsSidePlane => ViewPlane != ViewPlane.XY;
    public bool ShowSideControls => IsSidePlane || IsElevation;
    partial void OnViewPlaneChanged(ViewPlane value) { MeasurePoints.Clear(); OnPropertyChanged(nameof(IsSidePlane)); OnPropertyChanged(nameof(ShowSideControls)); ClearSelectionForPlane(); Changed?.Invoke(); }
    partial void OnPlaneFlipChanged(bool value) { Changed?.Invoke(); }
    public ViewSide PlaneSide => ViewPlane == ViewPlane.XZ ? (PlaneFlip ? ViewSide.North : ViewSide.South) : (PlaneFlip ? ViewSide.West : ViewSide.East);
    /// <summary>Maps the side canvas to the building: u along the horizontal axis (grows to the viewer's right), pipes sit <see cref="ElevationDepth"/> inside the outer line.</summary>
    public ElevationPlane SidePlaneModel() => ElevationPlane.ForSide(Building, PlaneSide, ElevationDepth);
    private void ClearSelectionForPlane() { SelectedWalls.Clear(); SelectedSegment = null; SelectedOpening = null; SelectedSpaceId = null; SelectedEquipmentId = null; OnPropertyChanged(nameof(SelectionText)); }
    [RelayCommand] private void SetViewPlane(string plane) { if (Mode != ViewMode.Plan) RequestMode(ViewMode.Plan); ViewPlane = Enum.Parse<ViewPlane>(plane); }
    [RelayCommand] private void FlipPlaneView() => PlaneFlip = !PlaneFlip;
    /// <summary>Plane chosen with the 2D view cube: top = XY, front/back = XZ, right/left = YZ.</summary>
    public void ChoosePlane(ViewPlane plane, bool flip) { PlaneFlip = flip; if (Mode != ViewMode.Plan) RequestMode(ViewMode.Plan); ViewPlane = plane; }

    // ---- How walls are drawn in the 2D views: solid, wireframe (outline only) or hidden, to lay pipes and equipment without the walls in the way ----
    [ObservableProperty] private WallDisplay _wallDisplay = WallDisplay.Solid;
    public string WallDisplayLabel => T("walls.display", T("walls.display." + WallDisplay));
    partial void OnWallDisplayChanged(WallDisplay value) { OnPropertyChanged(nameof(WallDisplayLabel)); Status = WallDisplayLabel; Changed?.Invoke(); }
    [RelayCommand] private void CycleWallDisplay() => WallDisplay = (WallDisplay)(((int)WallDisplay + 1) % 3);

    /// <summary>Pipe drawn on a side view: canvas points are (u, Z).</summary>
    public void CommitPipeSide(IReadOnlyList<PointMm> pts)
    {
        if (pts.Count < 2 || Building.Levels.Count == 0) return;
        var plane = SidePlaneModel(); var net = Building.Networks.FirstOrDefault(n => n.Type == PipeType && !n.Locked) is { } ex ? ex : Actions.AddNetwork(PipeType.ToString(), PipeType);
        Actions.AddPipeRun(net.Id, pts.Select(p => { var xy = plane.ToPlan(p.X); return new PointMm3(xy.X, xy.Y, p.Y); }).ToList(), PipeDiameter);
        Status = T("pipe.segments", pts.Count - 1); Refresh();
    }
    /// <summary>Riser clicked on a side view: a vertical pipe over the whole building at that horizontal position.</summary>
    public void PlaceRiserSide(double u)
    {
        if (Building.Levels.Count == 0) return;
        Actions.AddVerticalPipe(SidePlaneModel(), u, RiserType, Building.Levels[0].AltitudeMm, Building.Levels[^1].AltitudeMm + Building.Levels[^1].HeightMm, RiserDiameter);
        Status = T("elev.vertical"); Refresh();
    }

    // whole-building side views (front / right / back / left), reached from the toolbar; the plan stays the top view
    public ViewSide? Side { get; private set; }
    [ObservableProperty] private int _elevationDepth = 500;
    /// <summary>Side views show only the current level (its floor to its ceiling) when true, the whole building otherwise.</summary>
    [ObservableProperty] private bool _elevationLevelOnly = true;
    partial void OnElevationLevelOnlyChanged(bool value) => Changed?.Invoke();
    public IEnumerable<Level> ElevationLevels() => ElevationLevelOnly && CurrentLevel is { } l ? [l] : Building.Levels;
    public (int Bottom, int Top) ElevationRange() { var ls = ElevationLevels().ToList(); return ls.Count == 0 ? (0, 3000) : (ls.Min(x => x.AltitudeMm), ls.Max(x => x.AltitudeMm + x.HeightMm)); }
    /// <summary>How far from the viewing plane pipes are still drawn: only the ones on the selected wall face, or every pipe for a side view.</summary>
    public int ElevationReach => Side != null ? int.MaxValue / 4 : 600;
    partial void OnElevationDepthChanged(int value) { if (Side is { } s) { Plane = ElevationPlane.ForSide(Building, s, value); Changed?.Invoke(); } }

    [RelayCommand] private void ShowSide(string side)
    {
        if (side == "Top") { RequestMode(ViewMode.Plan); return; }
        if (Building.Levels.Count == 0) return;
        Side = Enum.Parse<ViewSide>(side); Plane = ElevationPlane.ForSide(Building, Side.Value, ElevationDepth); Mode = ViewMode.Elevation; Hint = T("elev.hint"); Changed?.Invoke();
    }
    /// <summary>Walks round the building: +1 = the viewer walks counter-clockwise round the building seen from above (front → right → back → left), -1 = clockwise.</summary>
    [RelayCommand] private void RotateView(int step)
    {
        var cur = Side ?? ViewSide.South; ShowSide(((ViewSide)(((int)cur + step + 4) % 4)).ToString());
    }

    /// <summary>Vertical pipe on the face; whole building by default, snapped to level lines when dragged.</summary>
    public void PlaceVerticalPipe(double u, double? zFrom, double? zTo)
    {
        if (Plane == null || Building.Levels.Count == 0) return;
        var lo = (int)(zFrom ?? Building.Levels[0].AltitudeMm); var hi = (int)(zTo ?? Building.Levels[^1].AltitudeMm + Building.Levels[^1].HeightMm);
        Actions.AddVerticalPipe(Plane, Math.Clamp(u, 0, Plane.Length), RiserType, Math.Min(lo, hi), Math.Max(lo, hi), RiserDiameter);
        Status = T("elev.vertical"); Refresh();
    }

    public void PlaceHorizontalPipe(double u0, double u1, double z)
    {
        if (Plane == null || Math.Abs(u1 - u0) < 20) return;
        Actions.AddHorizontalAlong(Plane, Math.Clamp(u0, 0, Plane.Length), Math.Clamp(u1, 0, Plane.Length), PipeType, (int)z, PipeDiameter);
        Status = T("elev.horizontal"); Refresh();
    }

    // ---- Doors, windows, rooms, cellars, measure ----
    [ObservableProperty] private int _openingWidth;                 // 0 = default (800 apartments, 700 cellars, 1200 windows)
    [ObservableProperty] private string _cellarPrefix = T("cellar.prefix");
    [ObservableProperty] private int _cellarStart = 1;
    [ObservableProperty] private SpaceKind _newSpaceKind = SpaceKind.Room;
    [ObservableProperty] private int _propThickness;
    public List<PointMm> MeasurePoints { get; } = new();
    public List<Guid> NumberPending { get; } = new();
    public event Action<PointMm>? FocusRequested;
    public event Action? ViewFitRequested;

    public (Wall Wall, Opening Opening)? HitOpening(PointMm p)
    {
        if (CurrentLevel is not { } l) return null;
        foreach (var w in l.Walls)
        {
            if (Geometry_Axis(w) is not { } ax) continue;
            foreach (var o in w.Openings)
            {
                var c = ax.PointAt(o.PositionMm); var half = Math.Max(o.WidthMm / 2, 150);
                if (Math.Abs(p.X - c.X) <= (Math.Abs(ax.B.X - ax.A.X) > Math.Abs(ax.B.Y - ax.A.Y) ? half : ax.ThicknessMm) && Math.Abs(p.Y - c.Y) <= (Math.Abs(ax.B.X - ax.A.X) > Math.Abs(ax.B.Y - ax.A.Y) ? ax.ThicknessMm : half)) return (w, o);
            }
        }
        return null;
    }
    private static WallAxis? Geometry_Axis(Wall w) => WallGeometry.Axis(w);

    /// <summary>What a click at this point would do with the door/window tool: the wall, position and width of the new opening, or null on a flip / when no wall is under the cursor.</summary>
    public (Wall Wall, int Pos, int Width)? OpeningPreview(PointMm at, OpeningKind kind)
    {
        if (CurrentLevel is not { } l) return null;
        if (kind == OpeningKind.Door && HitOpening(at) is { } hit && hit.Opening.Kind == OpeningKind.Door) return null;
        var wall = l.Walls.LastOrDefault(w => WallGeometry.PositionAlong(w, at) != null); if (wall == null) return null;
        var width = OpeningWidth > 0 ? OpeningWidth : kind == OpeningKind.Door ? (l.Kind == LevelKind.Basement ? 700 : 800) : 1200;
        return (wall, (int)Math.Round(WallGeometry.PositionAlong(wall, at)!.Value), width);
    }

    /// <summary>Door/window tool: a click on a wall inserts the opening; a click on an existing door flips its swing.</summary>
    public void PlaceOpening(PointMm click, OpeningKind kind)
    {
        if (CurrentLevel is not { } l) return;
        if (kind == OpeningKind.Door && HitOpening(click) is { } hit && hit.Opening.Kind == OpeningKind.Door) { Actions.FlipOpening(l.Id, hit.Wall.Id, hit.Opening.Id); Status = T("door.flipped"); Refresh(); return; }
        var wall = l.Walls.LastOrDefault(w => WallGeometry.PositionAlong(w, click) != null);
        if (wall == null) { Status = T("opening.clickwall"); return; }
        var pos = (int)Math.Round(WallGeometry.PositionAlong(wall, click)!.Value);
        var o = Actions.AddOpening(l.Id, wall.Id, kind, pos, OpeningWidth > 0 ? OpeningWidth : null);
        Status = kind == OpeningKind.Door ? T("door.added", o.WidthMm) : T("window.added"); Refresh();
    }

    // ---- Room (space) selection and properties ----
    private Guid? _selectedSpaceId;
    public Guid? SelectedSpaceId { get => _selectedSpaceId; private set { _selectedSpaceId = value; OnPropertyChanged(nameof(SelectedSpaceVisible)); OnPropertyChanged(nameof(SelectionText)); } }
    public bool SelectedSpaceVisible => _selectedSpaceId != null;
    public Space? SelectedSpaceModel => SelectedSpaceId is { } id ? CurrentLevel?.Spaces.FirstOrDefault(x => x.Id == id) : null;
    [ObservableProperty] private string _spaceName = ""; [ObservableProperty] private string _spaceNumber = ""; [ObservableProperty] private string _spaceLot = ""; [ObservableProperty] private SpaceKind _spaceKind = SpaceKind.Room;

    /// <summary>The room containing this point (the last drawn one wins), or null.</summary>
    public Space? SpaceAt(PointMm at) => CurrentLevel?.Spaces.LastOrDefault(x => SpaceFinder.Contains(x.Outline, at));

    public void SelectSpace(Guid id)
    {
        SelectedWalls.Clear(); SelectedSegment = null; SelectedEquipmentId = null; SelectedOpening = null; SelectedSpaceId = id;
        if (SelectedSpaceModel is { } sp) { SpaceName = sp.DisplayName; SpaceNumber = sp.Number ?? ""; SpaceLot = sp.Lot ?? ""; SpaceKind = sp.Kind; }
        Status = T("space.selected"); Changed?.Invoke();
    }
    public void ClearSpace() { if (SelectedSpaceId == null) return; SelectedSpaceId = null; Changed?.Invoke(); }

    [RelayCommand] private void ApplySpace()
    {
        if (SelectedSpaceModel is not { } sp || CurrentLevel is not { } l) return;
        var name = SpaceName.Trim() == sp.DisplayName ? sp.Name : SpaceName.Trim();   // untouched generated names stay tokens (follow the language)
        Actions.SetSpaceProperties(l.Id, sp.Id, name.Length == 0 ? sp.Name : name, SpaceKind, SpaceNumber.Length == 0 ? null : SpaceNumber, SpaceLot.Length == 0 ? null : SpaceLot);
        Refresh(); SelectSpace(sp.Id); Status = T("space.modified");
    }

    // ---- Door / window selection and properties ----
    private (Guid WallId, Guid OpeningId)? _selectedOpening;
    public (Guid WallId, Guid OpeningId)? SelectedOpening { get => _selectedOpening; private set { _selectedOpening = value; OnPropertyChanged(nameof(SelectedOpeningVisible)); } }
    public bool SelectedOpeningVisible => _selectedOpening != null;
    public Opening? SelectedOpeningModel => SelectedOpening is { } s ? CurrentLevel?.Walls.FirstOrDefault(w => w.Id == s.WallId)?.Openings.FirstOrDefault(o => o.Id == s.OpeningId) : null;
    [ObservableProperty] private int _opWidth; [ObservableProperty] private int _opHeight; [ObservableProperty] private int _opSill; [ObservableProperty] private string _opLabel = "";
    [ObservableProperty] private bool _opSwingLeft; [ObservableProperty] private bool _opSwingOut;
    public void SelectOpening(Guid wallId, Guid openingId)
    {
        SelectedSpaceId = null; SelectedWalls.Clear(); SelectedSegment = null; SelectedEquipmentId = null; SelectedOpening = (wallId, openingId);
        if (SelectedOpeningModel is { } o) { OpWidth = o.WidthMm; OpHeight = o.HeightMm; OpSill = o.SillMm; OpLabel = o.Label ?? ""; OpSwingLeft = o.SwingLeft; OpSwingOut = o.SwingOut; }
        Status = T("opening.selected"); OnPropertyChanged(nameof(SelectionText)); Changed?.Invoke();
    }
    public void ClearOpening() { if (SelectedOpening == null) return; SelectedOpening = null; OnPropertyChanged(nameof(SelectionText)); }
    [RelayCommand] private void ApplyOpening()
    {
        if (SelectedOpening is not { } s || CurrentLevel is not { } l) return;
        Actions.SetOpeningProperties(l.Id, s.WallId, s.OpeningId, OpWidth, OpHeight, OpSill, OpSwingLeft, OpSwingOut, OpLabel.Length == 0 ? null : OpLabel);
        Refresh(); SelectOpening(s.WallId, s.OpeningId); Status = T("opening.modified");
    }

    public void MoveOpeningTo(Wall wall, Opening o, PointMm to)
    {
        if (CurrentLevel is not { } l || WallGeometry.PositionAlong(wall, to, 2000) is not { } pos) return;
        Actions.MoveOpening(l.Id, wall.Id, o.Id, (int)Math.Round(pos)); Refresh(); SelectOpening(wall.Id, o.Id); Status = T("opening.moved");
    }

    /// <summary>Space tool: flood-fill from the click inside the walls; refused with a hint when the room is not closed.</summary>
    public void PlaceSpace(PointMm click)
    {
        if (CurrentLevel is not { } l) return;
        var outline = SpaceFinder.Find(l, click);
        if (outline == null) { Status = T("space.open"); return; }
        var n = l.Spaces.Count + 1; var sp = Actions.AddSpace(l.Id, outline, Token("space.default.name", n), NewSpaceKind);
        Refresh(); SelectSpace(sp.Id); Status = T("space.added", SpaceFinder.AreaMm2(sp.Outline) / 1e6);
    }

    public void NumberClick(PointMm click)
    {
        if (CurrentLevel is not { } l) return;
        var sp = l.Spaces.LastOrDefault(s => SpaceFinder.Contains(s.Outline, click));
        if (sp == null) { Status = T("space.clickin"); return; }
        if (!NumberPending.Remove(sp.Id)) NumberPending.Add(sp.Id);
        Status = T("number.pending", NumberPending.Count); Changed?.Invoke();
    }
    public void NumberCommit()
    {
        if (CurrentLevel is not { } l || NumberPending.Count == 0) return;
        var ids = NumberPending.ToList(); Actions.NumberSpaces(l.Id, ids, CellarPrefix, CellarStart); NumberPending.Clear();
        CellarStart += ids.Count; Status = T("spaces.numbered", ids.Count); Refresh();
    }

    /// <summary>Measured two points, typed the real distance: the whole level (plan and walls) is rescaled about the first point. One history step.</summary>
    [RelayCommand] private async Task MeasureScaleAsync()
    {
        if (MeasurePoints.Count != 2 || CurrentLevel is not { } l) return;
        var (a, b) = (MeasurePoints[0], MeasurePoints[1]); var measured = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) / 1000;
        if (measured < 0.01) return;
        var text = await Dialogs.PromptAsync(T("measure.scale.title"), T("measure.scale.label", measured.ToString("0.00")), measured.ToString("0.00"));
        if (text == null || !double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real) || real <= 0.01) return;
        var f = real / measured; Actions.ScaleLevel(l.Id, f, f, moveWalls: true, a.X, a.Y);
        MeasurePoints.Clear(); Status = T("measure.scale.done", (f * 100).ToString("0.#")); Refresh();
    }

    public void MeasureClick(PointMm p)
    {
        if (MeasurePoints.Count >= 2) MeasurePoints.Clear();
        MeasurePoints.Add(p);
        if (MeasurePoints.Count == 2) Status = T("measure.result", Math.Sqrt(Math.Pow(MeasurePoints[0].X - p.X, 2) + Math.Pow(MeasurePoints[0].Y - p.Y, 2)) / 1000);   // nothing is added to the model
        Changed?.Invoke();
    }

    [RelayCommand] private void FocusSpace(Guid id) { var s = SpaceItems.FirstOrDefault(x => x.Id == id); if (s != null) { if (s.LevelId != CurrentLevelId) SelectLevel(s.LevelId); FocusRequested?.Invoke(s.Center); } }

    // ---- Level context menu ----
    [RelayCommand] private void AddLevelAbove(Guid id) { var n = Actions.AddLevelNextTo(id, true, Token("level.n", Building.Levels.Count)); CurrentLevelId = n.Id; Refresh(); }
    [RelayCommand] private void AddLevelBelow(Guid id) { var n = Actions.AddLevelNextTo(id, false, Token("level.basement.n", Building.Levels.Count(l => l.AltitudeMm < 0) + 1)); CurrentLevelId = n.Id; Refresh(); }
    /// <summary>Deletes a level with its walls, openings, spaces and background plan (undoable, one history step).</summary>
    [RelayCommand] private void DeleteLevel(Guid id)
    {
        var l = Building.Levels.FirstOrDefault(x => x.Id == id); if (l == null) return;
        var name = l.DisplayName; var next = Building.Levels.Where(x => x.Id != id).OrderBy(x => Math.Abs(x.AltitudeMm - l.AltitudeMm)).FirstOrDefault();
        Actions.DeleteLevel(id); SelectedWalls.Clear(); SelectedSegment = null;
        if (CurrentLevelId == id && next != null) CurrentLevelId = next.Id;
        Status = T("level.deleted", name); Refresh();
    }
    [RelayCommand] private void DeleteCurrentLevel() { if (CurrentLevel is { } l) DeleteLevel(l.Id); }

    [RelayCommand] private async Task RenameLevelAsync(Guid id)
    {
        var l = Building.Levels.First(x => x.Id == id);
        if (await Dialogs.PromptAsync(T("dlg.rename.title"), T("dlg.rename.label"), l.DisplayName) is { Length: > 0 } name) { Actions.RenameLevel(id, name); Refresh(); }
    }
    [RelayCommand] private async Task SetLevelGeometryAsync(Guid id)
    {
        var l = Building.Levels.First(x => x.Id == id);
        var text = await Dialogs.PromptAsync(T("dlg.geom.title"), T("dlg.geom.label"), $"{l.AltitudeMm / 1000.0:0.00} ; {l.HeightMm / 1000.0:0.00}".Replace(',', '.'));
        var parts = text?.Split(';', StringSplitOptions.TrimEntries);
        if (parts is { Length: 2 } && double.TryParse(parts[0].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var alt) && double.TryParse(parts[1].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0)
        { Actions.SetLevelGeometry(id, (int)Math.Round(alt * 1000), (int)Math.Round(h * 1000)); Refresh(); }
    }
    [RelayCommand] private void SetLevelKind(string spec) { var parts = spec.Split(':'); Actions.SetLevelKind(Guid.Parse(parts[0]), Enum.Parse<LevelKind>(parts[1])); Refresh(); }
    [RelayCommand] private void ApplyThickness()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0 || PropThickness < 20) return;
        var n = Actions.SetWallThickness(l.Id, SelectedWalls.ToList(), PropThickness); Status = n > 0 ? T("thickness.set", PropThickness) : T("thickness.rect"); Refresh();
    }

    // ---- Risers ----
    private (List<NetworkType> Types, int Spacing) RiserTypes() => RiserTemplate switch
    {
        "tpl.dhw" => ([NetworkType.ColdWater, NetworkType.DomesticHotWater], RiserSpacing),
        "tpl.loop" => ([NetworkType.ColdWater, NetworkType.DomesticHotWater, NetworkType.DhwReturn], RiserSpacing),
        "tpl.drain" => ([NetworkType.Drainage], 0),
        "tpl.heating" => ([NetworkType.HeatingSupply, NetworkType.HeatingReturn], RiserSpacing),
        "tpl.all" => ([NetworkType.ColdWater, NetworkType.DomesticHotWater, NetworkType.Drainage], RiserSpacing),
        _ => (new List<NetworkType> { RiserType }, 0),
    };

    /// <summary>Quarter turns (counter-clockwise) of a multiple riser about the cursor: east, north, west, south. Space turns it while the riser tool is active.</summary>
    public int RiserTurns { get; private set; }
    public void TurnRiser() { RiserTurns = (RiserTurns + 1) % 4; Status = T("riser.turned", RiserTurns * 90); }

    /// <summary>The tubes a click would place at this point (snapped like the real placement): position, type and diameter of each.</summary>
    public List<(PointMm At, NetworkType Type, int DiameterMm)> RiserPreview(PointMm at)
    {
        if (Building.Levels.Count == 0) return [];
        var (types, spacing) = RiserTypes(); var dia = RiserTemplate == "tpl.drain" ? Math.Max(RiserDiameter, 100) : RiserDiameter;
        var p = RiserSnap.SnapRow(at, spacing, types.Count, dia, CurrentLevel?.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint) ?? [], quarterTurns: RiserTurns); var (sx, sy) = RiserSnap.RowStep(RiserTurns);
        return types.Select((t, i) => (new PointMm(p.X + sx * i * spacing, p.Y + sy * i * spacing), t, dia)).ToList();
    }

    /// <summary>Places a riser (or a template of several) at the point, snapped 50 mm from wall faces. One history step.</summary>
    public void PlaceRiser(PointMm at)
    {
        if (Building.Levels.Count == 0) return;
        var polys = CurrentLevel?.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint) ?? [];
        var (types, spacing) = RiserTypes(); var dia = RiserTemplate == "tpl.drain" ? Math.Max(RiserDiameter, 100) : RiserDiameter;
        var p = RiserSnap.SnapRow(at, spacing, types.Count, dia, polys, quarterTurns: RiserTurns);
        var lo = Math.Clamp(RiserFromIndex, 0, Building.Levels.Count - 1); var hi = Math.Clamp(RiserToIndex, 0, Building.Levels.Count - 1);
        var from = RiserWholeBuilding ? Building.Levels[0].AltitudeMm : Building.Levels[Math.Min(lo, hi)].AltitudeMm;
        var to = RiserWholeBuilding ? Building.Levels[^1].AltitudeMm + Building.Levels[^1].HeightMm : Building.Levels[Math.Max(lo, hi)].AltitudeMm + Building.Levels[Math.Max(lo, hi)].HeightMm;
        Actions.AddRiserTemplate(p, spacing, types, from, to, dia, RiserTurns);
        Status = T("riser.placed", types.Count); Refresh();
    }

    // ---- Horizontal pipes (plan): constrained to X/Y at the chosen height above the floor ----
    public void CommitPipe(IReadOnlyList<PointMm> pts)
    {
        if (CurrentLevel is not { } l || pts.Count < 2) return;
        var z = l.AltitudeMm + PipeElevation;
        var net = Building.Networks.FirstOrDefault(n => n.Type == PipeType && !n.Locked) is { } ex ? ex : Actions.AddNetwork(PipeType.ToString(), PipeType);
        Actions.AddPipeRun(net.Id, pts.Select(p => new PointMm3(p.X, p.Y, z)).ToList(), PipeDiameter);
        Status = T("pipe.segments", pts.Count - 1); Refresh();
    }

    // ---- Palette objects ----
    public string? PlacementHint(PointMm at)
    {
        var e = PaletteId == null ? null : EquipmentCatalog.Find(PaletteId); if (e == null || CurrentLevel is not { } l) return null;
        if (!e.Inline) return null;
        return FindPipeFor(at, l, e) is null ? T("valve.onpipe") : null;
    }

    /// <summary>Where the palette object would land at this point (same snapping as the real placement). Inline parts (valves…) land on the nearest pipe; <c>Valid</c> is false when there is none.</summary>
    public (CatalogEntry Entry, PointMm At, bool Valid)? PalettePreview(PointMm at)
    {
        var e = PaletteId == null ? null : EquipmentCatalog.Find(PaletteId); if (e == null || CurrentLevel is not { } l) return null;
        if (e.Inline) return FindPipeFor(at, l, e) is { } hit ? (e, new PointMm(hit.P.X, hit.P.Y), true) : (e, at, false);
        return (e, RiserSnap.Snap(at, l.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint), offsetMm: e.DepthMm / 2, reachMm: 400), true);
    }

    /// <summary>Outline of the room a click would create with the space tool (null when the walls are not closed there).</summary>
    public List<PointMm>? SpacePreview(PointMm at) => CurrentLevel is { } l ? SpaceFinder.Find(l, at) : null;

    private (SegmentRef Seg, PointMm3 P)? FindPipeFor(PointMm at, Level l, CatalogEntry e)
    {
        var near = NetworkQueries.Nearest(Building, new PointMm3(at.X, at.Y, l.AltitudeMm + PipeElevation), 250, s => !s.Network.Locked && !NetworkQueries.IsVertical(s) && s.A.Z >= l.AltitudeMm && s.A.Z < l.AltitudeMm + l.HeightMm);
        near ??= NetworkQueries.Nearest(Building, new PointMm3(at.X, at.Y, l.AltitudeMm + 1000), 250, s => !s.Network.Locked && NetworkQueries.IsVertical(s));
        return near is { } n && PlacementRules.Check(e, n.Point, n.Segment.Network).Valid ? (n.Segment, n.Point) : null;
    }

    public void PlaceFromPalette(PointMm at)
    {
        if (PaletteId == null || CurrentLevel is not { } l) { Status = T("palette.pick"); return; }
        var e = EquipmentCatalog.Find(PaletteId)!;
        if (e.Inline)
        {
            if (FindPipeFor(at, l, e) is not { } hit) { Status = T("valve.onpipe"); return; }
            Actions.InsertComponent(hit.Seg.Network.Id, hit.Seg.Segment.Id, hit.P, e.Id); Status = T("placed.onpipe", e.Name);
        }
        else
        {
            // snap against the nearest wall face, oriented along it; default height from the catalog
            var polys = l.Walls.Select(w => (IReadOnlyList<PointMm>)w.Footprint); var p = RiserSnap.Snap(at, polys, offsetMm: e.DepthMm / 2, reachMm: 400);
            Actions.AddEquipment(l.Id, e.Id, p, e.DefaultElevationMm); Status = T("placed", e.Name);
        }
        Recent.Remove(e.Id); Recent.Insert(0, e.Id); if (Recent.Count > 6) Recent.RemoveAt(6);
        Refresh();
    }

    [RelayCommand] private void SelectPalette(string id) { PaletteId = id; Tool = ToolKind.Place; Hint = EquipmentCatalog.Find(id)?.Inline == true ? T("palette.hint.inline") : T("palette.hint.object"); }
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
        Actions.SetWallProperties(l.Id, SelectedWalls.ToList(), PropLabel.Length == 0 ? null : PropLabel, PropHeight <= 0 ? null : PropHeight); Status = T("props.applied"); Refresh();
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
        Hint = AlignPending != null ? T("align.hint.second") : AlignPairs.Count < 2 ? T("align.hint.first") : T("align.hint.apply");
        Changed?.Invoke();
    }
    private void UpdateAlignPreview() => AlignResiduals = ImmoCad.Core.Geometry.ImageFit.Fit(AlignPairs) is { } f ? ImmoCad.Core.Geometry.ImageFit.Residuals(f, AlignPairs) : null;

    public void AlignUndoPair() { if (AlignPairs.Count > 0) AlignPairs.RemoveAt(AlignPairs.Count - 1); AlignPending = null; UpdateAlignPreview(); Changed?.Invoke(); }
    public void AlignCancel() { AlignPairs.Clear(); AlignPending = null; AlignResiduals = null; Changed?.Invoke(); }

    /// <summary>Applies the fit as one history step; returns false when degenerate (e.g. collinear points).</summary>
    public bool AlignApply()
    {
        if (CurrentLevel is not { } l || AlignPairs.Count < 2) return false;
        var res = Actions.AlignLevel(l.Id, AlignPairs.ToList(), AlignMoveWalls);
        if (res == null) { Status = T("align.collinear"); return false; }
        Status = T("align.done", res.Max().ToString("0")); AlignCancel(); Refresh(); return true;
    }
    public string? FilePath => ActiveIndex >= 0 ? Active.FilePath : null;
    public event Action? Changed;

    /// <summary>Always resolved by id: undo/redo rebuilds the whole object graph, so references go stale.</summary>
    public Level? CurrentLevel => Building.Levels.FirstOrDefault(l => l.Id == CurrentLevelId) ?? Building.Levels.FirstOrDefault();
    public bool Is3D => Mode is ViewMode.Solid or ViewMode.Wireframe;
    public bool IsElevation => Mode == ViewMode.Elevation;
    public bool IsPlan => Mode == ViewMode.Plan;
    /// <summary>Plan on the left, 3D on the right, both driven by the same level and selection.</summary>
    [ObservableProperty] private bool _sideBySide;
    partial void OnSideBySideChanged(bool value) { OnPropertyChanged(nameof(ShowPlan)); OnPropertyChanged(nameof(Show3D)); Changed?.Invoke(); }
    public bool ShowPlan => IsPlan || Is3D && SideBySide;
    public bool Show3D => Is3D || IsPlan && SideBySide;
    public bool IsSite => Mode == ViewMode.Site;
    public static readonly Guid SiteLevelId = new("00000000-0000-0000-0000-00000000517e");
    public string Title => ActiveIndex < 0 ? "ImmoCad" : $"{(History.IsDirty ? "• " : "")}{Active.Title} — ImmoCad";
    public ElevationPlane? Plane { get; private set; }
    public bool IsSelectTool => Tool == ToolKind.Select;
    public bool IsDrawTool => Tool == ToolKind.DrawWall;
    public bool IsAlignTool => Tool == ToolKind.Align;
    public bool IsRiserTool => Tool == ToolKind.Riser;
    public bool IsPipeTool => Tool == ToolKind.DrawPipe;
    public bool IsPlaceTool => Tool == ToolKind.Place;
    public bool IsDoorTool => Tool == ToolKind.Door;
    public bool IsWindowTool => Tool == ToolKind.Window;
    public bool IsSpaceTool => Tool == ToolKind.Space;
    public bool IsMeasureTool => Tool == ToolKind.Measure;
    public bool IsNumberTool => Tool == ToolKind.NumberCellars;
    public bool IsStraightenTool => Tool == ToolKind.Straighten;
    public bool IsStretchTool => Tool == ToolKind.Stretch;
    public string SelectionText => SelectedEquipment is { } se ? T("eq.selected", EquipmentCatalog.Find(se.CatalogId)?.Name ?? se.CatalogId) : SelectedSpaceId != null ? T("space.selected") : SelectedOpening != null ? T("opening.selected") : SelectedSegment != null ? T("pipe.selected") : SelectedWalls.Count == 0 ? T("no.selection") : T("walls.selected", SelectedWalls.Count);

    public MainViewModel() : this(startWithSample: true) { }

    /// <summary>startWithSample=false shows the welcome screen (New / Open / Recent) until a document exists.</summary>
    public MainViewModel(bool startWithSample)
    {
        RebuildPalette(); RebuildSteps(); Translations.LanguageChanged += OnLanguageChanged;
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

    partial void OnModeChanged(ViewMode value) { if (value == ViewMode.Site) { FocusSite(); PanelTab = 4; } OnPropertyChanged(nameof(ShowSideControls)); OnPropertyChanged(nameof(Is3D)); OnPropertyChanged(nameof(ShowPlan)); OnPropertyChanged(nameof(Show3D)); OnPropertyChanged(nameof(IsElevation)); OnPropertyChanged(nameof(IsPlan)); OnPropertyChanged(nameof(IsSite)); Changed?.Invoke(); }
    partial void OnToolChanged(ToolKind value)
    {
        OnPropertyChanged(nameof(IsSelectTool)); OnPropertyChanged(nameof(IsDrawTool)); OnPropertyChanged(nameof(IsAlignTool)); OnPropertyChanged(nameof(IsRiserTool)); OnPropertyChanged(nameof(IsPipeTool)); OnPropertyChanged(nameof(IsPlaceTool)); OnPropertyChanged(nameof(IsDoorTool)); OnPropertyChanged(nameof(IsWindowTool)); OnPropertyChanged(nameof(IsSpaceTool)); OnPropertyChanged(nameof(IsMeasureTool)); OnPropertyChanged(nameof(IsNumberTool)); OnPropertyChanged(nameof(IsStraightenTool)); OnPropertyChanged(nameof(IsStretchTool)); OnPropertyChanged(nameof(IsCutWallTool)); OnPropertyChanged(nameof(IsMergeRegionTool)); CutPoints.Clear(); CutAwaitSide = false; MeasurePoints.Clear(); NumberPending.Clear();
        Hint = value switch { ToolKind.DrawWall => T("hint.draw.wall"), ToolKind.Riser => T("hint.riser"), ToolKind.DrawPipe => T("hint.draw.pipe"), ToolKind.Place => T("hint.place"), ToolKind.Align => T("hint.align"), ToolKind.Door => T("hint.door"), ToolKind.Window => T("hint.window"), ToolKind.Space => T("hint.space"), ToolKind.Measure => T("hint.measure"), ToolKind.NumberCellars => T("hint.number"), ToolKind.Straighten => T("hint.straighten"), ToolKind.Stretch => T("hint.stretch"), ToolKind.CutWall => T("hint.cutwall"), ToolKind.MergeRegion => T("hint.mergeregion"), _ => T("hint.select") }; if (value != ToolKind.Align) AlignCancel();
        Changed?.Invoke();
    }

    /// <summary>Rebuild derived state after any model change.</summary>
    public void Refresh()
    {
        Tabs.Clear(); for (var i = 0; i < Docs.Count; i++) Tabs.Add(new TabItem(i, Docs[i].Title, i == ActiveIndex, Docs[i].History.IsDirty));
        OnPropertyChanged(nameof(Title));
        if (ActiveIndex < 0) { Levels.Clear(); Networks.Clear(); HistoryRows.Clear(); Changed?.Invoke(); return; }
        HistoryRows.Clear(); HistoryRows.Add(new HistoryRow(-1, T("history.initial"), History.Pointer == -1, false));
        for (var i = 0; i < History.Items.Count; i++) HistoryRows.Add(new HistoryRow(i, T("action." + History.Items[i].Name), i == History.Pointer, i > History.Pointer));
        SelectedWalls.IntersectWith(CurrentLevel?.Walls.Select(w => w.Id) ?? []);
        Levels.Clear();
        foreach (var l in Building.Levels.AsEnumerable().Reverse()) // top level first, like an elevator
            Levels.Add(new LevelItem(l.Id, l.DisplayName, $"{l.AltitudeMm / 1000.0:0.0} m", l.Id == CurrentLevel?.Id, l.Walls.Count, T("level.tooltip", l.DisplayName, Enum_(l.Kind), (l.AltitudeMm / 1000.0).ToString("0.00"), (l.HeightMm / 1000.0).ToString("0.00")), HiddenLevels.Contains(l.Id)));
        Networks.Clear();
        foreach (var n in Building.Networks)
        { var c = NetworkQueries.ColorOf(n.Type); var where = string.Join(", ", n.Segments.SelectMany(sg => Spaces.CrossedBy(Building, n, sg)).Select(sp => sp.DisplayName).Distinct()); Networks.Add(new NetworkItem(n.Id, n.Name, Enum_(n.Type), n.Visible, n.Locked, $"#{c.R:X2}{c.G:X2}{c.B:X2}", where.Length > 0 ? T("network.runs", where) : "")); }
        SpaceItems.Clear();
        foreach (var lv in Building.Levels.AsEnumerable().Reverse())
            foreach (var sp in lv.Spaces)
            {
                var c = new PointMm((int)sp.Outline.Average(p => p.X), (int)sp.Outline.Average(p => p.Y));
                SpaceItems.Add(new SpaceItem(lv.Id, sp.Id, sp.DisplayName, sp.Number ?? "", Enum_(sp.Kind), sp.Lot ?? "", T("space.area.fmt", (SpaceFinder.AreaMm2(sp.Outline) / 1e6).ToString("0.0")), sp.Stability == Stability.Common, c));
            }
        if (CurrentLevel is { } cl)
        {
            _bgOpacity = cl.BackgroundOpacity; _bgVisible = cl.BackgroundVisible; OnPropertyChanged(nameof(BgOpacity)); OnPropertyChanged(nameof(BgVisible));
            if (cl.ImageTransform is { Length: 6 } m) { var d = ImageStretch.Decompose(ImageStretch.FromArray(m)); ImgScaleX = Math.Round(d.ScaleX, 4); ImgScaleY = Math.Round(d.ScaleY, 4); ImgRotation = Math.Round(d.RotationDeg, 3); ImgOffsetX = Math.Round(d.OffsetX, 1); ImgOffsetY = Math.Round(d.OffsetY, 1); }
        }
        var sel = CurrentLevel?.Walls.FirstOrDefault(w => SelectedWalls.Contains(w.Id));
        if (sel != null) { PropLabel = sel.Label ?? ""; PropHeight = sel.HeightMm ?? 0; PropThickness = WallGeometry.Axis(sel)?.ThicknessMm ?? 0; }
        Levels.Add(new LevelItem(SiteLevelId, T("level.site"), T("level.site.alt"), IsSite, 0, T("site.tooltip")));
        OnPropertyChanged(nameof(CurrentLevel)); OnPropertyChanged(nameof(SelectionText));
        Changed?.Invoke();
    }

    public void SelectWalls(IEnumerable<Guid> ids, bool add)
    {
        if (ids.Any()) { SelectedSegment = null; SelectedEquipmentId = null; SelectedOpening = null; SelectedSpaceId = null; }
        if (!add) SelectedWalls.Clear();
        foreach (var id in ids) SelectedWalls.Add(id);
        Status = SelectionText; OnPropertyChanged(nameof(SelectionText)); Changed?.Invoke();
    }

    /// <summary>Whole drag = one history step.</summary>
    public void MoveSelection(int dx, int dy)
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0 || (dx == 0 && dy == 0)) return;
        Actions.MoveWalls(l.Id, SelectedWalls.ToList(), dx, dy); Status = T("walls.moved.by", (dx / 1000.0).ToString("0.00"), (dy / 1000.0).ToString("0.00")); Refresh();
    }

    [RelayCommand] private void SelectLevel(Guid id)
    {
        if (id == SiteLevelId) { RequestMode(ViewMode.Site); Refresh(); return; }
        if (IsSite) Mode = ViewMode.Plan;
        SelectLevelCore(id);
    }
    private void SelectLevelCore(Guid id) { CurrentLevelId = id; SelectedWalls.Clear(); SelectedSegment = null; Status = $"{CurrentLevel?.DisplayName}"; Refresh(); }
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
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) { Status = T("status.need.selection"); return; }
        var i = Building.Levels.FindIndex(x => x.Id == l.Id) + d;
        if (i < 0 || i >= Building.Levels.Count) { Status = T("status.no.level.there"); return; }
        var target = Building.Levels[i]; var ids = SelectedWalls.ToList();
        Actions.MoveWalls(l.Id, ids, 0, 0, target.Id);
        CurrentLevelId = target.Id; SelectedWalls.Clear(); foreach (var id in ids) SelectedWalls.Add(id);
        Status = T("status.walls.moved", ids.Count, target.DisplayName); Refresh();
    }

    [RelayCommand] private void CopySelectionToAllLevels()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) return;
        Actions.CopyWalls(l.Id, SelectedWalls.ToList(), Building.Levels.Select(x => x.Id)); Status = T("walls.copied"); Refresh();
    }

    [RelayCommand] private void DeleteSelection()
    {
        if (SelectedOpening is { } so && CurrentLevel is { } ol) { Actions.DeleteOpening(ol.Id, so.WallId, so.OpeningId); SelectedOpening = null; Status = T("deleted"); Refresh(); return; }
        if (SelectedSegment is { } sg) { Actions.DeleteSegment(sg.NetworkId, sg.SegmentId); SelectedSegment = null; Status = T("deleted"); Refresh(); OnPropertyChanged(nameof(SelectionText)); return; }
        if (SelectedEquipmentId is { } eq) { Actions.DeleteEquipment(eq); SelectEquipment(null); Status = T("deleted"); Refresh(); return; }
        if (SelectedSpaceId is { } sp && CurrentLevel is { } sl) { Actions.DeleteSpace(sl.Id, sp); SelectedSpaceId = null; Status = T("deleted"); Refresh(); return; }
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) return;
        Actions.DeleteWalls(l.Id, SelectedWalls.ToList()); SelectedWalls.Clear(); Status = T("deleted"); Refresh();
    }

    // clipboard (walls with their openings); pasting on the source level shifts each copy by 500 mm
    private List<Wall> _clip = []; private Guid _clipLevel; private int _pasteCount;
    public bool CanPaste => _clip.Count > 0;
    [RelayCommand] private void Copy()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) return;
        _clip = l.Walls.Where(w => SelectedWalls.Contains(w.Id)).Select(CloneWall).ToList(); _clipLevel = l.Id; _pasteCount = 0; Status = T("clip.copied");
    }
    [RelayCommand] private void Cut() { if (SelectedWalls.Count == 0) return; Copy(); DeleteSelection(); Status = T("clip.cut"); }
    [RelayCommand] private void Paste()
    {
        if (CurrentLevel is not { } l) return;
        if (_clip.Count == 0) { Status = T("clip.empty"); return; }
        _pasteCount++; var d = l.Id == _clipLevel ? 500 * _pasteCount : 0;
        var ids = Actions.PasteWalls(l.Id, _clip, d, d); Refresh(); SelectWalls(ids, false); Status = T("clip.pasted");
    }
    [RelayCommand] private void Duplicate() { if (SelectedWalls.Count == 0) return; Copy(); Paste(); }
    private static Wall CloneWall(Wall w) => new()
    {
        Footprint = w.Footprint.ToList(), Holes = w.Holes.Select(h => h.ToList()).ToList(), HeightMm = w.HeightMm, Label = w.Label, Stability = w.Stability,
        Openings = w.Openings.Select(o => new Opening { Kind = o.Kind, PositionMm = o.PositionMm, WidthMm = o.WidthMm, HeightMm = o.HeightMm, SillMm = o.SillMm, SwingLeft = o.SwingLeft, SwingOut = o.SwingOut, Label = o.Label, Stability = o.Stability }).ToList(),
    };

    /// <summary>Raised by the zoom commands; the window forwards it to the plan view ("in", "out", "fit").</summary>
    public event Action<string>? PlanZoomRequested;
    [RelayCommand] private void ZoomPlan(string how) => PlanZoomRequested?.Invoke(how);

    [RelayCommand] private void SelectAll() { if (CurrentLevel is { } l) SelectWalls(l.Walls.Select(w => w.Id), false); }
    [RelayCommand] private void SetTool(string t) => Tool = Enum.Parse<ToolKind>(t);
    /// <summary>Set by the window: runs the animated plan ↔ 3D transition and calls <c>done</c> when finished. Returns false when it does not handle the change.</summary>
    public Func<ViewMode, ViewMode, Action, bool>? TransitionHandler { get; set; }
    public void RequestMode(ViewMode to)
    {
        var from = Mode; if (to == from) return;
        if (TransitionHandler?.Invoke(from, to, () => Mode = to) == true) return;
        Mode = to;
    }
    [RelayCommand] private void SetMode(string m) => RequestMode(Enum.Parse<ViewMode>(m));
    [RelayCommand] private void Undo() { History.Undo(Building); Refresh(); }
    [RelayCommand] private void Redo() { History.Redo(Building); Refresh(); }

    // ---- Background picture: stretch, opacity, difference ----
    private readonly Dictionary<string, (int W, int H)> _imageSizes = new();
    public (int W, int H)? BackgroundSize()
    {
        if (CurrentLevel?.BackgroundImage is not { } n || !Images.TryGetValue(n, out var data)) return null;
        if (!_imageSizes.TryGetValue(n, out var sz)) _imageSizes[n] = sz = PlanImport.Size(data);
        return sz;
    }
    [ObservableProperty] private double _bgOpacity = 0.5;
    [ObservableProperty] private bool _bgDifference;
    [ObservableProperty] private bool _bgVisible = true;
    partial void OnBgOpacityChanged(double value) { if (CurrentLevel is { } l && Math.Abs(l.BackgroundOpacity - value) > 1e-6) { Actions.SetBackgroundDisplay(l.Id, value, BgVisible); Refresh(); } }
    partial void OnBgDifferenceChanged(bool value) => Changed?.Invoke();
    partial void OnBgVisibleChanged(bool value) { if (CurrentLevel is { } l && l.BackgroundVisible != value) { Actions.SetBackgroundDisplay(l.Id, BgOpacity, value); Refresh(); } }
    [ObservableProperty] private double _imgScaleX; [ObservableProperty] private double _imgScaleY; [ObservableProperty] private double _imgRotation; [ObservableProperty] private double _imgOffsetX; [ObservableProperty] private double _imgOffsetY;

    /// <summary>Commits a stretch gesture or nudge (one history step).</summary>
    public void SetImageTransform(Affine t) { if (CurrentLevel is { } l) { Actions.SetImageTransform(l.Id, t.ToArray(), AlignMoveWalls); Refresh(); } }
    public void NudgeImage(int dx, int dy) { if (CurrentLevel?.ImageTransform is { } m) SetImageTransform(ImageStretch.Nudge(ImageStretch.FromArray(m), dx, dy)); }
    [RelayCommand] private void ApplyImageFields() { if (CurrentLevel?.ImageTransform != null) SetImageTransform(ImageStretch.Compose(ImgScaleX, ImgScaleY, ImgRotation, ImgOffsetX, ImgOffsetY)); }

    // ---- Theme and guided tour ----
    public void ApplyTheme(string theme)
    {
        Settings.Theme = theme; if (SettingsPath != null) try { SettingsStore.Save(Settings, SettingsPath); } catch (IOException) { }
        if (Avalonia.Application.Current is { } app) app.RequestedThemeVariant = theme switch { "light" => Avalonia.Styling.ThemeVariant.Light, "dark" => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default };
        Changed?.Invoke();
    }
    [RelayCommand] private void SetTheme(string theme) => ApplyTheme(theme);

    // ---- Language: one language at a time, switchable at runtime ----
    public void ApplyLanguage(string language)
    {
        Settings.Language = language; Translations.Language = language;       // raises LanguageChanged → OnLanguageChanged
        if (SettingsPath != null) try { SettingsStore.Save(Settings, SettingsPath); } catch (IOException) { }
    }
    [RelayCommand] private void SetLanguage(string language) => ApplyLanguage(language);

    private void OnLanguageChanged()
    {
        Hint = Tool switch { ToolKind.DrawWall => T("hint.draw.wall"), ToolKind.DrawPipe => T("hint.draw.pipe"), ToolKind.Riser => T("hint.riser"), ToolKind.Place => T("hint.place"), ToolKind.Align => T("hint.align"), ToolKind.Door => T("hint.door"), ToolKind.Window => T("hint.window"), ToolKind.Space => T("hint.space"), ToolKind.Measure => T("hint.measure"), ToolKind.NumberCellars => T("hint.number"), ToolKind.Straighten => T("hint.straighten"), ToolKind.Stretch => T("hint.stretch"), ToolKind.CutWall => T("hint.cutwall"), ToolKind.MergeRegion => T("hint.mergeregion"), _ => T("hint.select") };
        Status = T("ready"); RebuildPalette(); RebuildSteps(); OnPropertyChanged(nameof(MapLayers)); OnPropertyChanged(nameof(RiserTemplates)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(TourTitle)); OnPropertyChanged(nameof(TourBody)); OnPropertyChanged(nameof(NetworkTypes)); OnPropertyChanged(nameof(SpaceKinds)); OnPropertyChanged(nameof(WallDisplayLabel));
        if (SelectedEquipment != null) SelectEquipment(SelectedEquipmentId);
        Refresh(); LanguageRefreshed?.Invoke();
    }
    public event Action? LanguageRefreshed;

    public static readonly (string Title, string Body, string ToolOrAction)[] TourSteps =
    [
        ("tour.t1", "tour.b1", "import"), ("tour.t2", "tour.b2", "calibrate"), ("tour.t3", "tour.b3", "detect"), ("tour.t4", "tour.b4", "Riser"), ("tour.t5", "tour.b5", "DrawPipe"),
    ];
    [ObservableProperty] private int _tourStep = -1;
    public bool TourActive => TourStep >= 0;
    public string TourTitle => TourActive ? T(TourSteps[TourStep].Title) : "";
    public string TourBody => TourActive ? T(TourSteps[TourStep].Body) : "";
    public string TourProgress => TourActive ? $"{TourStep + 1} / {TourSteps.Length}" : "";
    partial void OnTourStepChanged(int value) { OnPropertyChanged(nameof(TourActive)); OnPropertyChanged(nameof(TourTitle)); OnPropertyChanged(nameof(TourBody)); OnPropertyChanged(nameof(TourProgress)); }
    [RelayCommand] private void StartTour() { if (ActiveIndex < 0) OpenSample(); TourStep = 0; }
    [RelayCommand] private void TourNext()
    {
        if (!TourActive) return;
        var action = TourSteps[TourStep].ToolOrAction; if (action is "Riser" or "DrawPipe") SetTool(action);
        if (TourStep + 1 >= TourSteps.Length) EndTour(); else TourStep++;
    }
    [RelayCommand] private void TourSkip() => EndTour();
    private void EndTour() { TourStep = -1; Settings.TourDone = true; if (SettingsPath != null) try { SettingsStore.Save(Settings, SettingsPath); } catch (IOException) { } }

    // ---- Display options ----
    [ObservableProperty] private bool _ghostBelow = true;
    [ObservableProperty] private bool _ghostAbove;
    [ObservableProperty] private bool _commonOnly;
    /// <summary>Levels switched off with the eye in the level list: not drawn as a transparent ghost in the plan, not built in 3D. View state only, no history step.</summary>
    public HashSet<Guid> HiddenLevels { get; } = new();
    [RelayCommand] private void ToggleLevelVisibility(Guid id)
    {
        if (!HiddenLevels.Remove(id)) HiddenLevels.Add(id);
        Refresh(); Changed?.Invoke();
    }
    [RelayCommand] private void ToggleGhostBelow() { GhostBelow = !GhostBelow; Changed?.Invoke(); }
    [RelayCommand] private void ToggleGhostAbove() { GhostAbove = !GhostAbove; Changed?.Invoke(); }
    [RelayCommand] private void ToggleCommonOnly() { CommonOnly = !CommonOnly; Status = CommonOnly ? T("commononly") : T("showall"); Changed?.Invoke(); }

    [ObservableProperty] private int _straightenTolerance = 30;
    [ObservableProperty] private int _straightenAngle = 5;
    /// <summary>Live preview for the Straighten tool: outlines + "47 → 4 segments".</summary>
    public (IReadOnlyList<(Guid WallId, List<PointMm> Outline)> Shapes, string Info)? StraightenPreview()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) return null;
        var shapes = BuildingActions.PreviewStraighten(l, SelectedWalls.ToList(), StraightenTolerance, StraightenAngle, StraightenWithCadastre ? ReferenceAngles() : null);
        var before = l.Walls.Where(w => SelectedWalls.Contains(w.Id)).Sum(w => w.Footprint.Count); var after = shapes.Sum(x => x.Outline.Count);
        return (shapes, $"{before} → {after} segments");
    }
    [RelayCommand] private void MakeRectangular()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) { Status = T("status.need.selection"); return; }
        Actions.MakeWallsRectangular(l.Id, SelectedWalls.ToList()); Status = T("rectified"); Refresh();
    }
    /// <summary>Straightens the selected walls (tolerance 30 mm, 5°) as one history step.</summary>
    [RelayCommand] private void StraightenSelection()
    {
        if (CurrentLevel is not { } l || SelectedWalls.Count == 0) { Status = T("straighten.select"); return; }
        var before = l.Walls.Where(w => SelectedWalls.Contains(w.Id)).Sum(w => w.Footprint.Count);
        Actions.StraightenWalls(l.Id, SelectedWalls.ToList(), StraightenTolerance, StraightenAngle, StraightenWithCadastre ? ReferenceAngles() : null); var after = CurrentLevel!.Walls.Where(w => SelectedWalls.Contains(w.Id)).Sum(w => w.Footprint.Count);
        Status = T("straightened", before, after); Refresh();
    }

    // ---- Documents ----
    [RelayCommand] private void NewDocument()
    {
        var d = new Doc(); d.Building.Name = ""; AddDoc(d);
        Actions.AddLevel(Token("level.ground"), 0, 2700, LevelKind.Ground); CurrentLevelId = Building.Levels[0].Id; History.MarkClean(); Refresh(); Status = T("new.building");
    }
    /// <summary>Opens an autosaved building after a crash; it stays dirty so closing asks to save it.</summary>
    public void AdoptRecovered(Building b) { var d = new Doc { Building = b }; AddDoc(d); Actions.AddLevel(Token("recovered.level"), 0); Status = T("recovered"); }
    [RelayCommand] private void OpenSample() { var d = new Doc { Building = SampleBuilding.Create().Building }; d.Building.Name = Token("sample.name"); AddDoc(d); Status = T("sample.opened"); }
    [RelayCommand] private void SwitchTab(int index) { if (index >= 0 && index < Docs.Count) Activate(index); }
    [RelayCommand] private void JumpTo(int index) { History.JumpTo(Building, index); SelectedWalls.Clear(); Refresh(); }

    [RelayCommand] private async Task OpenAsync()
    {
        try { if (await Dialogs.PickOpenAsync("Immeuble", "*.immeuble") is { } p) OpenFile(p); }
        catch (Exception ex) { await Fail(T("fail.open"), ex); }
    }
    [RelayCommand] private async Task OpenRecentAsync(string path)
    {
        try { OpenFile(path); } catch (Exception ex) { Settings.RecentFiles.Remove(path); RecentFiles.Remove(path); await Fail(T("fail.open"), ex); }
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
        catch (Exception ex) { await Fail(T("fail.save"), ex); return false; }
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
            if (await Dialogs.RunImportWizardAsync(File.ReadAllBytes(path), WizardReferences()) is { } wiz) { var n = ApplyWizard(wiz); OnLog?.Invoke($"Import plan {Path.GetFileName(path)}: {n} shapes in {sw.ElapsedMilliseconds} ms"); }
        }
        catch (Exception ex) { await Fail(T("fail.import"), ex); }
    }
    /// <summary>"ask" opens the export dialog (format, levels, networks, units); any other value exports that format directly with everything included.</summary>
    /// <summary>The other levels that already have a calibrated picture: they can serve as scale reference for a new plan.</summary>
    private List<WizardReference> WizardReferences() => Building.Levels
        .Where(l => l.Id != CurrentLevelId && l.BackgroundImage is { } n && l.ImageTransform is { Length: 6 } && Images.ContainsKey(n))
        .Select(l => new WizardReference(l.DisplayName, Images[l.BackgroundImage!], l.ImageTransform!)).ToList();

    [RelayCommand] private async Task ImportIfcAsync()
    {
        try
        {
            if (await Dialogs.PickOpenAsync("IFC", "*.ifc") is not { } path) return;
            if (ActiveIndex < 0) AddDoc(new Doc());                                   // an empty building: the IFC brings its own storeys
            var r = IfcImport.ImportInto(Actions, File.ReadAllText(path));
            CurrentLevelId = Building.Levels.FirstOrDefault()?.Id ?? CurrentLevelId; Status = T("ifc.done", r.Building.Levels.Count, r.Walls) + (r.Warnings.Count > 0 ? T("ifc.warnings", r.Warnings.Count) : ""); OnLog?.Invoke($"Imported IFC {Path.GetFileName(path)}: {r.Building.Levels.Count} levels, {r.Walls} walls, {r.Warnings.Count} warnings");
            Refresh(); ViewFitRequested?.Invoke();
        }
        catch (Exception ex) { await Fail(T("fail.importifc"), ex); }
    }

    /// <summary>Reference model (OBJ / GLB) on the current level: a locked mesh shown as a section under the plan, to trace over.</summary>
    [RelayCommand] private async Task ImportReferenceAsync()
    {
        try
        {
            if (ActiveIndex < 0 || CurrentLevel is not { } l) return;
            if (await Dialogs.PickOpenAsync(T("ref.pick"), "*.obj;*.glb") is not { } path) return;
            Actions.AddReference(l.Id, ReferenceImport.FromFile(path)); Status = T("ref.added"); OnLog?.Invoke($"Reference model {Path.GetFileName(path)}"); Refresh(); ViewFitRequested?.Invoke();
        }
        catch (Exception ex) { await Fail(T("fail.importref"), ex); }
    }

    [RelayCommand] private async Task ExportAsync(string format)
    {
        if (ActiveIndex < 0) return;
        try
        {
            var options = new ExportOptions();
            if (format == "ask")
            {
                if (await Dialogs.ShowExportDialogAsync(Building) is not { } req) return;
                (format, options) = (req.Format, req.Options);
            }
            var ext = ExportFormats.Extension(format);
            if (await Dialogs.PickSaveAsync("Export", ext, Active.Title + ext.TrimStart('*')) is not { } path) return;
            var data = options.Apply(Building);
            switch (format)
            {
                case "csv": File.WriteAllText(path, BomFull.ToCsv(data, true)); break;
                case "bom_pdf": File.WriteAllBytes(path, BomFull.ToPdf(data)); break;
                case "glb": File.WriteAllBytes(path, GltfExporter.ExportGlb(data)); break;
                case "svg": File.WriteAllText(path, SvgExporter.Export(data.Levels.FirstOrDefault(l => l.Id == CurrentLevelId) ?? data.Levels.FirstOrDefault() ?? new Level())); break;
                default: File.WriteAllText(path, Exporters.All.First(x => x.Id == format).Export(Building, options)); break;
            }
            Status = T("exported", Path.GetFileName(path)); OnLog?.Invoke($"Exported {format} to {path}");
        }
        catch (Exception ex) { await Fail(T("fail.export"), ex); }
    }
    public event Action<string>? OnLog;
    public event Action<string, Exception>? OnError;
    private async Task Fail(string what, Exception ex) { OnError?.Invoke(what, ex); Status = T("error.status", what, ex.Message); await Dialogs.ShowErrorAsync("ImmoCad", T("error.dialog", what, ex.Message)); }

    [RelayCommand] private void AddLevel()
    {
        var top = Building.Levels.Count == 0 ? 0 : Building.Levels.Max(l => l.AltitudeMm + l.HeightMm);
        var lv = Actions.AddLevel(Building.Levels.Count == 0 ? Token("level.ground") : Token("level.n", Building.Levels.Count), top);
        CurrentLevelId = lv.Id; Status = T("levels.count", Building.Levels.Count); Refresh();
    }

    /// <summary>Chain of centre points → one rectangle per segment, committed as one history step.</summary>
    public void CommitWalls(IReadOnlyList<PointMm> pts)
    {
        if (CurrentLevel is not { } lv) return;
        var rects = new List<IEnumerable<PointMm>>();
        for (var i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y; var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) continue;
            rects.Add(ImmoCad.Core.Geometry.WallGeometry.Rectangle(pts[i - 1], pts[i], WallThicknessMm, WallAlign));
        }
        if (rects.Count == 0) return;
        Actions.ImportWalls(lv.Id, rects); Status = T("walls.added", rects.Count); Refresh();
    }

    public void SaveTo(string path)
    {
        BuildingFile.Save(Building, path, Images); History.MarkClean(); Active.FilePath = path; Status = T("saved", Path.GetFileName(path)); Remember(path); Refresh();
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
        AddDoc(new Doc { Building = b, Images = imgs, FilePath = path }); Status = T("opened", Path.GetFileName(path)); Remember(path);
    }
    public void LoadFrom(string path) => OpenFile(path);
    /// <summary>Applies a finished wizard to the current level: walls + background, one history step each.</summary>
    public int ApplyWizard(ImportWizardViewModel w)
    {
        if (CurrentLevel is not { } l) return 0;
        var n = w.Apply(Actions, l, Images); Status = T("import.done", n); Refresh(); ViewFitRequested?.Invoke(); return n;
    }

    public int ImportPlan(string imagePath, double pxPerMm)
    {
        if (CurrentLevel is not { } l) return 0;
        var n = PlanImport.Import(Actions, l.Id, File.ReadAllBytes(imagePath), new Calibration(pxPerMm, pxPerMm), new PlanWallDetectorAdapter());
        Status = T("import.done", n); Refresh(); return n;
    }
    public void Export(string format, string path)
    {
        File.WriteAllText(path, ImmoCad.Core.Export.Exporters.All.First(x => x.Id == format).Export(Building)); Status = T("exported", Path.GetFileName(path));
    }
    public string BomCsv() => Bom.ToCsv(Building);
}
