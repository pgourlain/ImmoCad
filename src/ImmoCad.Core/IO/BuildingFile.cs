using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmoCad.Core.History;
using ImmoCad.Core.Model;

namespace ImmoCad.Core.IO;

/// <summary>.immeuble = zip with building.json, images/ (content-addressed: one blob per distinct image, named by its SHA-256, plus a name→blob map), and optionally versions/ (tagged checkpoints, see <see cref="VersionStore"/>).</summary>
public static class BuildingFile
{
    /// <summary>Writes to a temporary file next to <paramref name="path"/> then swaps it in, so a crash or a full disk mid-write never leaves a half-written file where the real one was.</summary>
    public static void Save(Building b, string path, IReadOnlyDictionary<string, byte[]>? images = null, IReadOnlyList<BuildingVersion>? versions = null)
    {
        var tmp = path + ".tmp" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                using (var w = Text(zip, "building.json")) w.Write(BuildingJson.Serialize(b));

                // Images are already-compressed pixel data (PNG today): re-deflating them bought nothing but cost ~1s/70MB, so they're stored verbatim.
                // Content-addressed by SHA-256, so the same picture used by several levels/versions (a common case: only walls changed) is written once.
                var blobs = new Dictionary<string, byte[]>();
                string Intern(string logicalName, byte[] data)
                {
                    var ext = Path.GetExtension(logicalName); if (ext.Length == 0) ext = ".png";
                    var blob = "images/" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + ext;
                    blobs[blob] = data;
                    return blob;
                }
                void WriteMap(string entryName, IReadOnlyDictionary<string, byte[]> set)
                {
                    if (set.Count == 0) return;
                    var map = set.ToDictionary(kv => kv.Key, kv => Intern(kv.Key, kv.Value));
                    using var w = Text(zip, entryName); w.Write(JsonSerializer.Serialize(map, BuildingJsonContext.Default.DictionaryStringString));
                }

                if (images is { Count: > 0 }) WriteMap("images/map.json", images);
                if (versions is { Count: > 0 })
                {
                    var meta = versions.Select(v => new VersionMeta(v.Id, v.Label, v.Note, v.Created, v.Kind, v.SchemaVersion)).ToList();
                    using (var w = Text(zip, "versions/index.json")) w.Write(JsonSerializer.Serialize(meta, BuildingJsonContext.Default.ListVersionMeta));
                    foreach (var v in versions)
                    {
                        using (var w = Text(zip, $"versions/{v.Id}.json")) w.Write(v.Snapshot);
                        WriteMap($"versions/{v.Id}.images.json", v.Images);
                    }
                }
                foreach (var (blob, data) in blobs)
                { using var s = zip.CreateEntry(blob, CompressionLevel.NoCompression).Open(); s.Write(data); }
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    private static StreamWriter Text(ZipArchive zip, string entryName) => new(zip.CreateEntry(entryName, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));

    public static (Building Building, Dictionary<string, byte[]> Images) Load(string path)
    {
        var (b, imgs, _) = LoadWithVersions(path);
        return (b, imgs);
    }

    public static (Building Building, Dictionary<string, byte[]> Images, List<BuildingVersion> Versions) LoadWithVersions(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var e = zip.GetEntry("building.json") ?? throw new InvalidDataException("building.json missing");
        var b = BuildingJson.Deserialize(ReadText(e));

        Dictionary<string, byte[]> ReadImages(string mapEntryName, bool legacyFallback)
        {
            var result = new Dictionary<string, byte[]>();
            if (zip.GetEntry(mapEntryName) is { } mapEntry)
            {
                var map = JsonSerializer.Deserialize(ReadText(mapEntry), BuildingJsonContext.Default.DictionaryStringString) ?? [];
                foreach (var (name, blob) in map) result[name] = ReadBlob(zip, blob);
            }
            else if (legacyFallback)   // files saved before content-addressing: images/<name> directly, no map
                foreach (var x in zip.Entries.Where(x => x.FullName.StartsWith("images/") && x.Length > 0))
                    result[x.Name] = ReadBlob(zip, x.FullName);
            return result;
        }

        var imgs = ReadImages("images/map.json", legacyFallback: true);

        var versions = new List<BuildingVersion>();
        if (zip.GetEntry("versions/index.json") is { } idx)
            foreach (var m in JsonSerializer.Deserialize(ReadText(idx), BuildingJsonContext.Default.ListVersionMeta) ?? [])
            {
                var ve = zip.GetEntry($"versions/{m.Id}.json"); if (ve == null) continue;   // missing snapshot: skip rather than fail the whole load
                var vimgs = ReadImages($"versions/{m.Id}.images.json", legacyFallback: false);   // absent on versions saved before this field existed: no images for that old version
                versions.Add(new BuildingVersion { Id = m.Id, Label = m.Label, Note = m.Note, Created = m.Created, Kind = m.Kind, SchemaVersion = m.SchemaVersion, Snapshot = ReadText(ve), Images = vimgs });
            }
        return (b, imgs, versions);
    }

    private static string ReadText(ZipArchiveEntry e) { using var r = new StreamReader(e.Open(), Encoding.UTF8); return r.ReadToEnd(); }
    private static byte[] ReadBlob(ZipArchive zip, string entryPath)
    {
        var x = zip.GetEntry(entryPath) ?? throw new InvalidDataException($"missing image blob: {entryPath}");
        using var ms = new MemoryStream(); using var s = x.Open(); s.CopyTo(ms); return ms.ToArray();
    }
}
