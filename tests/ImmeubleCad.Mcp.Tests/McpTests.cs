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

    [Fact] public void Open_new_refuse_unsaved_and_paths_are_restricted()
    {
        var s = new McpSession(); var t = new BuildingTools(s);
        t.AddLevel("RDC", 0);
        Assert.Throws<InvalidOperationException>(() => t.New("x"));
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".immeuble");
        t.Save(p); Assert.Throws<IOException>(() => t.Save(p, overwrite: false));
        t.New("x"); Assert.Empty(s.Building.Levels);
        Assert.Equal("1 levels", t.Open(p)); File.Delete(p);
        Assert.Throws<UnauthorizedAccessException>(() => t.Export("obj", "/etc/immeuble-test.obj"));
    }

    [Fact] public void Export_glb_and_pdf_and_resources()
    {
        var s = new McpSession(); var t = new BuildingTools(s); var lv = t.AddLevel("RDC", 0); t.AddWall(lv, 0, 0, 1000, 200); t.AddRiser("ColdWater", 0, 0, 0, 2700);
        var d = Path.Combine(Path.GetTempPath(), "mcp" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        t.Export("glb", Path.Combine(d, "a.glb")); t.Export("bom_pdf", Path.Combine(d, "a.pdf"));
        Assert.True(new FileInfo(Path.Combine(d, "a.glb")).Length > 100); Assert.StartsWith("%PDF", File.ReadAllText(Path.Combine(d, "a.pdf"), System.Text.Encoding.Latin1));
        var r = new BuildingResources(s); Assert.Contains("ColdWater", r.Networks("current")); Assert.Contains("radiator", r.Library());
        Directory.Delete(d, true);
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
