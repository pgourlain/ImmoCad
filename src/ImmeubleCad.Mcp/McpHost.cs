using System.ComponentModel;
using ImmeubleCad.Core;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace ImmeubleCad.Mcp;

public sealed class McpSession
{
    public Building Building { get; } = new();
    public BuildingHistory History { get; } = new();
    public BuildingActions Actions { get; }
    public McpSession() => Actions = new(Building, History, new BuildingEventsService());
}

[McpServerToolType]
public sealed class BuildingTools(McpSession s)
{
    [McpServerTool, Description("Add a level (altitude and height in mm). Returns its id.")]
    public string AddLevel(string name, int altitudeMm, int heightMm = 2700) => s.Actions.AddLevel(name, altitudeMm, heightMm).Id.ToString();

    [McpServerTool, Description("List levels as 'id name altitudeMm'.")]
    public string[] ListLevels() => s.Building.Levels.Select(l => $"{l.Id} {l.Name} {l.AltitudeMm}").ToArray();

    [McpServerTool, Description("Add a vertical riser (pipe) at XY mm between two altitudes in a new network. Returns the network id.")]
    public string AddRiser(string type, int xMm, int yMm, int fromZMm, int toZMm, int diameterMm = 20)
    {
        var n = s.Actions.AddNetwork("Riser", Enum.Parse<NetworkType>(type, true));
        s.Actions.AddPipeRun(n.Id, [new(xMm, yMm, fromZMm), new(xMm, yMm, toZMm)], diameterMm);
        return n.Id.ToString();
    }

    [McpServerTool, Description("Undo last action.")]
    public string Undo() { s.History.Undo(s.Building); return "ok"; }

    [McpServerTool, Description("Redo last undone action.")]
    public string Redo() { s.History.Redo(s.Building); return "ok"; }

    [McpServerTool, Description("Bill of materials: pipe length per network and diameter, in metres.")]
    public string[] BillOfMaterials() => Bom.Pipes(s.Building).Select(x => $"{x.Network} {x.DiameterMm}mm {x.LengthM:0.###} m").ToArray();
}

public static class McpHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var b = Host.CreateApplicationBuilder(args);
        b.Logging.ClearProviders(); b.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        b.Services.AddSingleton<McpSession>();
        b.Services.AddMcpServer().WithStdioServerTransport().WithTools<BuildingTools>();
        await b.Build().RunAsync();
        return 0;
    }
}
