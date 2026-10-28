using System.Net;
using System.Text;
using ImmeubleCad.Geo;
using Xunit;

namespace ImmeubleCad.Geo.Tests;

public class GeoTests
{
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Calls++; return Task.FromResult(f(r)); }
    }
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "geo" + Guid.NewGuid().ToString("N"));

    [Fact] public async Task Geocoder_parses_and_works_offline_from_cache()
    {
        var online = true;
        var h = new Fake(_ => online
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"features":[{"geometry":{"coordinates":[5.9,47.0]}}]}""", Encoding.UTF8) }
            : throw new HttpRequestException("offline"));
        var g = new IgnGeocoder(new HttpClient(h), new DiskCache(Tmp()));
        Assert.Equal((47.0, 5.9), await g.GeocodeAsync("1 rue X"));
        online = false;
        Assert.Equal((47.0, 5.9), await g.GeocodeAsync("1 rue X")); // from disk cache
        Assert.Null(await g.GeocodeAsync("never seen"));
    }

    [Fact] public void Footprints_parse_multipolygon_and_drop_closing_point()
    {
        var json = """{"features":[{"geometry":{"type":"MultiPolygon","coordinates":[[[[0,0],[10,0],[10,5],[0,5],[0,0]]]]}},{"geometry":{"type":"Point","coordinates":[1,1]}}]}""";
        var r = IgnCadastre.ParseFootprints(Encoding.UTF8.GetBytes(json));
        Assert.Single(r); Assert.Equal(4, r[0].Count);
    }
}

public class OfflineTileTests
{
    private sealed class Flaky(bool[] online, byte[] png) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Calls++; return online[0] ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }) : throw new HttpRequestException("offline"); }
    }

    [Fact] public async Task A_site_seen_once_still_shows_its_tiles_footprints_and_attribution_with_the_network_off()
    {
        var online = new[] { true }; var png = new byte[] { 137, 80, 78, 71, 1, 2, 3 }; var cache = new DiskCache(Path.Combine(Path.GetTempPath(), "tiles" + Guid.NewGuid().ToString("N")));
        var h = new Flaky(online, png); var tiles = new IgnTiles(new HttpClient(h), cache);
        Assert.Equal(png, await tiles.GetTileAsync(IgnTiles.Orthophoto, 18, 134591, 93506));
        online[0] = false;
        Assert.Equal(png, await tiles.GetTileAsync(IgnTiles.Orthophoto, 18, 134591, 93506));                      // served from the disk cache
        Assert.Null(await tiles.GetTileAsync(IgnTiles.Orthophoto, 18, 1, 1));                                      // never seen: nothing to show, no exception
        Assert.Contains("IGN", IgnTiles.Attribution); Assert.Contains("DGFiP", IgnTiles.Attribution);
        // the footprints are cached the same way
        online[0] = true; var cad = new IgnCadastre(new HttpClient(new Flaky(online, System.Text.Encoding.UTF8.GetBytes("""{"features":[{"geometry":{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,5],[0,5],[0,0]]]}}]}"""))), cache);
        Assert.Single(await cad.BuildingFootprintsAsync(45.76, 4.83, 100)); online[0] = false; Assert.Single(await cad.BuildingFootprintsAsync(45.76, 4.83, 100));
    }
}
