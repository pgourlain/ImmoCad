using ModelContextProtocol.Client;
using Xunit;

namespace ImmoCad.Mcp.Tests;

public class LiveServerTests
{
    private sealed class Direct : IUiMarshaller { public int Calls; public ValueTask<T> Run<T>(Func<ValueTask<T>> work) { Calls++; return work(); } }

    [Fact] public async Task An_agent_edits_the_session_over_http_and_only_loopback_hosts_are_served()
    {
        var session = new McpSession(); var ui = new Direct(); var port = 39100 + Random.Shared.Next(800);
        await using var server = new LiveMcpServer(port); await server.StartAsync(session, ui);
        await using (var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(server.Url) }), cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Contains((await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)), t => t.Name == "add_level");
            await client.CallToolAsync("add_level", new Dictionary<string, object?> { ["name"] = "RDC", ["altitudeMm"] = 0 }, cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.Single(session.Building.Levels); Assert.True(ui.Calls > 0);                      // the edit reached the session, through the UI marshaller

        using var http = new HttpClient();
        var bad = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") }; bad.Headers.Host = "evil.example";
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(bad, TestContext.Current.CancellationToken)).StatusCode);   // DNS rebinding is refused
    }
}
