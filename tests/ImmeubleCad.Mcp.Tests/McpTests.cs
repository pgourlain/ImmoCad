using System.Diagnostics;
using System.Text.Json;
using ImmeubleCad.Mcp;
using Xunit;

namespace ImmeubleCad.Mcp.Tests;

public class McpTests
{
    [Fact] public void Tools_edit_session_with_undo()
    {
        var s = new McpSession(); var t = new BuildingTools(s);
        t.AddLevel("RDC", 0);
        t.AddRiser("ColdWater", 0, 0, 0, 2700);
        Assert.Single(t.BillOfMaterials(), x => x.Contains("2.7 m"));
        t.Undo();
        Assert.Empty(t.BillOfMaterials());
    }

    [Fact] public void Export_writes_file()
    {
        var s = new McpSession(); var t = new BuildingTools(s);
        var lv = t.AddLevel("RDC", 0); t.AddWall(lv, 0, 0, 1000, 200);
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ifc");
        t.Export("ifc", p); Assert.Contains("IFCWALL", File.ReadAllText(p)); File.Delete(p);
    }

    [Fact] public async Task Real_process_over_stdio_lists_tools()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "ImmeubleCad.dll");
        Assert.True(File.Exists(dll), dll);
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --mcp") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await proc.StandardInput.FlushAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string? line, all = "";
        while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token)) != null) { all += line; if (line.Contains("\"id\":2")) break; }
        proc.Kill();
        Assert.Contains("add_riser", all); Assert.Contains("export", all);
        using var _ = JsonDocument.Parse(line!);
    }
}
