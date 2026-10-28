using System.Text.Json;
using System.Text.Json.Serialization;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.IO;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core;

public sealed class AppSettings
{
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public string LastTool { get; set; } = "select";
    public int SnapGridMm { get; set; } = 100;
    public bool AnimateTransitions { get; set; } = true;
    public bool ShowGhostBelow { get; set; } = true;
    public string Language { get; set; } = "fr";
    public List<string> RecentFiles { get; set; } = new();
    public List<RiserTemplate> RiserTemplates { get; set; } = new();

    public void AddRecent(string path) { RecentFiles.Remove(path); RecentFiles.Insert(0, path); if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10); }
}

public sealed record RiserTemplate(string Name, int SpacingMm, List<NetworkType> Types);

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class SettingsJsonContext : JsonSerializerContext { }

public static class SettingsStore
{
    public static AppSettings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings) ?? new() : new(); }
        catch (JsonException) { return new(); } // corrupt settings must never block startup
    }
    public static void Save(AppSettings s, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings)); File.Move(tmp, path, true);
    }
}

/// <summary>Autosave to the app-data folder; offered at next start when the previous run did not close cleanly.</summary>
public sealed class AutosaveService(string dir)
{
    private string Path_ => Path.Combine(dir, "autosave.immeuble");
    private string Flag => Path.Combine(dir, "running.flag");

    public void Save(Building b) { Directory.CreateDirectory(dir); BuildingFile.Save(b, Path_ + ".tmp"); File.Move(Path_ + ".tmp", Path_, true); }
    public void MarkStarted() { Directory.CreateDirectory(dir); File.WriteAllText(Flag, DateTime.UtcNow.ToString("O")); }
    public void MarkCleanExit() { if (File.Exists(Flag)) File.Delete(Flag); if (File.Exists(Path_)) File.Delete(Path_); }
    /// <summary>True when the previous session crashed and left an autosave.</summary>
    public bool HasRecovery => File.Exists(Flag) && File.Exists(Path_);
    public Building? Recover() => HasRecovery ? BuildingFile.Load(Path_).Building : null;
}

public static class SampleBuilding
{
    /// <summary>3-level sample: walls, a stairwell space, a cold-water and heating riser, one radiator.</summary>
    public static (Building Building, BuildingHistory History) Create()
    {
        var b = new Building { Name = "Exemple" }; var h = new BuildingHistory(); var a = new BuildingActions(b, h, new BuildingEventsService());
        for (var i = 0; i < 3; i++)
        {
            var l = a.AddLevel(i == 0 ? "RDC" : $"R+{i}", i * 2700, 2700, i == 0 ? LevelKind.Ground : LevelKind.Standard);
            a.ImportWalls(l.Id, new[]
            {
                new[] { new PointMm(0, 0), new PointMm(8000, 0), new PointMm(8000, 200), new PointMm(0, 200) },
                new[] { new PointMm(0, 5800), new PointMm(8000, 5800), new PointMm(8000, 6000), new PointMm(0, 6000) },
                new[] { new PointMm(0, 0), new PointMm(200, 0), new PointMm(200, 6000), new PointMm(0, 6000) },
                new[] { new PointMm(7800, 0), new PointMm(8000, 0), new PointMm(8000, 6000), new PointMm(7800, 6000) },
            });
            a.AddSpace(l.Id, [new(200, 200), new(2200, 200), new(2200, 2200), new(200, 2200)], "Cage d'escalier", SpaceKind.Stairwell);
        }
        a.AddRiserTemplate(new(300, 300), 150, [NetworkType.HeatingSupply, NetworkType.HeatingReturn], 0, 8100, 28);
        a.AddEquipment(b.Levels[0].Id, "radiator", new(3000, 250), 150);
        h.MarkClean();
        return (b, h);
    }
}
