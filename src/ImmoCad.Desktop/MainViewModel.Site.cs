using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmoCad.Core.Geometry;
using ImmoCad.Core.History;
using ImmoCad.Core.Model;
using ImmoCad.Core.Site;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>Everything the site level needs, behind the Core interfaces (HTTP + disk cache in the app, fakes in tests).</summary>
public sealed record SiteData(IGeocoder Geocoder, ICadastreService Cadastre, IMapTileSource Tiles);

public sealed record FootprintItem(int Index, string Label, double AreaM2) { public string Text => ImmoCad.Core.Translations.T("site.item.area", Label, AreaM2.ToString("0")); }

public partial class MainViewModel
{
    public SiteData? Geo { get; set; }
    public static string Attribution => T("attribution");
    public string[] MapLayers => [T("map.ortho"), T("map.plan"), T("map.cadastre")];
    [ObservableProperty] private string _siteAddress = "";
    [ObservableProperty] private int _mapLayer;                                // 0 orthophoto, 1 plan IGN, 2 cadastre
    [ObservableProperty] private bool _showFootprint = true;
    /// <summary>true = true north up (the plan is turned by the site rotation); false = building aligned.</summary>
    [ObservableProperty] private bool _northUp;
    partial void OnNorthUpChanged(bool value) => Changed?.Invoke();
    [RelayCommand] private void ToggleNorthUp() { NorthUp = !NorthUp; Status = T(NorthUp ? "northup.on" : "northup.off"); }
    [ObservableProperty] private string _siteMessage = "";
    public (double E, double N) MapCenter { get; set; } = (650000, 6860000);
    public double MapScale { get; set; } = 3;                                   // px per metre
    public List<List<(double E, double N)>> Footprints { get; } = new();        // cadastral buildings around the centre, Lambert 93
    private readonly List<int> _selected = new();                                // the parcels picked on the map, in click order; the first one carries the building's axes
    public int SelectedFootprint => _selected.Count > 0 ? _selected[0] : -1;
    public int SelectedCount => _selected.Count;
    public bool IsSelected(int index) => _selected.Contains(index);
    public ObservableCollection<FootprintItem> FootprintItems { get; } = new();
    public ObservableCollection<CornerItem> AngleIssues { get; } = new();
    public event Action? SiteChanged;

    // ---- First visit to the map: show the building's own parcels when the plan is already georeferenced ----
    private Building? _siteFocused;
    /// <summary>Set by <see cref="FocusSite"/>, consumed by the map view once it knows its size: centre of the parcels and their extent in metres.</summary>
    public double? PendingSiteFit { get; set; }
    private void FocusSite()
    {
        if (Building.Site is not { } s || ReferenceEquals(_siteFocused, Building)) return;
        var rings = new[] { s.Footprint }.Concat(s.ExtraFootprints).Where(r => r.Count >= 3).ToList();
        var pts = rings.SelectMany(r => r).Select(p => (E: p[0], N: p[1])).ToList();
        if (pts.Count == 0) { var a = new SiteAnchor(s.EastM, s.NorthM, s.RotationDeg, s.AltitudeM); pts = BuildingCorners().Select(c => a.ToLambert(c.X, c.Y)).ToList(); }   // no cadastral outline: the plan itself
        if (pts.Count == 0) { pts.Add((s.EastM, s.NorthM)); }
        _siteFocused = Building;
        double e0 = pts.Min(p => p.E), e1 = pts.Max(p => p.E), n0 = pts.Min(p => p.N), n1 = pts.Max(p => p.N);
        MapCenter = ((e0 + e1) / 2, (n0 + n1) / 2); PendingSiteFit = Math.Max(e1 - e0, n1 - n0); SiteChanged?.Invoke();
        if (Geo != null && Footprints.Count == 0) _ = LoadBuildingParcelsAsync();
    }
    private async Task LoadBuildingParcelsAsync()
    {
        try
        {
            var (lat, lon) = Lambert93.ToWgs84(MapCenter.E, MapCenter.N); await LoadFootprintsAsync(lat, lon);
            for (var i = 0; i < Footprints.Count; i++) if (InBuilding(i)) _selected.Add(i);          // the building's parcels come back selected
            SiteMessage = _selected.Count > 0 ? T("site.selected", _selected.Count) : ""; SiteChanged?.Invoke();
        }
        catch (Exception ex) { OnError?.Invoke("load parcels", ex); }
    }

    public sealed record CornerItem(int Index, string Text, PointMm At);

    [RelayCommand] private async Task SearchAddressAsync()
    {
        if (Geo == null) { SiteMessage = T("site.nomap"); return; }
        try
        {
            var at = await Geo.Geocoder.GeocodeAsync(SiteAddress);
            if (at is not { } p) { SiteMessage = T("site.addr.notfound"); return; }
            var (e, n) = Lambert93.FromWgs84(p.Lat, p.Lon); MapCenter = (e, n); SiteMessage = $"{SiteAddress} → {e:0}, {n:0}";
            await LoadFootprintsAsync(p.Lat, p.Lon); SiteChanged?.Invoke();
        }
        catch (Exception ex) { OnError?.Invoke("search address", ex); SiteMessage = ex.Message; }
    }

    private async Task LoadFootprintsAsync(double lat, double lon)
    {
        Footprints.Clear(); FootprintItems.Clear(); _selected.Clear();
        var rings = await Geo!.Cadastre.BuildingFootprintsAsync(lat, lon, 120);
        foreach (var r in rings) Footprints.Add(r.ToList());
        for (var i = 0; i < Footprints.Count; i++) FootprintItems.Add(new FootprintItem(i, T("site.building.n", i + 1), Area(Footprints[i])));
        SiteMessage += T("site.found", Footprints.Count);
    }

    private static double Area(List<(double E, double N)> r) { double a = 0; for (var i = 0; i < r.Count; i++) { var (p, q) = (r[i], r[(i + 1) % r.Count]); a += p.E * q.N - q.E * p.N; } return Math.Abs(a) / 2; }

    /// <summary>Selects a parcel; with <paramref name="toggle"/> it joins or leaves the selection, otherwise it replaces it (-1 clears).</summary>
    public void SelectFootprint(int index, bool toggle = false)
    {
        if (index < 0 || index >= Footprints.Count) { if (!toggle) _selected.Clear(); }
        else if (toggle && _selected.Contains(index)) _selected.Remove(index);
        else { if (!toggle) _selected.Clear(); _selected.Add(index); }
        if (_selected.Count > 0 && !Placing) SiteMessage = T("site.selected", _selected.Count);
        SiteChanged?.Invoke();
    }
    [RelayCommand] private void PickFootprint(int index) => SelectFootprint(index, true);

    /// <summary>Picks the cadastral building under a click (Lambert metres), if any.</summary>
    public void PickFootprintAt(double e, double n)
    {
        for (var i = Footprints.Count - 1; i >= 0; i--) if (InPoly(Footprints[i], e, n)) { SelectFootprint(i, true); return; }
        SelectFootprint(-1);
    }
    private static bool InPoly(List<(double E, double N)> r, double x, double y) { var inside = false; for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) if ((r[i].N > y) != (r[j].N > y) && x < (r[j].E - r[i].E) * (y - r[i].N) / (r[j].N - r[i].N) + r[i].E) inside = !inside; return inside; }

    /// <summary>Start a new building from the selected footprint: model origin on its first corner, X axis along its first edge, outer walls pre-drawn (300 mm, inside the outline). One history step for the site, one for the walls.</summary>
    [RelayCommand] private void CreateFromFootprint()
    {
        if (SelectedFootprint < 0) { SiteMessage = T("site.pick.first"); return; }
        if (ActiveIndex < 0) NewDocument();
        var ring = Footprints[SelectedFootprint]; var rot = Math.Atan2(ring[1].N - ring[0].N, ring[1].E - ring[0].E);
        var site = new SiteInfo { EastM = ring[0].E, NorthM = ring[0].N, RotationDeg = rot * 180 / Math.PI, Address = SiteAddress.Length > 0 ? SiteAddress : null, Footprint = ring.Select(p => new[] { p.E, p.N }).ToList(), Neighbours = new(), ExtraFootprints = new() };
        ApplySelection(site);
        Actions.SetSite(site);
        if (CurrentLevel is not { } l) return;
        var walls = new List<IEnumerable<PointMm>>();
        foreach (var model in new[] { FootprintFit.ToModel(site, site.Footprint) }.Concat(site.ExtraFootprints.Select(r => FootprintFit.ToModel(site, r))))      // every selected parcel gets its outer walls
        {
            double cx = model.Average(p => (double)p.X), cy = model.Average(p => (double)p.Y);
            for (var i = 0; i < model.Count; i++)
            {
                var a = model[i]; var b = model[(i + 1) % model.Count]; double dx = b.X - a.X, dy = b.Y - a.Y; var len = Math.Sqrt(dx * dx + dy * dy); if (len < 400) continue;
                double nx = -dy / len, ny = dx / len; if ((cx - a.X) * nx + (cy - a.Y) * ny < 0) { nx = -nx; ny = -ny; }          // normal pointing inside
                const int t = 300; walls.Add([a, b, new((int)(b.X + nx * t), (int)(b.Y + ny * t)), new((int)(a.X + nx * t), (int)(a.Y + ny * t))]);
            }
        }
        Actions.ImportWalls(l.Id, walls); Mode = ViewMode.Plan; Status = T("site.created", walls.Count); Refresh(); ViewFitRequested?.Invoke();
    }

    // ---- Place an existing building: two corners of the plan, then the matching two corners of the footprint ----
    public List<PointMm> PlacementPlanPoints { get; } = new(); public List<(double E, double N)> PlacementMapPoints { get; } = new();
    private bool _placing;
    public bool Placing { get => _placing; private set { _placing = value; OnPropertyChanged(); } }
    /// <summary>Where the building is drawn while placing it: fixed on the map (pan and zoom do not move it), dragged and turned by the user.</summary>
    public SiteAnchor PlacementAnchor { get; private set; } = new(0, 0);
    [RelayCommand] private void BeginPlacement()
    {
        Placing = true; PlacementPlanPoints.Clear(); PlacementMapPoints.Clear();
        if (Building.Site is { } st) PlacementAnchor = new(st.EastM, st.NorthM, st.RotationDeg, st.AltitudeM);
        else
        {   // centred on the chosen cadastral building (or on the screen), north up
            var (ce, cn) = SelectedFootprint >= 0 ? (Footprints[SelectedFootprint].Average(p => p.E), Footprints[SelectedFootprint].Average(p => p.N)) : MapCenter;
            var (bx, by) = PlacementCentre(); PlacementAnchor = new(ce - bx / 1000.0, cn - by / 1000.0);
        }
        SiteMessage = T(SelectedFootprint >= 0 ? "site.place.hint" : "site.place.drag"); SiteChanged?.Invoke();
    }
    /// <summary>Walls drawn while placing: the current level only (all levels on top of each other are unreadable).</summary>
    public IEnumerable<Wall> PlacementWalls() => CurrentLevel?.Walls is { Count: > 0 } w ? w : Building.Levels.SelectMany(l => l.Walls);
    /// <summary>Centre (mm) of the walls drawn while placing: the pivot of the rotation.</summary>
    public (double X, double Y) PlacementCentre()
    {
        var pts = PlacementWalls().SelectMany(w => w.Footprint).ToList(); if (pts.Count == 0) return (0, 0);
        return ((pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0, (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0);
    }
    public void MovePlacement(double dE, double dN) { PlacementAnchor = PlacementAnchor with { EastM = PlacementAnchor.EastM + dE, NorthM = PlacementAnchor.NorthM + dN }; SiteChanged?.Invoke(); }
    /// <summary>Turns the building about its centre (degrees, counter-clockwise).</summary>
    [RelayCommand] private void RotatePlacement(string degrees)
    {
        var d = double.Parse(degrees, System.Globalization.CultureInfo.InvariantCulture); var (cx, cy) = PlacementCentre();
        var before = PlacementAnchor.ToLambert((int)cx, (int)cy); var a = PlacementAnchor with { RotationDeg = PlacementAnchor.RotationDeg + d };
        var after = a.ToLambert((int)cx, (int)cy); PlacementAnchor = a with { EastM = a.EastM + before.E - after.E, NorthM = a.NorthM + before.N - after.N };
        SiteMessage = T("site.place.angle", PlacementAnchor.RotationDeg.ToString("0.0")); SiteChanged?.Invoke();
    }
    /// <summary>Keeps the position set by hand (one history step).</summary>
    [RelayCommand] private void ConfirmPlacement()
    {
        if (!Placing) return; var a = PlacementAnchor;
        var site = new SiteInfo { EastM = a.EastM, NorthM = a.NorthM, RotationDeg = a.RotationDeg, AltitudeM = Building.Site?.AltitudeM ?? 0, Address = SiteAddress.Length > 0 ? SiteAddress : Building.Site?.Address,
            Footprint = SelectedFootprint >= 0 ? Footprints[SelectedFootprint].Select(p => new[] { p.E, p.N }).ToList() : Building.Site?.Footprint ?? new(),
            Neighbours = new(), ExtraFootprints = new() };
        ApplySelection(site);
        Actions.SetSite(site); Placing = false; PlacementPlanPoints.Clear(); PlacementMapPoints.Clear(); SiteMessage = T("site.placed"); Status = SiteMessage; Refresh(); SiteChanged?.Invoke();
    }
    [RelayCommand] private void CancelPlacement() { Placing = false; PlacementPlanPoints.Clear(); PlacementMapPoints.Clear(); SiteMessage = ""; SiteChanged?.Invoke(); }
    public IEnumerable<PointMm> BuildingCorners() => Building.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).Distinct();

    /// <summary>Alternates building corner / footprint corner; the second pair computes anchor and rotation (one history step).</summary>
    public void PlacementClick(double e, double n)
    {
        if (!Placing) return;
        if (SelectedFootprint < 0) { SiteMessage = T("site.place.drag"); SiteChanged?.Invoke(); return; }   // no cadastral building chosen: placement by hand only
        var expectPlan = PlacementPlanPoints.Count == PlacementMapPoints.Count;
        if (expectPlan)
        {   // a click in map space is converted to the nearest corner of the building outline drawn at the current provisional anchor
            var corners = PlacementWalls().SelectMany(w => w.Footprint).Distinct().ToList(); if (corners.Count == 0) { SiteMessage = T("site.nowalls"); return; }
            var a = PlacementAnchor;
            var best = corners.MinBy(c => { var (ce, cn) = a.ToLambert(c.X, c.Y); return (ce - e) * (ce - e) + (cn - n) * (cn - n); }); PlacementPlanPoints.Add(best);
            SiteMessage = T("site.corner.chosen", PlacementPlanPoints.Count);
        }
        else
        {
            var ring = Footprints[SelectedFootprint]; var best = _selected.SelectMany(i => Footprints[i]).MinBy(p => (p.E - e) * (p.E - e) + (p.N - n) * (p.N - n)); PlacementMapPoints.Add(best);
            if (PlacementMapPoints.Count == 2)
            {
                var site = FootprintFit.Anchor(PlacementPlanPoints[0], PlacementPlanPoints[1], PlacementMapPoints[0], PlacementMapPoints[1], ring.Select(p => new[] { p.E, p.N }).ToList());
                if (site == null) { SiteMessage = T("site.corners.close"); PlacementPlanPoints.Clear(); PlacementMapPoints.Clear(); }
                else
                {
                    site.Address = SiteAddress.Length > 0 ? SiteAddress : null; ApplySelection(site);
                    Actions.SetSite(site); Placing = false; PlacementPlanPoints.Clear(); PlacementMapPoints.Clear(); SiteMessage = T("site.placed"); Status = SiteMessage; Refresh();
                }
            }
            else SiteMessage = T("site.corner2");
        }
        SiteChanged?.Invoke();
    }

    // ---- Cadastre-assisted checks: suggestions with a preview, never applied silently ----
    // ---- Several cadastral parcels for one building: they share the building's anchor ----
    private static bool SameRing(IReadOnlyList<double[]> a, List<(double E, double N)> b) => a.Count == b.Count && a.Count > 0 && Math.Abs(a[0][0] - b[0].E) < 1e-6 && Math.Abs(a[0][1] - b[0].N) < 1e-6;
    private static List<double[]> ToRing(List<(double E, double N)> r) => r.Select(p => new[] { p.E, p.N }).ToList();
    private bool IsExtra(List<(double E, double N)> r) => Building.Site?.ExtraFootprints.Any(e => SameRing(e, r)) == true;
    private bool IsPrimary(List<(double E, double N)> r) => Building.Site is { } s && SameRing(s.Footprint, r);
    private List<List<double[]>> ExtraRings() => Building.Site?.ExtraFootprints.ToList() ?? new();
    /// <summary>The cadastral buildings around that are not part of the building (party-wall candidates).</summary>
    /// <summary>The selected parcels become the building (the first is the main one), every other cadastral building a neighbour; with nothing selected the current split is kept.</summary>
    private void ApplySelection(SiteInfo site)
    {
        if (_selected.Count > 0)
        {
            site.ExtraFootprints = _selected.Skip(1).Select(i => ToRing(Footprints[i])).ToList();
            site.Neighbours = Footprints.Where((_, i) => !_selected.Contains(i)).Select(ToRing).ToList();
        }
        else { site.ExtraFootprints = ExtraRings(); site.Neighbours = Footprints.Where(r => !IsExtra(r) && !IsPrimary(r)).Select(ToRing).ToList(); }
    }
    public bool InBuilding(int index) => index >= 0 && index < Footprints.Count && (IsExtra(Footprints[index]) || IsPrimary(Footprints[index]));
    private SiteInfo CloneSite(SiteInfo s) => new() { EastM = s.EastM, NorthM = s.NorthM, RotationDeg = s.RotationDeg, AltitudeM = s.AltitudeM, Address = s.Address, Footprint = s.Footprint, ShowFootprint = s.ShowFootprint, ExtraFootprints = s.ExtraFootprints.ToList(), Neighbours = s.Neighbours.ToList() };

    /// <summary>Adds the selected parcels to the current building (same georeferencing). One history step.</summary>
    [RelayCommand] private void AddParcel()
    {
        if (Building.Site is not { } s) { SiteMessage = T("site.place.first"); return; }
        var add = _selected.Where(i => !InBuilding(i)).Select(i => Footprints[i]).ToList(); if (add.Count == 0) { SiteMessage = T("site.pick.first"); return; }
        var n = CloneSite(s); foreach (var ring in add) n.ExtraFootprints.Add(ToRing(ring)); n.Neighbours = n.Neighbours.Where(r => !add.Any(a => SameRing(r, a))).ToList();
        Actions.SetSite(n); SiteMessage = T("site.parcel.added", n.ExtraFootprints.Count + 1); Status = SiteMessage; Refresh(); SiteChanged?.Invoke();
    }
    /// <summary>Takes the selected extra parcels off the building (they become neighbours again).</summary>
    [RelayCommand] private void RemoveParcel()
    {
        var off = _selected.Select(i => Footprints[i]).Where(IsExtra).ToList();
        if (Building.Site is not { } s || off.Count == 0) { SiteMessage = T("site.parcel.pick"); return; }
        var n = CloneSite(s); n.ExtraFootprints = n.ExtraFootprints.Where(r => !off.Any(o => SameRing(r, o))).ToList(); foreach (var ring in off) n.Neighbours.Add(ToRing(ring));
        Actions.SetSite(n); SiteMessage = T("site.parcel.removed"); Status = SiteMessage; Refresh(); SiteChanged?.Invoke();
    }
    public List<List<PointMm>> ExtraFootprintModels() => Building.Site is { } s ? s.ExtraFootprints.Select(r => FootprintFit.ToModel(s, r)).ToList() : new();

    public List<PointMm>? FootprintModel() => Building.Site is { Footprint.Count: >= 3 } s ? FootprintFit.ToModel(s, s.Footprint) : null;
    public List<List<PointMm>> NeighbourModels() => Building.Site?.Neighbours.Select(r => FootprintFit.ToModel(Building.Site, r)).ToList() ?? new();
    public IReadOnlyList<double>? ReferenceAngles() => FootprintModel() is { } f ? ImmoCad.Plan.FootprintMatcher.ReferenceAngles(f.Select(p => new ImmoCad.Plan.PointPx(p.X, p.Y)).ToList()) : null;

    public List<WallShift> FitSuggestion() => FootprintModel() is { } f && CurrentLevel is { } l ? FootprintFit.Suggest(l, f, 300) : new();
    [RelayCommand] private void FitOuterWalls()
    {
        var shifts = FitSuggestion(); if (shifts.Count == 0 || CurrentLevel is not { } l) { Status = T("site.nofacade"); return; }
        Actions.FitOuterWalls(l.Id, shifts); Status = T("site.fitted", shifts.Count, shifts.Max(s => s.MaxMoveMm).ToString("0")); Refresh();
    }

    public ScaleSuggestion? ScaleCheck()
    {
        if (FootprintModel() is not { } f || CurrentLevel is not { } l) return null;
        var outline = l.Walls.Where(w => w.Footprint.Count >= 3).SelectMany(w => w.Footprint).ToList(); if (outline.Count < 3) return null;
        var hull = ConvexHull(outline); return FootprintFit.CheckScale(hull, f);
    }
    [ObservableProperty] private string _scaleText = "";
    [RelayCommand] private void CheckScale() { ScaleText = ScaleCheck()?.Message ?? T("site.place.first"); }
    [RelayCommand] private void ApplyScale()
    {
        if (ScaleCheck() is not { } s || CurrentLevel is not { } l || (Math.Abs(s.WidthPercent) < 0.5 && Math.Abs(s.HeightPercent) < 0.5)) { Status = T("site.nothing"); return; }
        Actions.ScaleLevel(l.Id, 1 / (1 + s.WidthPercent / 100), 1 / (1 + s.HeightPercent / 100), moveWalls: true); Status = T("site.scale.applied"); Refresh();
    }

    [RelayCommand] private void CheckAngles()
    {
        AngleIssues.Clear(); if (FootprintModel() is not { } f || CurrentLevel is not { } l) return;
        var outline = ConvexHull(l.Walls.SelectMany(w => w.Footprint).ToList()); if (outline.Count < 3) return;
        foreach (var c in FootprintFit.AngleCheck(outline, f, 2)) AngleIssues.Add(new CornerItem(c.Index, T("site.angle.item", c.Index + 1, c.PlanAngleDeg.ToString("0.0"), c.FootprintAngleDeg.ToString("0.0")), c.At));
        Status = AngleIssues.Count == 0 ? T("site.angles.ok") : T("site.angles.n", AngleIssues.Count);
    }
    [RelayCommand] private void FocusCorner(PointMm at) => FocusRequested?.Invoke(at);

    public List<int> PartyWallEdges() => FootprintModel() is { } f ? FootprintFit.SharedEdges(f, NeighbourModels()) : new();
    [ObservableProperty] private bool _straightenWithCadastre;

    private static List<PointMm> ConvexHull(List<PointMm> pts)
    {
        var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList(); if (p.Count < 3) return p;
        long Cross(PointMm o, PointMm a, PointMm b) => (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);
        var lower = new List<PointMm>(); foreach (var q in p) { while (lower.Count >= 2 && Cross(lower[^2], lower[^1], q) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(q); }
        var upper = new List<PointMm>(); foreach (var q in Enumerable.Reverse(p)) { while (upper.Count >= 2 && Cross(upper[^2], upper[^1], q) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(q); }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1); return lower.Concat(upper).ToList();
    }
}
