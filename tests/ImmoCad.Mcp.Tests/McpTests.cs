using System.Diagnostics;
using System.Text.Json;
using ImmoCad.Mcp;
using Xunit;

namespace ImmoCad.Mcp.Tests;

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
        var dll = Path.Combine(AppContext.BaseDirectory, "ImmoCad.dll");
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

public class McpExportAliasTests
{
    [Fact] public void Named_export_tools_ifc_import_and_reference_models()
    {
        var s = new McpSession(); var t = new BuildingTools(s); var lv = t.AddLevel("RDC", 0); t.AddWall(lv, 0, 0, 4000, 200); t.AddRiser("ColdWater", 100, 100, 0, 2700);
        var d = Path.Combine(Path.GetTempPath(), "mcpx" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        t.ExportIfc(Path.Combine(d, "a.ifc")); t.ExportFreecadMacro(Path.Combine(d, "a.FCMacro")); t.ExportDxf(Path.Combine(d, "a.dxf")); t.ExportGltf(Path.Combine(d, "a.glb"));
        Assert.Contains("IFCWALL", File.ReadAllText(Path.Combine(d, "a.ifc"))); Assert.Contains("makeCylinder", File.ReadAllText(Path.Combine(d, "a.FCMacro"))); Assert.Contains("LINE", File.ReadAllText(Path.Combine(d, "a.dxf"))); Assert.True(File.ReadAllBytes(Path.Combine(d, "a.glb")).Length > 100);
        var other = new McpSession(); var t2 = new BuildingTools(other); Assert.StartsWith("1 level(s), 1 wall(s)", t2.ImportIfc(Path.Combine(d, "a.ifc")));
        File.WriteAllText(Path.Combine(d, "box.obj"), "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n"); Assert.Equal("ok", t2.ImportReference(other.Building.Levels[0].Id.ToString(), Path.Combine(d, "box.obj"))); Assert.Single(other.Building.Levels[0].References);
        Directory.Delete(d, true);
    }
}

public class McpTwoJpgTests
{
    private static byte[] PlanPng(int shift)
    {
        const int W = 600, H = 480; var px = Enumerable.Repeat((byte)255, W * H).ToArray();
        for (var y = 40; y < 440; y++) for (var x = 50; x < 550; x++) if (x < 60 || x >= 540 || y < 50 || y >= 430) px[(y + shift) * W + x] = 0;
        using var img = new ImageMagick.MagickImage(px, new ImageMagick.PixelReadSettings(W, H, ImageMagick.StorageType.Char, "I")); return img.ToByteArray(ImageMagick.MagickFormat.Png);
    }

    [Fact] public void Agent_builds_a_two_level_building_from_two_images_and_adds_risers()
    {
        var s = new McpSession(); var t = new BuildingTools(s); var d = Path.Combine(Path.GetTempPath(), "mcp2" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, "rdc.png"), PlanPng(0)); File.WriteAllBytes(Path.Combine(d, "r1.png"), PlanPng(3));
        var rdc = t.AddLevel("RDC", 0); var r1 = t.AddLevel("R+1", 2700);
        Assert.Contains("1 wall group", t.ImportPlan(rdc, Path.Combine(d, "rdc.png"), 0.1)); Assert.Contains("1 wall group", t.ImportPlan(r1, Path.Combine(d, "r1.png"), 0.1));
        Assert.True(s.Building.Levels.Count(l => l.Walls.Count == 1) == 2, string.Join(",", s.Building.Levels.Select(l => l.DisplayName + ":" + l.Walls.Count))); var shape = s.Building.Levels[0].Walls[0]; Assert.Equal(5000, shape.Footprint.Max(p => p.X) - shape.Footprint.Min(p => p.X));       // 500 px at 0.1 px/mm
        t.AddRiser("ColdWater", 500, 500, 0, 5400); t.AddRiserTemplate("HeatingSupply,HeatingReturn", 800, 500, 150, 0, 5400, 22);
        Assert.Equal(3, s.Building.Networks.Count); Assert.Contains(t.BillOfMaterials(), l => l.Contains("5.4 m"));
        t.Undo(); Assert.Single(s.Building.Networks); Directory.Delete(d, true);   // the template (2 networks) was one history step
    }
}
