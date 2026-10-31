using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ImmoCad.Core.Catalog;
using ImmoCad.Core.Model;
using ImmoCad.Core.Networks;

namespace ImmoCad.Desktop;

/// <summary>Draws a catalog symbol the same way in the palette and on the plan: black stroke, white or black fills, pipe cut by the symbol.</summary>
public static class SymbolArt
{
    /// <summary>Draws the figures of <paramref name="entry"/> mapped by <paramref name="toScreen"/>; the first closed figure of an unmarked symbol is white (old catalog entries).</summary>
    public static void Draw(DrawingContext ctx, CatalogEntry entry, PointMm origin, int rotationDeg, double scale, bool centre, Func<PointMm, Point> toScreen, double screenScale, IBrush stroke, double thickness, IBrush white)
    {
        var pen = new Pen(stroke, thickness, lineJoin: PenLineJoin.Round); var figs = Symbol.Figures(entry, origin, rotationDeg, scale, centre);
        foreach (var fig in figs)
        {
            var brush = fig.Fill switch { SymbolFill.White => white, SymbolFill.Black => stroke, _ => fig.Closed && ReferenceEquals(fig, figs[0]) ? white : null };
            if (fig.Centre is { } c) { ctx.DrawEllipse(brush, pen, toScreen(c), fig.Radius * screenScale, fig.Radius * screenScale); continue; }
            var g = new StreamGeometry(); using (var cx = g.Open()) { cx.BeginFigure(toScreen(fig.Points[0]), fig.Closed); foreach (var p in fig.Points.Skip(1)) cx.LineTo(toScreen(p)); cx.EndFigure(fig.Closed); }
            ctx.DrawGeometry(brush, pen, g);
        }
    }

    /// <summary>Network the part usually sits on, to colour the pipe stub behind it in the palette.</summary>
    public static NetworkType PipeOf(CatalogEntry e) => PlacementRules.HeatingOnly.Contains(e.Id) ? NetworkType.HeatingSupply : NetworkType.ColdWater;
}

/// <summary>The palette icon of a catalog object: its own symbol (with a pipe stub for inline parts and its short code underneath), identical to the plan.</summary>
public sealed class SymbolIcon : Control
{
    public static readonly StyledProperty<string?> CatalogIdProperty = AvaloniaProperty.Register<SymbolIcon, string?>(nameof(CatalogId));
    public string? CatalogId { get => GetValue(CatalogIdProperty); set => SetValue(CatalogIdProperty, value); }
    static SymbolIcon() { AffectsRender<SymbolIcon>(CatalogIdProperty); }

    public override void Render(DrawingContext ctx)
    {
        if (CatalogId is not { } id || EquipmentCatalog.Find(id) is not { } e) return;
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark; IBrush ink = dark ? Brushes.Gainsboro : Brushes.Black; IBrush white = dark ? new SolidColorBrush(Color.FromRgb(0x2b, 0x2b, 0x2b)) : Brushes.White;
        var code = e.Code; var labelH = code.Length > 0 ? 12.0 : 0; double w = Bounds.Width, h = Bounds.Height - labelH;
        double sw = e.WidthMm, sh = e.DepthMm; if (e.Inline) sh = Math.Max(sh, sw);   // stems rise above the pipe axis
        var k = Math.Min((w - 6) / sw, (h - 6) / sh); var x0 = (w - sw * k) / 2; var y0 = (h + sh * k) / 2;   // plan Y is up
        Point P(PointMm p) => new(x0 + p.X * k, y0 - p.Y * k);
        if (e.Inline)
        {
            var c = NetworkQueries.ColorOf(SymbolArt.PipeOf(e)); var yy = y0 - e.DepthMm / 2.0 * k;
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), 4), new Point(0, yy), new Point(w, yy));
        }
        SymbolArt.Draw(ctx, e, new PointMm(0, 0), 0, 1, false, P, k, ink, 1.3, white);
        if (code.Length > 0)
        {
            var ft = new FormattedText(code, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold), 9.5, ink);
            ctx.DrawText(ft, new Point((w - ft.Width) / 2, h + 1));
        }
    }
}
