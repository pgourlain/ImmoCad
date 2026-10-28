using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmeubleCad.Core.Geometry;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Site;
using static ImmeubleCad.Core.Translations;

namespace ImmeubleCad.Desktop;

/// <summary>Everything the site level needs, behind the Core interfaces (HTTP + disk cache in the app, fakes in tests).</summary>
public sealed record SiteData(IGeocoder Geocoder, ICadastreService Cadastre, IMapTileSource Tiles);

public sealed record FootprintItem(int Index, string Label, double AreaM2) { public string Text => ImmeubleCad.Core.Translations.T("site.item.area", Label, AreaM2.ToString("0")); }

public partial class MainViewModel
{
    public SiteData? Geo { get; set; }
    public static string Attribution => T("attribution");
    public string[] MapLayers => [T("map.ortho"), T("map.plan"), T("map.cadastre")];
    [ObservableProperty] private string _siteAddress = "";
    [ObservableProperty] private int _mapLayer;                                // 0 orthophoto, 1 plan IGN, 2 cadastre
    [ObservableProperty] private bool _showFootprint = true;
    [ObservableProperty] private string _siteMessage = "";
    public (double E, double N) MapCenter { get; set; } = (650000, 6860000);
    public double MapScale { get; set; } = 3;                                   // px per metre
    public List<List<(double E, double N)>> Footprints { get; } = new();        // cadastral buildings around the centre, Lambert 93
    public int SelectedFootprint { get; private set; } = -1;
    public ObservableCollection<FootprintItem> FootprintItems { get; } = new();
    public ObservableCollection<CornerItem> AngleIssues { get; } = new();
    public event Action? SiteChanged;

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
        Footprints.Clear(); FootprintItems.Clear(); SelectedFootprint = -1;
        var rings = await Geo!.Cadastre.BuildingFootprintsAsync(lat, lon, 120);
        foreach (var r in rings) Footprints.Add(r.ToList());
        for (var i = 0; i < Footprints.Count; i++) FootprintItems.Add(new FootprintItem(i, T("site.building.n", i + 1), Area(Footprints[i])));
        SiteMessage += T("site.found", Footprints.Count);
    }

    private static double Area(List<(double E, double N)> r) { double a = 0; for (var i = 0; i < r.Count; i++) { var (p, q) = (r[i], r[(i + 1) % r.Count]); a += p.E * q.N - q.E * p.N; } return Math.Abs(a) / 2; }

    public void SelectFootprint(int index) { SelectedFootprint = index >= 0 && index < Footprints.Count ? index : -1; SiteChanged?.Invoke(); }
    [RelayCommand] private void PickFootprint(int index) => SelectFootprint(index);

    /// <summary>Picks the cadastral building under a click (Lambert metres), if any.</summary>
    public void PickFootprintAt(double e, double n)
    {
        for (var i = Footprints.Count - 1; i >= 0; i--) if (InPoly(Footprints[i], e, n)) { SelectFootprint(i); return; }
        SelectFootprint(-1);
    }
    private static bool InPoly(List<(double E, double N)> r, double x, double y) { var inside = false; for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) if ((r[i].N > y) != (r[j].N > y) && x < (r[j].E - r[i].E) * (y - r[i].N) / (r[j].N - r[i].N) + r[i].E) inside = !inside; return inside; }

    /// <summary>Start a new building from the selected footprint: model origin on its first corner, X axis along its first edge, outer walls pre-drawn (300 mm, inside the outline). One history step for the site, one for the walls.</summary>
    [RelayCommand] private void CreateFromFootprint()
    {
        if (SelectedFootprint < 0) { SiteMessage = T("site.pick.first"); return; }
        if (ActiveIndex < 0) NewDocument();
        var ring = Footprints[SelectedFootprint]; var rot = Math.Atan2(ring[1].N - ring[0].N, ring[1].E - ring[0].E);
        var site = new SiteInfo { EastM = ring[0].E, NorthM = ring[0].N, RotationDeg = rot * 180 / Math.PI, Address = SiteAddress.Length > 0 ? SiteAddress : null, Footprint = ring.Select(p => new[] { p.E, p.N }).ToList(), Neighbours = Footprints.Where((_, i) => i != SelectedFootprint).Select(r => r.Select(p => new[] { p.E, p.N }).ToList()).ToList() };
        Actions.SetSite(site);
        var model = FootprintFit.ToModel(site, site.Footprint); if (CurrentLevel is not { } l) return;
        double cx = model.Average(p => (double)p.X), cy = model.Average(p => (double)p.Y); var walls = new List<IEnumerable<PointMm>>();
        for (var i = 0; i < model.Count; i++)
        {
            var a = model[i]; var b = model[(i + 1) % model.Count]; double dx = b.X - a.X, dy = b.Y - a.Y; var len = Math.Sqrt(dx * dx + dy * dy); if (len < 400) continue;
            double nx = -dy / len, ny = dx / len; if ((cx - a.X) * nx + (cy - a.Y) * ny < 0) { nx = -nx; ny = -ny; }          // normal pointing inside
            const int t = 300; walls.Add([a, b, new((int)(b.X + nx * t), (int)(b.Y + ny * t)), new((int)(a.X + nx * t), (int)(a.Y + ny * t))]);
        }
        Actions.ImportWalls(l.Id, walls); Mode = ViewMode.Plan; Status = T("site.created", walls.Count); Refresh(); ViewFitRequested?.Invoke();
    }

    // ---- Place an existing building: two corners of the plan, then the matching two corners of the footprint ----
    public List<PointMm> PlacementPlanPoints { get; } = new(); public List<(double E, double N)> PlacementMapPoints { get; } = new();
    public bool Placing { get; private set; }
    [RelayCommand] private void BeginPlacement()
    {
        if (SelectedFootprint < 0) { SiteMessage = T("site.pick.first"); return; }
        Placing = true; PlacementPlanPoints.Clear(); PlacementMapPoints.Clear(); SiteMessage = T("site.place.hint"); SiteChanged?.Invoke();
    }
    public IEnumerable<PointMm> BuildingCorners() => Building.Levels.SelectMany(l => l.Walls.SelectMany(w => w.Footprint)).Distinct();

    /// <summary>Alternates building corner / footprint corner; the second pair computes anchor and rotation (one history step).</summary>
    public void PlacementClick(double e, double n)
    {
        if (!Placing) return;
        var expectPlan = PlacementPlanPoints.Count == PlacementMapPoints.Count;
        if (expectPlan)
        {   // a click in map space is converted to the nearest corner of the building outline drawn at the current provisional anchor
            var corners = BuildingCorners().ToList(); if (corners.Count == 0) { SiteMessage = T("site.nowalls"); return; }
            var a = Building.Site != null ? new SiteAnchor(Building.Site.EastM, Building.Site.NorthM, Building.Site.RotationDeg) : new SiteAnchor(MapCenter.E, MapCenter.N, 0);
            var best = corners.MinBy(c => { var (ce, cn) = a.ToLambert(c.X, c.Y); return (ce - e) * (ce - e) + (cn - n) * (cn - n); }); PlacementPlanPoints.Add(best);
            SiteMessage = T("site.corner.chosen", PlacementPlanPoints.Count);
        }
        else
        {
            var ring = Footprints[SelectedFootprint]; var best = ring.MinBy(p => (p.E - e) * (p.E - e) + (p.N - n) * (p.N - n)); PlacementMapPoints.Add(best);
            if (PlacementMapPoints.Count == 2)
            {
                var site = FootprintFit.Anchor(PlacementPlanPoints[0], PlacementPlanPoints[1], PlacementMapPoints[0], PlacementMapPoints[1], ring.Select(p => new[] { p.E, p.N }).ToList());
                if (site == null) { SiteMessage = T("site.corners.close"); PlacementPlanPoints.Clear(); PlacementMapPoints.Clear(); }
                else
                {
                    site.Address = SiteAddress.Length > 0 ? SiteAddress : null; site.Neighbours = Footprints.Where((_, i) => i != SelectedFootprint).Select(r => r.Select(p => new[] { p.E, p.N }).ToList()).ToList();
                    Actions.SetSite(site); Placing = false; PlacementPlanPoints.Clear(); PlacementMapPoints.Clear(); SiteMessage = T("site.placed"); Status = SiteMessage; Refresh();
                }
            }
            else SiteMessage = T("site.corner2");
        }
        SiteChanged?.Invoke();
    }

    // ---- Cadastre-assisted checks: suggestions with a preview, never applied silently ----
    public List<PointMm>? FootprintModel() => Building.Site is { Footprint.Count: >= 3 } s ? FootprintFit.ToModel(s, s.Footprint) : null;
    public List<List<PointMm>> NeighbourModels() => Building.Site?.Neighbours.Select(r => FootprintFit.ToModel(Building.Site, r)).ToList() ?? new();
    public IReadOnlyList<double>? ReferenceAngles() => FootprintModel() is { } f ? ImmeubleCad.Plan.FootprintMatcher.ReferenceAngles(f.Select(p => new ImmeubleCad.Plan.PointPx(p.X, p.Y)).ToList()) : null;

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
