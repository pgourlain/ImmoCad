using System.Text;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Core.Networks;
using static ImmeubleCad.Core.Translations;

namespace ImmeubleCad.Core.Export;

public sealed record BomFitting(string Network, FittingKind Kind, int DiameterMm, int Count);
public sealed record BomEquipment(string CatalogId, string Name, int Count);

/// <summary>Full bill of materials: pipes (optionally rounded up to standard bars), fittings, equipment.</summary>
public static class BomFull
{
    public const double BarLengthM = 3.0;

    public static IReadOnlyList<BomFitting> Fittings(Building b) =>
        b.Networks.SelectMany(n => FittingResolver.Resolve(n, b.Levels).Where(f => f.Kind != FittingKind.EndCap && f.Kind != FittingKind.Joint)
            .Select(f => (n.Type.ToString(), f.Kind, D: Dn(n, f.NodeId)))).GroupBy(x => x).Select(g => new BomFitting(g.Key.Item1, g.Key.Kind, g.Key.D, g.Count())).OrderBy(x => x.Network).ThenBy(x => x.Kind).ToList();

    private static int Dn(Network n, Guid node) => n.Segments.Where(s => s.A == node || s.B == node).Max(s => s.DiameterMm);

    public static IReadOnlyList<BomEquipment> Equipment(Building b) =>
        b.Equipment.GroupBy(e => e.CatalogId).Select(g => new BomEquipment(g.Key, Catalog.EquipmentCatalog.Find(g.Key)?.Name ?? g.Key, g.Count())).ToList();

    public static string ToCsv(Building b, bool roundToBars = false)
    {
        var sb = new StringBuilder(T("bom.csv.header") + "\n");
        foreach (var p in Bom.Pipes(b)) sb.AppendLine($"{T("bom.sec.pipe")},{p.Network},{T("bom.sec.pipe")},{p.DiameterMm},{(roundToBars ? Math.Ceiling(p.LengthM / BarLengthM) : Math.Round(p.LengthM, 3)).ToString(System.Globalization.CultureInfo.InvariantCulture)},{(roundToBars ? T("bom.bars") : "m")}");
        foreach (var f in Fittings(b)) sb.AppendLine($"{T("bom.sec.fitting")},{f.Network},{f.Kind},{f.DiameterMm},{f.Count},pcs");
        foreach (var e in Equipment(b)) sb.AppendLine($"{T("bom.sec.equipment")},,{e.Name},,{e.Count},pcs");
        return sb.ToString();
    }

    /// <summary>Minimal single-page text PDF of the bill of materials (no external library).</summary>
    public static byte[] ToPdf(Building b)
    {
        var lines = new List<string> { $"{T("bom.title")} - {b.DisplayName}", "" };
        lines.Add(T("bom.pipes")); lines.AddRange(Bom.Pipes(b).Select(p => $"  {p.Network}  DN{p.DiameterMm}  {p.LengthM:0.00} m  ({Math.Ceiling(p.LengthM / BarLengthM)} {T("bom.bars")})"));
        lines.Add(""); lines.Add(T("bom.fittings")); lines.AddRange(Fittings(b).Select(f => $"  {f.Network}  {f.Kind}  DN{f.DiameterMm}  x{f.Count}"));
        lines.Add(""); lines.Add(T("bom.equipment")); lines.AddRange(Equipment(b).Select(e => $"  {e.Name}  x{e.Count}"));
        var content = new StringBuilder("BT /F1 11 Tf 50 790 Td 14 TL\n");
        foreach (var l in lines.Take(52)) content.Append('(').Append(l.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*\n");
        content.Append("ET");
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
        };
        var o = new StringBuilder("%PDF-1.4\n"); var offs = new List<int>();
        for (var i = 0; i < objs.Count; i++) { offs.Add(o.Length); o.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = o.Length; o.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offs) o.Append($"{off:D10} 00000 n \n");
        o.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(o.ToString());
    }
}
