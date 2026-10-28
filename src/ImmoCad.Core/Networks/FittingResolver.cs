using ImmoCad.Core.Model;

namespace ImmoCad.Core.Networks;

public enum FittingKind { EndCap, Joint, Elbow90, Tee, Cross, Reducer, SlabCrossing, Component, Other }
public sealed record Fitting(Guid NodeId, PointMm3 Position, FittingKind Kind);

/// <summary>Derives fittings from the network graph; never stored in the model.</summary>
public static class FittingResolver
{
    public static IReadOnlyList<Fitting> Resolve(Network net, IReadOnlyList<Level>? levels = null)
    {
        var res = new List<Fitting>();
        foreach (var n in net.Nodes)
        {
            var segs = net.Segments.Where(s => s.A == n.Id || s.B == n.Id).ToList();
            if (segs.Count == 0) continue;
            var dirs = segs.Select(s =>
            {
                var o = net.Nodes.First(x => x.Id == (s.A == n.Id ? s.B : s.A)).Position;
                return (Math.Sign(o.X - n.Position.X), Math.Sign(o.Y - n.Position.Y), Math.Sign(o.Z - n.Position.Z));
            }).ToList();
            FittingKind k;
            if (n.Component != null) k = FittingKind.Component;
            else if (segs.Count == 1) k = FittingKind.EndCap;
            else if (segs.Count == 2)
            {
                var (a, b) = (dirs[0], dirs[1]);
                var straight = a.Item1 == -b.Item1 && a.Item2 == -b.Item2 && a.Item3 == -b.Item3;
                if (!straight) k = FittingKind.Elbow90;
                else if (segs[0].DiameterMm != segs[1].DiameterMm) k = FittingKind.Reducer;
                else if (levels != null && n.Position.Z != 0 && levels.Any(l => l.AltitudeMm == n.Position.Z) && a.Item3 != 0) k = FittingKind.SlabCrossing;
                else k = FittingKind.Joint;
            }
            else k = segs.Count == 3 ? FittingKind.Tee : segs.Count == 4 ? FittingKind.Cross : FittingKind.Other;
            res.Add(new Fitting(n.Id, n.Position, k));
        }
        return res;
    }

    public static IReadOnlyList<string> Validate(Network net)
    {
        var w = new List<string>();
        foreach (var f in Resolve(net).Where(f => f.Kind == FittingKind.EndCap))
            w.Add($"Unconnected end at {f.Position}");
        foreach (var s in net.Segments)
        {
            var a = net.Nodes.First(n => n.Id == s.A).Position; var b = net.Nodes.First(n => n.Id == s.B).Position;
            var axes = (a.X != b.X ? 1 : 0) + (a.Y != b.Y ? 1 : 0) + (a.Z != b.Z ? 1 : 0);
            if (axes != 1) w.Add($"Segment {s.Id} not axis-aligned");
        }
        return w;
    }
}
