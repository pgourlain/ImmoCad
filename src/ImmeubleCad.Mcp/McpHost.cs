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
    public Building Building { get; private set; } = new();
    public BuildingHistory History { get; private set; } = new();
    public BuildingActions Actions { get; private set; }
    public string? Path { get; set; }
    /// <summary>Folders the agent may read/write; empty = the temp folder and the user's home only.</summary>
    public List<string> AllowedFolders { get; } = new() { System.IO.Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
    public McpSession() => Actions = new(Building, History, new BuildingEventsService());

    public void Replace(Building b) { Building = b; History = new BuildingHistory(); Actions = new(Building, History, new BuildingEventsService()); }

    public string Check(string path, bool write, bool overwrite = true)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!AllowedFolders.Any(f => full.StartsWith(System.IO.Path.GetFullPath(f), StringComparison.Ordinal))) throw new UnauthorizedAccessException($"Path not in an allowed folder: {full}");
        if (write && !overwrite && File.Exists(full)) throw new IOException($"{full} exists; pass overwrite=true");
        return full;
    }
    public void EnsureSaved(bool discardChanges) { if (History.IsDirty && !discardChanges) throw new InvalidOperationException("Unsaved changes; save first or pass discardChanges=true"); }
}

[McpServerResourceType]
public sealed class BuildingResources(McpSession s)
{
    [McpServerResource(UriTemplate = "immeuble://buildings", Name = "buildings", MimeType = "application/json")]
    public string Buildings() => System.Text.Json.JsonSerializer.Serialize(new[] { new { id = "current", name = s.Building.Name, path = s.Path, levels = s.Building.Levels.Count } });

    [McpServerResource(UriTemplate = "immeuble://buildings/{id}/networks", Name = "networks", MimeType = "application/json")]
    public string Networks(string id) => System.Text.Json.JsonSerializer.Serialize(s.Building.Networks.Select(n => new { id = n.Id, n.Name, type = n.Type.ToString(), nodes = n.Nodes.Count, segments = n.Segments.Count }));

    [McpServerResource(UriTemplate = "immeuble://equipment-library", Name = "equipment-library", MimeType = "application/json")]
    public string Library() => System.Text.Json.JsonSerializer.Serialize(ImmeubleCad.Core.Catalog.EquipmentCatalog.Default.Select(e => new { e.Id, e.Category, e.Name, e.WidthMm, e.DepthMm, e.HeightMm, e.Inline, ports = e.Ports.Count }));
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

    [McpServerTool, Description("Add a wall to a level from a rectangle (mm).")]
    public string AddWall(string levelId, int x0, int y0, int x1, int y1) =>
        s.Actions.AddWall(Guid.Parse(levelId), [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)]).Id.ToString();

    [McpServerTool, Description("Add a pipe run through a list of 'x,y,z' points (mm). Returns the network id.")]
    public string AddPipeRun(string type, string[] points, int diameterMm = 20)
    {
        var pts = points.Select(p => p.Split(',').Select(int.Parse).ToArray()).Select(a => new PointMm3(a[0], a[1], a[2])).ToList();
        var n = s.Actions.AddNetwork("Run", Enum.Parse<NetworkType>(type, true));
        s.Actions.AddPipeRun(n.Id, pts, diameterMm);
        return n.Id.ToString();
    }

    [McpServerTool, Description("Export the building. format: obj, dxf, freecad, ifc, glb, bom_csv, bom_pdf. Writes to path and returns it.")]
    public string Export(string format, string path, bool overwrite = true)
    {
        path = s.Check(path, true, overwrite);
        switch (format)
        {
            case "glb": File.WriteAllBytes(path, ImmeubleCad.Core.Export.GltfExporter.ExportGlb(s.Building)); break;
            case "bom_pdf": File.WriteAllBytes(path, ImmeubleCad.Core.Export.BomFull.ToPdf(s.Building)); break;
            case "bom_csv": File.WriteAllText(path, ImmeubleCad.Core.Export.BomFull.ToCsv(s.Building)); break;
            default:
                var e = ImmeubleCad.Core.Export.Exporters.All.FirstOrDefault(x => x.Id == format) ?? throw new ArgumentException("unknown format " + format);
                File.WriteAllText(path, e.Export(s.Building)); break;
        }
        return path;
    }

    [McpServerTool, Description("Save the building to a .immeuble file.")]
    public string Save(string path, bool overwrite = true) { path = s.Check(path, true, overwrite); ImmeubleCad.Core.IO.BuildingFile.Save(s.Building, path); s.History.MarkClean(); s.Path = path; return path; }

    [McpServerTool, Description("Open a .immeuble file. Fails when the current building has unsaved changes unless discardChanges=true.")]
    public string Open(string path, bool discardChanges = false)
    {
        s.EnsureSaved(discardChanges); path = s.Check(path, false);
        s.Replace(ImmeubleCad.Core.IO.BuildingFile.Load(path).Building); s.Path = path; return $"{s.Building.Levels.Count} levels";
    }

    [McpServerTool, Description("Start a new empty building. Fails when there are unsaved changes unless discardChanges=true.")]
    public string New(string name = "", bool discardChanges = false)
    { s.EnsureSaved(discardChanges); s.Replace(new Building { Name = name }); s.Path = null; return "ok"; }

    [McpServerTool, Description("Close the current building (same as New with an empty name).")]
    public string Close(bool discardChanges = false) => New("", discardChanges);

    [McpServerTool, Description("Import a plan image on a level: calibrate with pxPerMm, detect walls (minThickness in px, 0 threshold = automatic).")]
    public string ImportPlan(string levelId, string imagePath, double pxPerMm, int minThickness = 5, int threshold = 0)
    {
        imagePath = s.Check(imagePath, false);
        var n = ImmeubleCad.Core.Import.PlanImport.Import(s.Actions, Guid.Parse(levelId), File.ReadAllBytes(imagePath), new(pxPerMm, pxPerMm),
            new ImmeubleCad.Core.Import.PlanWallDetectorAdapter(), new ImmeubleCad.Plan.WallDetectionOptions(threshold, minThickness));
        return $"{n} wall group(s) imported";
    }

    [McpServerTool, Description("List walls of a level as 'id label pointCount'.")]
    public string[] ListWalls(string levelId) => s.Building.Levels.First(l => l.Id == Guid.Parse(levelId)).Walls.Select(w => $"{w.Id} {w.Label} {w.Footprint.Count}").ToArray();

    [McpServerTool, Description("Connect a radiator/equipment port (x,y,z mm) to a node of an existing network with orthogonal routing.")]
    public string ConnectEquipment(string networkId, int x, int y, int z, string toNodeId, int diameterMm = 14) =>
        $"{s.Actions.ConnectOrthogonal(Guid.Parse(networkId), new(x, y, z), Guid.Parse(toNodeId), diameterMm).Count} segment(s)";

    [McpServerTool, Description("Add a riser template: several risers side by side (types comma-separated, e.g. 'ColdWater,DomesticHotWater').")]
    public string AddRiserTemplate(string types, int xMm, int yMm, int spacingMm, int fromZMm, int toZMm, int diameterMm = 20) =>
        string.Join(",", s.Actions.AddRiserTemplate(new(xMm, yMm), spacingMm, types.Split(',').Select(t => Enum.Parse<NetworkType>(t.Trim(), true)).ToList(), fromZMm, toZMm, diameterMm).Select(n => n.Id));

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
        b.Services.AddMcpServer().WithStdioServerTransport().WithTools<BuildingTools>().WithResources<BuildingResources>();
        await b.Build().RunAsync();
        return 0;
    }
}
