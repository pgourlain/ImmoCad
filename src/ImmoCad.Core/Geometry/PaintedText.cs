using ImmoCad.Core.Model;

namespace ImmoCad.Core.Geometry;

/// <summary>
/// A name painted on the floor, like the numbers of a parking bay: letters drawn with strokes (a simple stencil font, capitals, digits and a few signs) turned into flat white shapes.
/// Pure geometry, no font: the 3D view and the walk draw the shapes like any other floor marking.
/// </summary>
public static class PaintedText
{
    private const double Cell = 4;            // advance of one character, in units (3 wide + 1 gap); a character is 5 units high
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['0'] = "0,0 3,0 3,5 0,5 0,0", ['1'] = "0.5,1 1.5,0 1.5,5;0.5,5 2.5,5", ['2'] = "0,0 3,0 3,2.5 0,2.5 0,5 3,5", ['3'] = "0,0 3,0 3,5 0,5;0.8,2.5 3,2.5",
        ['4'] = "0,0 0,2.5 3,2.5;3,0 3,5", ['5'] = "3,0 0,0 0,2.5 3,2.5 3,5 0,5", ['6'] = "3,0 0,0 0,5 3,5 3,2.5 0,2.5", ['7'] = "0,0 3,0 1.2,5",
        ['8'] = "0,0 3,0 3,5 0,5 0,0;0,2.5 3,2.5", ['9'] = "3,2.5 0,2.5 0,0 3,0 3,5 0,5",
        ['A'] = "0,5 0,1.5 1.5,0 3,1.5 3,5;0,3 3,3", ['B'] = "0,0 0,5 2.4,5 3,4 3,3 2.4,2.5 0,2.5;2.4,2.5 3,2 3,1 2.4,0 0,0", ['C'] = "3,0 0,0 0,5 3,5",
        ['D'] = "0,0 0,5 2,5 3,4 3,1 2,0 0,0", ['E'] = "3,0 0,0 0,5 3,5;0,2.5 2.4,2.5", ['F'] = "3,0 0,0 0,5;0,2.5 2.4,2.5", ['G'] = "3,0 0,0 0,5 3,5 3,2.8 1.8,2.8",
        ['H'] = "0,0 0,5;3,0 3,5;0,2.5 3,2.5", ['I'] = "0.5,0 2.5,0;1.5,0 1.5,5;0.5,5 2.5,5", ['J'] = "3,0 3,5 0,5 0,3.8", ['K'] = "0,0 0,5;3,0 0,3;1,2.2 3,5",
        ['L'] = "0,0 0,5 3,5", ['M'] = "0,5 0,0 1.5,2.5 3,0 3,5", ['N'] = "0,5 0,0 3,5 3,0", ['O'] = "0,0 3,0 3,5 0,5 0,0", ['P'] = "0,5 0,0 3,0 3,2.5 0,2.5",
        ['Q'] = "0,0 3,0 3,5 0,5 0,0;2,3.8 3,5.4", ['R'] = "0,5 0,0 3,0 3,2.5 0,2.5;1.5,2.5 3,5", ['S'] = "3,0 0,0 0,2.5 3,2.5 3,5 0,5", ['T'] = "0,0 3,0;1.5,0 1.5,5",
        ['U'] = "0,0 0,5 3,5 3,0", ['V'] = "0,0 1.5,5 3,0", ['W'] = "0,0 0.75,5 1.5,2.5 2.25,5 3,0", ['X'] = "0,0 3,5;3,0 0,5", ['Y'] = "0,0 1.5,2.5 3,0;1.5,2.5 1.5,5",
        ['Z'] = "0,0 3,0 0,5 3,5", ['-'] = "0.5,2.5 2.5,2.5", ['.'] = "1.2,4.4 1.8,4.4 1.8,5 1.2,5 1.2,4.4", ['/'] = "3,0 0,5", ['\''] = "1.5,0 1.5,1.5",
    };
    private static readonly Dictionary<char, List<List<(double X, double Y)>>> Parsed = Glyphs.ToDictionary(g => g.Key, g => g.Value.Split(';')
        .Select(s => s.Split(' ').Select(p => { var c = p.Split(','); return (double.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture)); }).ToList()).ToList());

    /// <summary>Width of the text in character heights (a character is 5 units high).</summary>
    public static double WidthInHeights(string text) => Math.Max(0, text.Length * Cell - 1) / 5.0;

    /// <summary>
    /// The shapes of the text centred on <paramref name="centre"/>, <paramref name="heightMm"/> high. <paramref name="up"/> is the direction of the top of the letters (unit vector);
    /// the text reads along the direction turned a quarter turn clockwise from it.
    /// </summary>
    public static List<List<PointMm>> Shapes(string text, PointMm centre, double heightMm, (double X, double Y) up)
    {
        var res = new List<List<PointMm>>(); if (text.Length == 0 || heightMm < 10) return res;
        var len = Math.Sqrt(up.X * up.X + up.Y * up.Y); if (len < 1e-9) return res; double ux = up.X / len, uy = up.Y / len, bx = uy, by = -ux;
        double unit = heightMm / 5, half = unit * 0.28, total = (text.Length * Cell - 1) * unit;                                  // half the stroke width
        (double X, double Y) World(double gx, double gy) { var s = gx * unit - total / 2; var t = -(gy - 2.5) * unit; return (centre.X + bx * s + ux * t, centre.Y + by * s + uy * t); }
        for (var i = 0; i < text.Length; i++)
        {
            if (!Parsed.TryGetValue(char.ToUpperInvariant(text[i]), out var strokes)) continue;                                  // unknown character or a space: only the advance
            foreach (var stroke in strokes)
                for (var k = 1; k < stroke.Count; k++)
                {
                    var a = World(i * Cell + stroke[k - 1].X, stroke[k - 1].Y); var b = World(i * Cell + stroke[k].X, stroke[k].Y);
                    double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy); if (l < 1e-6) continue; double nx = -dy / l * half, ny = dx / l * half, ex = dx / l * half, ey = dy / l * half;
                    PointMm P(double x, double y) => new((int)Math.Round(x), (int)Math.Round(y));
                    res.Add([P(a.X - ex + nx, a.Y - ey + ny), P(b.X + ex + nx, b.Y + ey + ny), P(b.X + ex - nx, b.Y + ey - ny), P(a.X - ex - nx, a.Y - ey - ny)]);   // a segment with square ends: the joints overlap
                }
        }
        return res;
    }

    /// <summary>
    /// The name of a space painted in it: across the narrow side of its tightest rectangle, tops of the letters towards the long side and the top of the plan,
    /// sized to fit (at most 450 mm high). Empty when the space is degenerate.
    /// </summary>
    public static List<List<PointMm>> ForSpace(IReadOnlyList<PointMm> outline, string text)
    {
        if (outline.Count < 3 || text.Length == 0) return [];
        double bestArea = double.MaxValue, depth = 0, width = 0; (double X, double Y) along = (0, 1);                              // the tightest rectangle: its long side and its short side
        for (var i = 0; i < outline.Count; i++)
        {
            var a = outline[i]; var b = outline[(i + 1) % outline.Count]; double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy); if (l < 1) continue;
            double ex = dx / l, ey = dy / l, fx = -ey, fy = ex, e0 = double.MaxValue, e1 = double.MinValue, f0 = double.MaxValue, f1 = double.MinValue;
            foreach (var p in outline) { var e = p.X * ex + p.Y * ey; var f = p.X * fx + p.Y * fy; e0 = Math.Min(e0, e); e1 = Math.Max(e1, e); f0 = Math.Min(f0, f); f1 = Math.Max(f1, f); }
            var area = (e1 - e0) * (f1 - f0); if (area >= bestArea) continue;
            bestArea = area; var longE = e1 - e0 >= f1 - f0; along = longE ? (ex, ey) : (fx, fy); depth = Math.Max(e1 - e0, f1 - f0); width = Math.Min(e1 - e0, f1 - f0);
        }
        if (bestArea == double.MaxValue) return [];
        if (along.Y < -1e-6 || Math.Abs(along.Y) <= 1e-6 && along.X > 0) along = (-along.X, -along.Y);                              // letters read from the bottom of the plan up, or from the right
        var reading = (X: along.Y, Y: -along.X);
        var h = Math.Min(450.0, depth * 0.4); h = Math.Min(h, width * 0.78 * 5 / Math.Max(1, text.Length * Cell - 1)); h = Math.Max(h, 30);
        return Shapes(text, Centre(outline, along, reading), h, along);
    }

    /// <summary>The middle of the outline's extent along the two axes.</summary>
    private static PointMm Centre(IReadOnlyList<PointMm> outline, (double X, double Y) u, (double X, double Y) v)
    {
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
        foreach (var p in outline) { var a = p.X * u.X + p.Y * u.Y; var b = p.X * v.X + p.Y * v.Y; u0 = Math.Min(u0, a); u1 = Math.Max(u1, a); v0 = Math.Min(v0, b); v1 = Math.Max(v1, b); }
        double mu = (u0 + u1) / 2, mv = (v0 + v1) / 2; return new PointMm((int)Math.Round(u.X * mu + v.X * mv), (int)Math.Round(u.Y * mu + v.Y * mv));
    }
}
