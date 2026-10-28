using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmoCad.Core.Site;

namespace ImmoCad.Geo;

/// <summary>Disk cache so the site keeps working offline once loaded.</summary>
public sealed class DiskCache(string dir)
{
    public string PathFor(string key) { Directory.CreateDirectory(dir); return Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]); }
    public byte[]? Get(string key) { var p = PathFor(key); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
    public void Put(string key, byte[] data) => File.WriteAllBytes(PathFor(key), data);
}

public abstract class CachedHttp(HttpClient http, DiskCache cache)
{
    /// <summary>Network first, cache fallback (and vice versa when offline).</summary>
    protected async Task<byte[]?> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var data = await http.GetByteArrayAsync(url, ct);
            cache.Put(url, data); return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return cache.Get(url);
        }
    }
}

public sealed class IgnGeocoder(HttpClient http, DiskCache cache) : CachedHttp(http, cache), IGeocoder
{
    public async Task<(double Lat, double Lon)?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        var data = await GetAsync("https://data.geopf.fr/geocodage/search?limit=1&q=" + Uri.EscapeDataString(address), ct);
        if (data == null) return null;
        using var doc = JsonDocument.Parse(data);
        var feats = doc.RootElement.GetProperty("features");
        if (feats.GetArrayLength() == 0) return null;
        var c = feats[0].GetProperty("geometry").GetProperty("coordinates");
        return (c[1].GetDouble(), c[0].GetDouble());
    }
}

/// <summary>Building footprints from the IGN WFS (BD TOPO), in Lambert 93.</summary>
public sealed class IgnCadastre(HttpClient http, DiskCache cache) : CachedHttp(http, cache), ICadastreService
{
    public async Task<IReadOnlyList<IReadOnlyList<(double E, double N)>>> BuildingFootprintsAsync(double lat, double lon, int radiusM, CancellationToken ct = default)
    {
        var (e, n) = Lambert93.FromWgs84(lat, lon); var f = (Func<double, string>)(v => v.ToString("0.##", CultureInfo.InvariantCulture));
        var url = "https://data.geopf.fr/wfs/ows?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature&TYPENAMES=BDTOPO_V3:batiment&OUTPUTFORMAT=application/json&SRSNAME=EPSG:2154" +
                  $"&BBOX={f(e - radiusM)},{f(n - radiusM)},{f(e + radiusM)},{f(n + radiusM)},EPSG:2154";
        var data = await GetAsync(url, ct);
        return data == null ? [] : ParseFootprints(data);
    }

    public static IReadOnlyList<IReadOnlyList<(double E, double N)>> ParseFootprints(byte[] geoJson)
    {
        var res = new List<IReadOnlyList<(double, double)>>();
        using var doc = JsonDocument.Parse(geoJson);
        foreach (var feat in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var g = feat.GetProperty("geometry"); var type = g.GetProperty("type").GetString(); var coords = g.GetProperty("coordinates");
            IEnumerable<JsonElement> polygons = type == "MultiPolygon" ? coords.EnumerateArray() : type == "Polygon" ? [coords] : [];
            foreach (var poly in polygons)
            {
                var ring = poly[0].EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList();
                if (ring.Count > 1 && ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);
                res.Add(ring);
            }
        }
        return res;
    }
}

public sealed class IgnTiles(HttpClient http, DiskCache cache) : CachedHttp(http, cache), IMapTileSource
{
    public static string Attribution => ImmoCad.Core.Translations.T("attribution");
    public const string Orthophoto = "ORTHOIMAGERY.ORTHOPHOTOS", PlanIgn = "GEOGRAPHICALGRIDSYSTEMS.PLANIGNV2", Cadastre = "CADASTRALPARCELS.PARCELLAIRE_EXPRESS";
    public Task<byte[]?> GetTileAsync(string layer, int z, int x, int y, CancellationToken ct = default)
    {
        var (format, style) = layer == Orthophoto ? ("image/jpeg", "normal") : layer == Cadastre ? ("image/png", "PCI vecteur") : ("image/png", "normal");
        return GetAsync($"https://data.geopf.fr/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={layer}&STYLE={Uri.EscapeDataString(style)}&FORMAT={format}&TILEMATRIXSET=PM&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}", ct);
    }
}
