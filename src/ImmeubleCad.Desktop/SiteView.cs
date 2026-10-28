using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImmeubleCad.Core.Geometry;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Site;
using Microsoft.Extensions.Logging;

namespace ImmeubleCad.Desktop;

/// <summary>
/// The site level: map tiles (orthophoto, IGN plan or cadastre) drawn in Lambert 93, cadastral buildings as outlines, and the building's own footprint through the site anchor.
/// Pan with the left button, wheel zooms. Tiles come from <see cref="IMapTileSource"/> (disk-cached, so a site already seen still shows offline).
/// </summary>
public class SiteView : Control
{
    private static readonly ILogger Log = App.Log.CreateLogger("SiteView");
    private MainViewModel? Vm => DataContext as MainViewModel;
    private readonly Dictionary<string, Bitmap?> _tiles = new(); private readonly HashSet<string> _loading = new();
    private Point? _pan; private Point _down;
    public int TilesDrawn { get; private set; }
    internal int RenderCount { get; private set; }
    internal string LastTileInfo { get; private set; } = "";

    public SiteView() { ClipToBounds = true; DataContextChanged += (_, _) => { if (Vm != null) Vm.SiteChanged += () => { InvalidateVisual(); }; }; }

    private Point ToScreen(double e, double n) => new(Bounds.Width / 2 + (e - Vm!.MapCenter.E) * Vm.MapScale, Bounds.Height / 2 - (n - Vm.MapCenter.N) * Vm.MapScale);
    private (double E, double N) ToMap(Point p) => (Vm!.MapCenter.E + (p.X - Bounds.Width / 2) / Vm.MapScale, Vm.MapCenter.N - (p.Y - Bounds.Height / 2) / Vm.MapScale);

    /// <summary>Slippy zoom matching the current scale (px per metre) so that tiles are drawn close to 1:1.</summary>
    private int Zoom() { var (lat, _) = Lambert93.ToWgs84(Vm!.MapCenter.E, Vm.MapCenter.N); var res = 1 / Vm.MapScale; return Math.Clamp((int)Math.Round(Math.Log2(156543.03392 * Math.Cos(lat * Math.PI / 180) / res)), 12, 19); }

    private static string Layer(int i) => i switch { 0 => Geo.IgnTiles.Orthophoto, 1 => Geo.IgnTiles.PlanIgn, _ => Geo.IgnTiles.Cadastre };

    protected override void OnPointerPressed(PointerPressedEventArgs e) { Focus(); _down = e.GetPosition(this); _pan = _down; e.Pointer.Capture(this); }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_pan is not { } l || Vm == null) return; var p = e.GetPosition(this);
        Vm.MapCenter = (Vm.MapCenter.E - (p.X - l.X) / Vm.MapScale, Vm.MapCenter.N + (p.Y - l.Y) / Vm.MapScale); _pan = p; InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _pan = null; e.Pointer.Capture(null); var p = e.GetPosition(this);
        if (Vm == null || Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 4) return;
        var (me, mn) = ToMap(p);
        if (Vm.Placing) Vm.PlacementClick(me, mn); else Vm.PickFootprintAt(me, mn); InvalidateVisual();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Vm == null) return; var p = e.GetPosition(this); var before = ToMap(p); Vm.MapScale = Math.Clamp(Vm.MapScale * (e.Delta.Y > 0 ? 1.25 : 0.8), 0.15, 40);
        var after = ToMap(p); Vm.MapCenter = (Vm.MapCenter.E + before.E - after.E, Vm.MapCenter.N + before.N - after.N); InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(214, 218, 222)), new Rect(Bounds.Size));
        RenderCount++; if (Vm == null) return; TilesDrawn = 0;
        DrawTiles(ctx);
        foreach (var (ring, i) in Vm.Footprints.Select((r, i) => (r, i)))
        {
            var sel = i == Vm.SelectedFootprint; var g = Poly(ring.Select(p => ToScreen(p.E, p.N)).ToArray());
            ctx.DrawGeometry(new SolidColorBrush(sel ? Color.FromArgb(70, 255, 140, 0) : Color.FromArgb(25, 40, 90, 200)), new Pen(sel ? Brushes.OrangeRed : Brushes.SteelBlue, sel ? 2.5 : 1.2), g);
        }
        if (Vm.Building.Site is { } site)
        {   // the building through its anchor
            var anchor = new SiteAnchor(site.EastM, site.NorthM, site.RotationDeg, site.AltitudeM);
            foreach (var w in Vm.Building.Levels.SelectMany(l => l.Walls))
            {
                var pts = w.Footprint.Select(p => { var (e, n) = anchor.ToLambert(p.X, p.Y); return ToScreen(e, n); }).ToArray(); if (pts.Length < 3) continue;
                ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 70, 72, 78)), new Pen(Brushes.Black, 0.8), Poly(pts));
            }
        }
        foreach (var (pt, k) in Vm.PlacementMapPoints.Select((p, k) => (p, k))) { var s = ToScreen(pt.E, pt.N); ctx.DrawEllipse(Brushes.DeepPink, new Pen(Brushes.White, 2), s, 6, 6); ctx.DrawText(Txt((k + 1).ToString(), 12, Brushes.DeepPink), s + new Point(8, -16)); }
        if (Vm.Placing && Vm.Building.Site == null) DrawProvisionalBuilding(ctx);
        // attribution and scale
        var att = Txt(MainViewModel.Attribution, 11, Brushes.Black); ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), null, new Rect(Bounds.Width - att.Width - 14, Bounds.Height - att.Height - 10, att.Width + 10, att.Height + 4)); ctx.DrawText(att, new Point(Bounds.Width - att.Width - 9, Bounds.Height - att.Height - 8));
        ctx.DrawText(Txt(Vm.SiteMessage, 12, Brushes.Black), new Point(12, 8)); DrawNorth(ctx);
    }

    private void DrawProvisionalBuilding(DrawingContext ctx)
    {   // before the first placement the building is drawn at the map centre so its corners can be picked
        var a = new SiteAnchor(Vm!.MapCenter.E, Vm.MapCenter.N, 0);
        foreach (var w in Vm.Building.Levels.SelectMany(l => l.Walls)) { var pts = w.Footprint.Select(p => { var (e, n) = a.ToLambert(p.X, p.Y); return ToScreen(e, n); }).ToArray(); if (pts.Length >= 3) ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 255, 100, 0)), new Pen(Brushes.OrangeRed, 1), Poly(pts)); }
    }

    private void DrawNorth(DrawingContext ctx)
    {
        var c = new Point(Bounds.Width - 40, 50); ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), new Pen(Brushes.Gray, 1), c, 22, 22);
        var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(c + new Point(0, -17), true); cx.LineTo(c + new Point(7, 9)); cx.LineTo(c + new Point(0, 4)); cx.LineTo(c + new Point(-7, 9)); cx.EndFigure(true); }
        ctx.DrawGeometry(Brushes.Black, null, g); ctx.DrawText(Txt("N", 11, Brushes.Black), c + new Point(-4, -34));
    }

    private void DrawTiles(DrawingContext ctx)
    {
        if (Vm!.Geo == null) return; var z = Zoom(); var layer = Layer(Vm.MapLayer);
        var (lat0, lon0) = Lambert93.ToWgs84(ToMap(new Point(0, 0)).E, ToMap(new Point(0, 0)).N); var (lat1, lon1) = Lambert93.ToWgs84(ToMap(new Point(Bounds.Width, Bounds.Height)).E, ToMap(new Point(Bounds.Width, Bounds.Height)).N);
        var (xa, ya) = WebMercator.TileOf(Math.Max(lat0, lat1), Math.Min(lon0, lon1), z); var (xb, yb) = WebMercator.TileOf(Math.Min(lat0, lat1), Math.Max(lon0, lon1), z);
        LastTileInfo = $"z={z} x={xa}..{xb} y={ya}..{yb} lat={lat0:0.000}/{lat1:0.000}";
        if ((xb - xa + 1) * (yb - ya + 1) > 120) return;                                      // too many tiles: zoomed out too far for this layer
        for (var y = ya - 1; y <= yb + 1; y++)
            for (var x = xa - 1; x <= xb + 1; x++)
            {
                var key = $"{layer}/{z}/{x}/{y}";
                if (!_tiles.TryGetValue(key, out var bmp)) { Request(key, layer, z, x, y); continue; }
                if (bmp == null) continue;
                var (tl, tr, bl) = WebMercator.LambertCorners(x, y, z); var a = ToScreen(tl.E, tl.N); var b = ToScreen(tr.E, tr.N); var c = ToScreen(bl.E, bl.N);
                // affine: image pixel space (0..256) → the tile's corners
                var sx = 1.0 / bmp.PixelSize.Width; var sy = 1.0 / bmp.PixelSize.Height;
                var m = new Matrix((b.X - a.X) * sx, (b.Y - a.Y) * sx, (c.X - a.X) * sy, (c.Y - a.Y) * sy, a.X, a.Y);
                using (ctx.PushTransform(m)) ctx.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height)); TilesDrawn++;
            }
    }

    private void Request(string key, string layer, int z, int x, int y)
    {
        if (!_loading.Add(key)) return;
        var source = Vm!.Geo!.Tiles;                                   // read on the UI thread: Avalonia properties cannot be touched from the pool
        _ = Task.Run(async () =>
        {
            Bitmap? bmp = null;
            try { var data = await source.GetTileAsync(layer, z, x, y); if (data != null) bmp = new Bitmap(new MemoryStream(data)); }
            catch (Exception ex) { Log.LogWarning(ex, "Tile {Key} not available", key); }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _tiles[key] = bmp; _loading.Remove(key); InvalidateVisual(); });
        });
    }

    internal bool TileLoaded(string layer, int z, int x, int y) => _tiles.TryGetValue($"{layer}/{z}/{x}/{y}", out var b) && b != null;
    internal int TilesInMemory => _tiles.Count(t => t.Value != null);
    private static StreamGeometry Poly(Point[] pts) { var g = new StreamGeometry(); using var c = g.Open(); c.BeginFigure(pts[0], true); foreach (var p in pts.Skip(1)) c.LineTo(p); c.EndFigure(true); return g; }
    private static FormattedText Txt(string t, double size, IBrush b) => new(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, b);
}
