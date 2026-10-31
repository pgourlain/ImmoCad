using CommunityToolkit.Mvvm.Input;
using ImmoCad.Core.Model;
using ImmoCad.Core.Walk;
using ImmoCad.Scene;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>
/// Walk mode: first-person inspection of a level at eye height. View state only, so nothing here touches the history or the dirty flag.
/// The movement logic is <see cref="WalkState"/> (Core); this part connects it to the levels, the status line and the other views.
/// </summary>
public partial class MainViewModel
{
    public WalkState? Walk { get; private set; }
    public bool IsWalk => Mode == ViewMode.Walk;
    public bool NotWalk => Mode != ViewMode.Walk;

    private bool _walkAvailable;
    /// <summary>Set by the window: walking is drawn with OpenGL only, there is no software viewport for it.</summary>
    public bool WalkAvailable
    {
        get => _walkAvailable;
        set { if (_walkAvailable == value) return; _walkAvailable = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanWalk)); OnPropertyChanged(nameof(WalkUnavailable)); OnPropertyChanged(nameof(WalkTip)); WalkCommand.NotifyCanExecuteChanged(); }
    }
    public bool CanWalk => HasDocument && WalkAvailable && Building.Levels.Any(l => l.Walls.Count > 0);
    /// <summary>Why the Walk entries are disabled (shown as their tooltip), empty when they are available.</summary>
    public string WalkUnavailable => !WalkAvailable ? T("walk.nogl") : HasDocument && !Building.Levels.Any(l => l.Walls.Count > 0) ? T("walk.nowalls.building") : "";

    /// <summary>Tooltip of the Walk button: its purpose, or why it is disabled.</summary>
    public string WalkTip => WalkUnavailable is { Length: > 0 } why ? why : T("tip.walk");

    /// <summary>The plan's look-at point, set by the window: where the walk starts when nothing better is known.</summary>
    public Func<PointMm>? PlanLookAt { get; set; }
    /// <summary>Last walk position, shown as a pin on the plan after leaving.</summary>
    public PointMm? WalkPin { get; private set; }
    /// <summary>Raised when the walk state changed in a way the overlay should show (level, notices).</summary>
    public event Action? WalkChanged;

    private ViewMode _walkBack = ViewMode.Plan;
    private readonly Dictionary<Guid, WalkPoint> _lastWalk = new();
    private bool _walkLevelSync;

    [RelayCommand(CanExecute = nameof(CanWalk))] private void Walking() => BeginWalk(null);
    /// <summary>Same command with the name the menus and the palette use.</summary>
    public IRelayCommand WalkCommand => WalkingCommand;

    [RelayCommand] private void LeaveWalk() => EndWalkRequest(null);
    [RelayCommand] private void WalkFromSpace(Guid id) { var s = SpaceItems.FirstOrDefault(x => x.Id == id); if (s != null) BeginWalk(s.Center, s.LevelId); }
    [RelayCommand]
    private void ToggleWalkGhost()
    {
        if (Walk is not { } w) return;
        w.SetGhost(!w.Ghost); Settings.WalkGhost = w.Ghost; Status = T(w.Ghost ? "walk.ghost.on" : "walk.ghost.off"); WalkChanged?.Invoke();
    }

    /// <summary>Starts walking: from a point, otherwise the last position on this level, otherwise the middle of the largest corridor, otherwise the plan's centre.</summary>
    public void BeginWalk(PointMm? at, Guid? levelId = null)
    {
        if (!CanWalk) { Status = WalkUnavailable; return; }
        var level = (levelId is { } id ? Building.Levels.FirstOrDefault(l => l.Id == id) : CurrentLevel);
        if (level == null || level.Walls.Count == 0) level = Building.Levels.FirstOrDefault(l => l.Walls.Count > 0);
        if (level == null) return;
        var area = new WalkArea(Building, level);
        WalkPoint start = at is { } a ? new(a.X, a.Y)
            : _lastWalk.TryGetValue(level.Id, out var last) ? last
            : CorridorCenter(level) ?? (PlanLookAt?.Invoke() is { } p ? new(p.X, p.Y) : new(0, 0));
        var spot = area.NearestWalkable(start, WalkState.DefaultEyeMm);
        Walk = new WalkState(Building, level, spot, area.LongestFreeYaw(spot, WalkState.DefaultEyeMm));
        Walk.Notice += OnWalkNotice;
        if (Settings.WalkGhost) Walk.SetGhost(true);
        _walkBack = Mode is ViewMode.Solid or ViewMode.Wireframe ? Mode : ViewMode.Plan;
        _walkLevelSync = true; CurrentLevelId = level.Id; _walkLevelSync = false;
        WalkPin = null; Mode = ViewMode.Walk;
        Hint = T("walk.hint"); Status = T("walk.entered", level.DisplayName);
        WalkChanged?.Invoke();
    }

    private WalkPoint? CorridorCenter(Level l)
    {
        var best = l.Spaces.Where(s => s.Kind == SpaceKind.Corridor && s.Outline.Count >= 3).OrderByDescending(s => ImmoCad.Core.Geometry.SpaceFinder.AreaMm2(s.Outline)).FirstOrDefault();
        return best == null ? null : new((best.Outline.Min(p => p.X) + best.Outline.Max(p => p.X)) / 2.0, (best.Outline.Min(p => p.Y) + best.Outline.Max(p => p.Y)) / 2.0);
    }

    /// <summary>Leaves the walk (Esc): back to the view it came from, with a message when something forced it.</summary>
    public void EndWalkRequest(string? message)
    {
        if (Walk == null) return;
        Mode = _walkBack; if (message != null) Status = message;
    }

    /// <summary>Bookkeeping once the mode has changed away from Walk, whatever way it happened (Esc, a view button, a level of the Site tab…).</summary>
    private void EndWalk()
    {
        if (Walk is not { } w) return;
        w.Notice -= OnWalkNotice; _lastWalk[w.LevelId] = w.Position; Settings.WalkGhost = w.Ghost;
        var pin = new PointMm((int)Math.Round(w.X), (int)Math.Round(w.Y)); WalkPin = pin; Walk = null;
        OnToolChanged(Tool);                                  // restores the hint of the active tool
        FocusRequested?.Invoke(pin);                          // the plan comes back centred on the last position
        WalkChanged?.Invoke();
    }

    private void OnWalkNotice(WalkNotice n)
    {
        switch (n.Kind)
        {
            case WalkNoticeKind.Shifted: Status = T("walk.shifted", n.Value); break;
            case WalkNoticeKind.LevelHasNoWalls: Status = T("walk.nowalls.level"); break;
            case WalkNoticeKind.LevelChanged:
                if (Walk is { } w && CurrentLevelId != w.LevelId) { _walkLevelSync = true; CurrentLevelId = w.LevelId; _walkLevelSync = false; Refresh(); }
                break;
        }
        WalkChanged?.Invoke();
    }

    /// <summary>The level bar (or Page Up/Down) changed the level while walking: the elevator.</summary>
    partial void OnCurrentLevelIdChanged(Guid value)
    {
        if (Walk is not { } w || _walkLevelSync) return;
        var l = Building.Levels.FirstOrDefault(x => x.Id == value); if (l == null || l.Id == w.LevelId) return;
        if (!w.ChangeLevel(l)) { _walkLevelSync = true; CurrentLevelId = w.LevelId; _walkLevelSync = false; }
        WalkChanged?.Invoke();
    }

    /// <summary>Called at the start of <see cref="Refresh"/>: keeps the Walk entries enabled or not, and the walk state valid after an undo or when the document changed.</summary>
    private void WalkSync()
    {
        WalkingCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanWalk)); OnPropertyChanged(nameof(WalkUnavailable)); OnPropertyChanged(nameof(WalkTip));
        if (Walk is not { } w) return;
        if (!ReferenceEquals(w.Building, Building)) { Mode = _walkBack; return; }                       // another document: the walk ends
        if (!w.Rebind()) EndWalkRequest(T("walk.levelgone"));
    }

    /// <summary>Level and room where the user stands, the eye height when it is not the usual one, ghost mode.</summary>
    public string WalkHud()
    {
        if (Walk is not { } w) return "";
        var room = SpaceAt(new PointMm((int)Math.Round(w.X), (int)Math.Round(w.Y)))?.DisplayName;
        var text = room == null ? w.Level.DisplayName : T("walk.where", w.Level.DisplayName, room);
        if (Math.Abs(w.EyeAboveFloor - WalkState.DefaultEyeMm) > 20) text += "  ·  " + T("walk.eye", w.EyeAboveFloor / 1000.0);
        return text;
    }

    /// <summary>One line about the object under the cursor: network, diameter and material for a pipe, name for equipment.</summary>
    public string DescribeScene(Guid id, SceneKind kind)
    {
        switch (kind)
        {
            case SceneKind.Pipe:
                foreach (var n in Building.Networks)
                    if (n.Segments.FirstOrDefault(s => s.Id == id) is { } s)
                        return T("walk.pipe", string.IsNullOrWhiteSpace(n.Name) ? Enum_(n.Type) : n.Name, s.DiameterMm, s.Material);
                return "";
            case SceneKind.Equipment:
                var e = Building.Equipment.FirstOrDefault(x => x.Id == id); if (e == null) return "";
                var cat = ImmoCad.Core.Catalog.EquipmentCatalog.Find(e.CatalogId); return e.Label is { Length: > 0 } lab ? $"{cat?.Name ?? e.CatalogId} — {lab}" : cat?.Name ?? e.CatalogId;
            case SceneKind.Wall: return T("walk.wall");
            case SceneKind.Component: case SceneKind.Fitting:
                return T("walk.fitting");
            default: return "";
        }
    }
}
