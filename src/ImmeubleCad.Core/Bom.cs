using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core;

public sealed record PipeLine(string Network, int DiameterMm, double LengthM);

public static class Bom
{
    public static IEnumerable<PipeLine> Pipes(Building b) =>
        b.Networks.SelectMany(n => n.Segments.Select(sg =>
        {
            var a = n.Nodes.First(x => x.Id == sg.A).Position; var c = n.Nodes.First(x => x.Id == sg.B).Position;
            double dx = a.X - c.X, dy = a.Y - c.Y, dz = a.Z - c.Z;
            return (n.Type.ToString(), sg.DiameterMm, Len: Math.Sqrt(dx * dx + dy * dy + dz * dz) / 1000);
        })).GroupBy(x => (x.Item1, x.DiameterMm)).Select(g => new PipeLine(g.Key.Item1, g.Key.DiameterMm, g.Sum(x => x.Len)));

    public static string ToCsv(Building b) =>
        "network,diameter_mm,length_m\n" + string.Join("\n", Pipes(b).Select(p => $"{p.Network},{p.DiameterMm},{p.LengthM:0.###}"));
}
