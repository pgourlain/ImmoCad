using System.Globalization;
using ImmoCad.Core.Model;

namespace ImmoCad.Core.Catalog;

public enum SymbolFill { None, White, Black }

/// <summary>One drawn element of a 2D symbol in plan millimetres: an open/closed polyline, or a circle. <c>Fill</c> is set by an F (white) or B (black) marker in the path.</summary>
public sealed record SymbolFigure(IReadOnlyList<PointMm> Points, bool Closed, PointMm? Centre = null, int Radius = 0, SymbolFill Fill = SymbolFill.None);

public static class Symbol
{
    /// <summary>Evaluates the catalog entry's symbol paths for an instance (position + rotation in 90° steps or any angle) in plan coordinates.
    /// <paramref name="scale"/> grows the symbol about its origin (small valves are drawn larger when zoomed out); <paramref name="centre"/> puts the symbol's
    /// middle, not its bottom-left corner, on the origin (inline parts sit on the pipe axis).</summary>
    public static List<SymbolFigure> Figures(CatalogEntry entry, PointMm origin, int rotationDeg = 0, double scale = 1, bool centre = false)
    {
        var res = new List<SymbolFigure>(); var r = rotationDeg * Math.PI / 180; double c = Math.Cos(r), s = Math.Sin(r);
        double ox = centre ? -entry.WidthMm / 2.0 : 0, oy = centre ? -entry.DepthMm / 2.0 : 0;
        PointMm T(double x, double y) { x = (x + ox) * scale; y = (y + oy) * scale; return new((int)Math.Round(origin.X + x * c - y * s), (int)Math.Round(origin.Y + x * s + y * c)); }
        foreach (var path in entry.Symbol)
        {
            var parts = path.Split(' ', StringSplitOptions.RemoveEmptyEntries); var pts = new List<PointMm>(); var closed = false; var fill = SymbolFill.None;
            foreach (var p in parts)
            {
                if (p == "Z") { closed = true; continue; }
                if (p == "F") { fill = SymbolFill.White; continue; }
                if (p == "B") { fill = SymbolFill.Black; continue; }
                var kind = p[0]; var nums = p[1..].Split(',').Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray();
                if (kind == 'C') { res.Add(new SymbolFigure([], false, T(nums[0], nums[1]), (int)Math.Round(nums[2] * scale), fill)); continue; }
                if (kind == 'E')
                {   // ellipse cx,cy,rx,ry as a 32-gon
                    var ring = new List<PointMm>(); for (var i = 0; i < 32; i++) { var a = i * Math.PI / 16; ring.Add(T(nums[0] + nums[2] * Math.Cos(a), nums[1] + nums[3] * Math.Sin(a))); }
                    res.Add(new SymbolFigure(ring, true, Fill: fill)); continue;
                }
                pts.Add(T(nums[0], nums[1]));
            }
            if (pts.Count > 0) res.Add(new SymbolFigure(pts, closed, Fill: fill));
        }
        return res;
    }

    /// <summary>The 3D parts of an instance (boxes and cylinders) placed on the floor of its level at the instance elevation, rotated about its origin.</summary>
    public static List<(string Type, int X, int Y, int Z, int W, int D, int H, int R, int RotationDeg)> Parts(CatalogEntry entry, PointMm origin, int baseZ, int rotationDeg)
    {
        var res = new List<(string, int, int, int, int, int, int, int, int)>(); var r = rotationDeg * Math.PI / 180; double c = Math.Cos(r), s = Math.Sin(r);
        foreach (var p in entry.Shape)
        {
            // centre of the part in local coordinates, rotated; the box keeps its own w/d swapped for quarter turns
            double lx = p.Type == "box" ? p.X + p.W / 2.0 : p.X, ly = p.Type == "box" ? p.Y + p.D / 2.0 : p.Y;
            var cx = origin.X + lx * c - ly * s; var cy = origin.Y + lx * s + ly * c; var quarter = ((rotationDeg % 180) + 180) % 180 == 90;
            var (w, d) = quarter ? (p.D, p.W) : (p.W, p.D);
            if (p.Type == "box") res.Add(("box", (int)Math.Round(cx - w / 2.0), (int)Math.Round(cy - d / 2.0), baseZ + p.Z, w, d, p.H, 0, rotationDeg));
            else res.Add(("cylinder", (int)Math.Round(cx), (int)Math.Round(cy), baseZ + p.Z, 0, 0, p.H, p.R, rotationDeg));
        }
        return res;
    }

    /// <summary>Plan positions (and altitudes) of the instance's ports, for the Connect gesture.</summary>
    public static List<(Port Port, PointMm3 At)> PortPositions(CatalogEntry entry, PointMm origin, int baseZ, int rotationDeg)
    {
        var r = rotationDeg * Math.PI / 180; double c = Math.Cos(r), s = Math.Sin(r);
        return entry.Ports.Select(p => (p, new PointMm3((int)Math.Round(origin.X + p.OffsetX * c - p.OffsetY * s), (int)Math.Round(origin.Y + p.OffsetX * s + p.OffsetY * c), baseZ + p.OffsetZ))).ToList();
    }
}
