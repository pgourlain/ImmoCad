using ImmoCad.Core.Model;

namespace ImmoCad.Core.History;

/// <summary>Manual = the user tagged it on purpose. Auto = the app tagged it as a safety net before a risky operation (bounded, see <see cref="VersionStore.MaxAutoVersions"/>).</summary>
public enum VersionKind { Manual, Auto }

/// <summary>A permanent, named checkpoint saved inside the file: a full snapshot of the building, separate from the in-memory undo history (<see cref="BuildingHistory"/>), which is lost when the app closes.</summary>
public sealed class BuildingVersion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Label { get; init; } = "";
    public string Note { get; init; } = "";
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public VersionKind Kind { get; init; } = VersionKind.Manual;
    public int SchemaVersion { get; init; }
    /// <summary>Full <see cref="BuildingJson"/> serialization of the building at the time this version was tagged.</summary>
    public string Snapshot { get; init; } = "";
    /// <summary>The background images this version's levels pointed to, captured at tag time (name → bytes). Needed because a later re-import can overwrite that same name with different bytes in the live image set, which would otherwise silently corrupt old versions. Stored content-addressed on disk (see <see cref="IO.BuildingFile"/>), so an unchanged image across many versions costs nothing extra.</summary>
    public IReadOnlyDictionary<string, byte[]> Images { get; init; } = new Dictionary<string, byte[]>();
}

/// <summary>Metadata only (no snapshot): what <c>versions/index.json</c> holds in the .immeuble file.</summary>
public sealed record VersionMeta(Guid Id, string Label, string Note, DateTimeOffset Created, VersionKind Kind, int SchemaVersion);

/// <summary>The versions tagged on a building. Unlike <see cref="BuildingHistory"/>, this is not an edit: tagging or removing a version does not go through <see cref="BuildingActions"/> and is not undoable. Restoring one IS an edit (see <see cref="BuildingActions.RestoreVersion"/>).</summary>
public sealed class VersionStore
{
    /// <summary>Auto versions beyond this count are dropped, oldest first. Manual versions are never dropped automatically.</summary>
    public const int MaxAutoVersions = 10;

    private readonly List<BuildingVersion> _versions = new();
    public IReadOnlyList<BuildingVersion> Versions => _versions;
    public event Action? Changed;

    /// <param name="images">The live background-image set (see <see cref="Model.Level.BackgroundImage"/>); only the entries this building's levels actually point to are kept, so an unrelated image never inflates this version.</param>
    public BuildingVersion Tag(Building b, IReadOnlyDictionary<string, byte[]> images, string label, string note = "", VersionKind kind = VersionKind.Manual)
    {
        var referenced = b.Levels.Select(l => l.BackgroundImage).Where(n => n != null).ToHashSet();
        var kept = images.Where(kv => referenced.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var v = new BuildingVersion { Label = label, Note = note, Kind = kind, SchemaVersion = b.SchemaVersion, Snapshot = BuildingJson.Serialize(b), Images = kept };
        _versions.Add(v);
        if (kind == VersionKind.Auto)
        {
            var autos = _versions.Where(x => x.Kind == VersionKind.Auto).ToList();
            while (autos.Count > MaxAutoVersions) { _versions.Remove(autos[0]); autos.RemoveAt(0); }
        }
        Changed?.Invoke();
        return v;
    }

    public BuildingVersion? Get(Guid id) => _versions.FirstOrDefault(v => v.Id == id);
    public void Remove(Guid id) { _versions.RemoveAll(v => v.Id == id); Changed?.Invoke(); }

    /// <summary>Upper bound on what the versions add to the file: snapshot JSON plus images, summed per version. The real cost on disk is usually lower — identical images across versions are stored once (see <see cref="IO.BuildingFile"/>).</summary>
    public long EstimatedBytes => _versions.Sum(v => (long)v.Snapshot.Length + v.Images.Values.Sum(img => (long)img.Length));

    /// <summary>Replaces the whole set, e.g. right after loading a file. Does not raise <see cref="Changed"/> (the caller refreshes once).</summary>
    public void LoadFrom(IEnumerable<BuildingVersion> versions) { _versions.Clear(); _versions.AddRange(versions); }
}
