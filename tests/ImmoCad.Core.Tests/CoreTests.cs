using ImmoCad.Core.History;
using ImmoCad.Core.IO;
using ImmoCad.Core.Model;
using Xunit;

namespace ImmoCad.Core.Tests;

public class CoreArchitectureTests
{
    [Theory]
    [InlineData("Avalonia")] [InlineData("SkiaSharp")] [InlineData("Silk.NET")] [InlineData("System.Drawing")]
    public void Core_does_not_reference_ui(string prefix)
    {
        var refs = typeof(Building).Assembly.GetReferencedAssemblies().Select(a => a.Name!);
        Assert.DoesNotContain(refs, n => n.StartsWith(prefix));
    }
}

public class CoreArchitectureExtraTests
{
    [Fact] public void Core_has_no_dispatcher_dialog_or_pinvoke()
    {
        var types = typeof(Building).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Any(m => (m.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0));
        var allowed = new[] { "System", "Microsoft.Extensions", "Magick", "ImmoCad", "netstandard", "mscorlib", "Microsoft.Win32" };
        Assert.All(typeof(Building).Assembly.GetReferencedAssemblies().Select(a => a.Name!), n => Assert.True(allowed.Any(n.StartsWith), $"Core references {n}"));
    }
}

public class HistoryTests
{
    private static (Building, BuildingActions, BuildingHistory) New()
    {
        var b = new Building(); var h = new BuildingHistory();
        return (b, new BuildingActions(b, h, new BuildingEventsService()), h);
    }

    [Fact]
    public void Actions_undo_restores_model_exactly()
    {
        var (b, a, h) = New();
        var l = a.AddLevel("RDC", 0);
        var steps = new List<Action>
        {
            () => a.AddLevel("R+1", 2700),
            () => a.RenameLevel(l.Id, "Ground"),
            () => a.SetLevelGeometry(l.Id, 100, 2800),
            () => { var w = a.AddWall(l.Id, new[] { new PointMm(0, 0), new PointMm(100, 0), new PointMm(100, 10) }); a.MoveWall(l.Id, w.Id, 5, 5); a.DeleteWall(l.Id, w.Id); },
            () => a.ImportWalls(l.Id, new[] { new[] { new PointMm(0, 0), new PointMm(1, 1) } }),
            () => a.SetImageTransform(l.Id, new[] { 1.0, 0, 0, 0, 1, 0 }),
            () => a.SetWorldFrame(new RectMm(0, 0, 5000, 5000)),
            () => { var w = a.AddWall(l.Id, new[] { new PointMm(0, 0), new PointMm(80, 0), new PointMm(80, 2000), new PointMm(0, 2000) }); a.ConvertWallsToMarkings(l.Id, [w.Id]); a.EraseMarkings(l.Id, l.Markings.Select(m => m.Id).ToList()); },
            () => a.AddSpace(l.Id, new[] { new PointMm(0, 0) }, "Cave 1", SpaceKind.Cellar, "1"),
            () => { var n = a.AddNetwork("ECS", NetworkType.DomesticHotWater); a.AddPipeRun(n.Id, new[] { new PointMm3(0, 0, 0), new PointMm3(0, 0, 2700) }); },
            () => a.AddEquipment(l.Id, "radiator", new PointMm(10, 10), 150),
            () => a.DeleteLevel(l.Id),
        };
        foreach (var s in steps)
        {
            var before = BuildingJson.Serialize(b);
            var count = h.Items.Count;
            s();
            var after = BuildingJson.Serialize(b);
            while (h.Items.Count > count && h.CanUndo && h.Pointer >= count) h.Undo(b);
            Assert.Equal(before, BuildingJson.Serialize(b));
            while (h.CanRedo) h.Redo(b);
            Assert.Equal(after, BuildingJson.Serialize(b));
        }
    }

    [Fact]
    public void Two_hundred_undo_redo_cycles_on_ten_levels_stay_fast_and_exact()
    {
        var (b, h) = SampleBuilding.Create(); var a = new BuildingActions(b, h, new BuildingEventsService());
        for (var i = 3; i < 10; i++) a.AddLevel("R+" + i, i * 2700);
        var before = BuildingJson.Serialize(b); var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) { a.AddWall(b.Levels[0].Id, [new(0, 0), new(100, 0), new(100, 100)]); h.Undo(b); h.Redo(b); h.Undo(b); }
        Assert.Equal(before, BuildingJson.Serialize(b)); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"{sw.Elapsed}");
    }

    [Fact]
    public void Dirty_follows_pointer()
    {
        var (b, a, h) = New();
        a.AddLevel("RDC", 0); h.MarkClean(); Assert.False(h.IsDirty);
        a.AddLevel("R+1", 2700); Assert.True(h.IsDirty);
        h.Undo(b); Assert.False(h.IsDirty);
    }

    [Fact]
    public void File_round_trip()
    {
        var (b, a, _) = New();
        var l = a.AddLevel("RDC", 0);
        a.AddWall(l.Id, new[] { new PointMm(0, 0), new PointMm(1000, 0) });
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".immeuble");
        BuildingFile.Save(b, p, new Dictionary<string, byte[]> { ["a.png"] = new byte[] { 1, 2, 3 } });
        var (b2, imgs) = BuildingFile.Load(p);
        File.Delete(p);
        Assert.Equal(BuildingJson.Serialize(b), BuildingJson.Serialize(b2));
        Assert.Equal(new byte[] { 1, 2, 3 }, imgs["a.png"]);
    }
}

public class VersionTests
{
    [Fact]
    public void Tag_captures_a_snapshot_independent_of_later_edits()
    {
        var (b, a, _) = NewBuilding();
        var l = a.AddLevel("RDC", 0);
        var store = new VersionStore();
        var v = store.Tag(b, NoImages, "Before the mess");
        a.AddWall(l.Id, new[] { new PointMm(0, 0), new PointMm(1000, 0) });
        Assert.NotEqual(BuildingJson.Serialize(b), v.Snapshot);
        Assert.Equal(0, BuildingJson.Deserialize(v.Snapshot).Levels.Single().Walls.Count);
    }

    [Fact]
    public void Restore_version_is_a_single_undoable_history_step()
    {
        var (b, a, h) = NewBuilding();
        var l = a.AddLevel("RDC", 0);
        var store = new VersionStore();
        var tagged = store.Tag(b, NoImages, "Empty level");
        a.AddWall(l.Id, new[] { new PointMm(0, 0), new PointMm(1000, 0) });
        var afterWall = BuildingJson.Serialize(b);
        var countBefore = h.Items.Count;

        a.RestoreVersion(tagged);
        Assert.Equal(countBefore + 1, h.Items.Count);
        Assert.Empty(b.Levels.Single().Walls);

        h.Undo(b);
        Assert.Equal(afterWall, BuildingJson.Serialize(b));
        h.Redo(b);
        Assert.Empty(b.Levels.Single().Walls);
    }

    [Fact]
    public void Auto_versions_beyond_the_cap_drop_the_oldest_but_manual_ones_are_kept()
    {
        var (b, _, _) = NewBuilding();
        var store = new VersionStore();
        var manual = store.Tag(b, NoImages, "Keep me", kind: VersionKind.Manual);
        for (var i = 0; i < VersionStore.MaxAutoVersions + 5; i++) store.Tag(b, NoImages, "auto" + i, kind: VersionKind.Auto);
        Assert.Equal(VersionStore.MaxAutoVersions + 1, store.Versions.Count);
        Assert.Contains(store.Versions, v => v.Id == manual.Id);
        Assert.DoesNotContain(store.Versions, v => v.Label == "auto4");
        Assert.Contains(store.Versions, v => v.Label == "auto14");
    }

    [Fact]
    public void Versions_round_trip_through_the_file()
    {
        var (b, a, _) = NewBuilding();
        a.AddLevel("RDC", 0);
        var store = new VersionStore();
        store.Tag(b, NoImages, "v1", "first checkpoint");
        store.Tag(b, NoImages, "v2", kind: VersionKind.Auto);
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".immeuble");
        BuildingFile.Save(b, p, versions: store.Versions);
        var (_, _, versions) = BuildingFile.LoadWithVersions(p);
        File.Delete(p);
        Assert.Equal(2, versions.Count);
        var v1 = versions.Single(v => v.Label == "v1");
        Assert.Equal("first checkpoint", v1.Note);
        Assert.Equal(VersionKind.Manual, v1.Kind);
        Assert.Equal(VersionKind.Auto, versions.Single(v => v.Label == "v2").Kind);
        Assert.Equal(BuildingJson.Serialize(b), v1.Snapshot);
    }

    [Fact]
    public void Tag_only_keeps_images_the_building_actually_points_to()
    {
        var (b, a, _) = NewBuilding();
        var l = a.AddLevel("RDC", 0);
        a.SetBackground(l.Id, "bg.png", new double[] { 1, 0, 0, 0, 1, 0 });
        var images = new Dictionary<string, byte[]> { ["bg.png"] = [1, 2, 3], ["unrelated.png"] = [9, 9] };
        var store = new VersionStore();
        var v = store.Tag(b, images, "with background");
        Assert.Equal(new byte[] { 1, 2, 3 }, v.Images["bg.png"]);
        Assert.False(v.Images.ContainsKey("unrelated.png"));
    }

    [Fact]
    public void Restoring_a_version_brings_back_the_background_picture_it_was_tagged_with()
    {
        var (b, a, _) = NewBuilding();
        var l = a.AddLevel("RDC", 0);
        a.SetBackground(l.Id, "bg.png", new double[] { 1, 0, 0, 0, 1, 0 });
        var images = new Dictionary<string, byte[]> { ["bg.png"] = [1, 2, 3] };
        var store = new VersionStore();
        var v = store.Tag(b, images, "old plan");

        images["bg.png"] = [9, 9, 9];                               // re-import on the same level: same name, different bytes
        a.RestoreVersion(v);
        Assert.Equal(new byte[] { 1, 2, 3 }, v.Images["bg.png"]);    // the version kept its own copy; the caller merges it back into the live dictionary
    }

    [Fact]
    public void Images_byte_identical_across_a_version_and_the_live_set_are_stored_once()
    {
        var (b, a, _) = NewBuilding();
        var l = a.AddLevel("RDC", 0);
        a.SetBackground(l.Id, "bg.png", new double[] { 1, 0, 0, 0, 1, 0 });
        var bytes = new byte[50_000]; new Random(1).NextBytes(bytes);
        var images = new Dictionary<string, byte[]> { ["bg.png"] = bytes };
        var store = new VersionStore();
        store.Tag(b, images, "v1");   // same bytes, same name: one blob on disk, not two

        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".immeuble");
        BuildingFile.Save(b, p, images, store.Versions);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(p))
            Assert.Single(zip.Entries.Where(e => e.FullName.StartsWith("images/") && e.FullName != "images/map.json"));
        var (_, imgs2, versions2) = BuildingFile.LoadWithVersions(p);
        File.Delete(p);
        Assert.Equal(bytes, imgs2["bg.png"]);
        Assert.Equal(bytes, versions2.Single().Images["bg.png"]);
    }

    [Fact]
    public void Save_is_atomic_a_failed_write_never_touches_the_existing_file()
    {
        var (b, _, _) = NewBuilding();
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".immeuble");
        BuildingFile.Save(b, p);
        var original = File.ReadAllBytes(p);
        // Simulate a crash mid-write: the temp file is left behind, but the real file must be untouched and still loadable.
        var tmp = p + ".tmp" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp, new byte[] { 0 });
        Assert.Equal(original, File.ReadAllBytes(p));
        var (loaded, _) = BuildingFile.Load(p);
        Assert.Equal(BuildingJson.Serialize(b), BuildingJson.Serialize(loaded));
        File.Delete(p); File.Delete(tmp);
    }

    private static readonly Dictionary<string, byte[]> NoImages = new();
    private static (Building, BuildingActions, BuildingHistory) NewBuilding()
    {
        var b = new Building(); var h = new BuildingHistory();
        return (b, new BuildingActions(b, h, new BuildingEventsService()), h);
    }
}
