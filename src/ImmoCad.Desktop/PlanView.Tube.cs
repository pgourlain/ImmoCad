using Avalonia;
using Avalonia.Media;

namespace ImmoCad.Desktop;

/// <summary>Pipes drawn as tubes at their real diameter (outline, body, highlight), with the diameter written once the tube is wide enough to read.</summary>
public partial class PlanView
{
    private static Color Shade(Color c, double k) => Color.FromRgb((byte)Math.Clamp(c.R * k, 0, 255), (byte)Math.Clamp(c.G * k, 0, 255), (byte)Math.Clamp(c.B * k, 0, 255));
    private double TubeWidthPx(double diameterMm) => Math.Max(3, diameterMm * Scale);
    private double RiserRadiusPx(double diameterMm) => Math.Max(4, diameterMm / 2 * Scale);

    private void DrawDiameterLabel(DrawingContext ctx, Point at, double diameterMm, bool dark)
    {
        var ft = Txt($"Ø{diameterMm:0}", 10, dark ? Brushes.White : Brushes.Black);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, dark ? (byte)30 : (byte)255, dark ? (byte)32 : (byte)255, dark ? (byte)36 : (byte)255)), null, new Rect(at.X - ft.Width / 2 - 3, at.Y - ft.Height / 2 - 1, ft.Width + 6, ft.Height + 2), 3, 3);
        ctx.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    /// <summary>A straight tube between two screen points.</summary>
    private void DrawTube(DrawingContext ctx, Point a, Point b, double diameterMm, Color col, bool dark)
    {
        var w = TubeWidthPx(diameterMm);
        var dark50 = new SolidColorBrush(Shade(col, 0.5));
        ctx.DrawLine(new Pen(dark50, w + 2.4, lineCap: PenLineCap.Flat), a, b);                                                 // outline
        ctx.DrawLine(new Pen(new SolidColorBrush(col), w, lineCap: PenLineCap.Flat), a, b);                                     // body: square-cut ends
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 1)
        {   // the cut ends get their own edge
            var nx = -dy / len * (w + 2.4) / 2; var ny = dx / len * (w + 2.4) / 2; var edge = new Pen(dark50, 1.4);
            ctx.DrawLine(edge, new Point(a.X - nx, a.Y - ny), new Point(a.X + nx, a.Y + ny)); ctx.DrawLine(edge, new Point(b.X - nx, b.Y - ny), new Point(b.X + nx, b.Y + ny));
        }
        if (w >= 6 && len > 1)
        {   // light strip along the upper side, so it reads as a round tube
            var n = new Point(-dy / len, dx / len); if (n.Y > 0) n = new Point(-n.X, -n.Y); var o = new Point(n.X * w * 0.2, n.Y * w * 0.2);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), Math.Max(1.2, w * 0.22), lineCap: PenLineCap.Flat), a + o, b + o);
        }
        if (w >= 14 && len > 40) DrawDiameterLabel(ctx, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), diameterMm, dark);
    }

    /// <summary>Joint where two tubes meet (elbow, tee): a disc of the tube's diameter so the square-cut ends leave no gap.</summary>
    private void DrawJoint(DrawingContext ctx, Point c, double diameterMm, Color col)
    {
        var r = TubeWidthPx(diameterMm) / 2;
        ctx.DrawEllipse(new SolidColorBrush(col), new Pen(new SolidColorBrush(Shade(col, 0.5)), 1.2), c, r + 0.6, r + 0.6);
    }

    /// <summary>Joints for every node shared by two or more of the given segments.</summary>
    private void DrawJoints(DrawingContext ctx, IEnumerable<ImmoCad.Core.Networks.SegmentRef> segs, Func<ImmoCad.Core.Model.PointMm3, Point> toScreen)
    {
        var list = segs.ToList();
        var uses = list.SelectMany(s => new[] { (s.Network.Id, s.Segment.A), (s.Network.Id, s.Segment.B) }).GroupBy(x => x).Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet();
        foreach (var s in list)
        {
            var c = ImmoCad.Core.Networks.NetworkQueries.ColorOf(s.Network.Type); var col = Color.FromRgb(c.R, c.G, c.B);
            if (uses.Contains((s.Network.Id, s.Segment.A))) DrawJoint(ctx, toScreen(s.A), s.Segment.DiameterMm, col);
            if (uses.Contains((s.Network.Id, s.Segment.B))) DrawJoint(ctx, toScreen(s.B), s.Segment.DiameterMm, col);
        }
    }

    /// <summary>A vertical tube seen from above: a ring with its bore.</summary>
    private void DrawRiserTube(DrawingContext ctx, Point c, double diameterMm, Color col, bool dark)
    {
        var r = RiserRadiusPx(diameterMm);
        ctx.DrawEllipse(new SolidColorBrush(col), new Pen(new SolidColorBrush(Shade(col, 0.5)), 1.4), c, r, r);
        if (r >= 6)
        {
            ctx.DrawEllipse(new SolidColorBrush(Shade(col, 0.55)), null, c, r * 0.58, r * 0.58);                                // bore
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), null, c + new Point(-r * 0.3, -r * 0.3), r * 0.22, r * 0.22);
        }
        if (r >= 11) DrawDiameterLabel(ctx, c + new Point(0, r + 9), diameterMm, dark);
    }
}
