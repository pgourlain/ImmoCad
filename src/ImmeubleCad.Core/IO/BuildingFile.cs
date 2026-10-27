using System.IO.Compression;
using System.Text;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.IO;

/// <summary>.immeuble = zip with building.json and images/.</summary>
public static class BuildingFile
{
    public static void Save(Building b, string path, IReadOnlyDictionary<string, byte[]>? images = null)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("building.json").Open(), new UTF8Encoding(false))) w.Write(BuildingJson.Serialize(b));
        if (images != null)
            foreach (var (name, data) in images)
            { using var s = zip.CreateEntry("images/" + name).Open(); s.Write(data); }
    }

    public static (Building Building, Dictionary<string, byte[]> Images) Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var e = zip.GetEntry("building.json") ?? throw new InvalidDataException("building.json missing");
        using var r = new StreamReader(e.Open(), Encoding.UTF8);
        var b = BuildingJson.Deserialize(r.ReadToEnd());
        var imgs = new Dictionary<string, byte[]>();
        foreach (var x in zip.Entries.Where(x => x.FullName.StartsWith("images/") && x.Length > 0))
        { using var ms = new MemoryStream(); using var s = x.Open(); s.CopyTo(ms); imgs[x.Name] = ms.ToArray(); }
        return (b, imgs);
    }
}
