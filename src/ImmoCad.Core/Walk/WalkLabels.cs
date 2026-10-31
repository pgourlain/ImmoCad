using ImmoCad.Core.Geometry;
using ImmoCad.Core.Model;
using ImmoCad.Scene;

namespace ImmoCad.Core.Walk;

/// <summary>One room name to draw over the first-person view: where, how strongly, and what it says.</summary>
public sealed record RoomLabel(Guid SpaceId, string Text, SpaceKind Kind, double X, double Y, double Distance, double Alpha);

/// <summary>
/// Room names for the walk view. A name typed by the user is shown as it is; a generated one ("Room 3") gives way to the kind ("Bike room") when the kind says more.
/// The name floats under the ceiling at the middle of the room (a few points along a long corridor), only when it is in front of the eyes, near enough, and not behind a wall
/// (in ghost mode it shows through walls). The room the user stands in is not labelled: the line at the top already names it.
/// </summary>
public sealed class WalkLabels
{
    public const double MaxDistanceMm = 15000, FullAlphaMm = 8000, AnchorHeightMm = 2200;
    private readonly Dictionary<Guid, (int Sig, List<PointMm> Anchors)> _anchors = new();

    /// <summary>The words for a room: its own name, or its kind when the name was generated and the kind is more telling; the number is added unless the text already has it.</summary>
    public static string RoomText(Space s)
    {
        var generated = s.Name.Length > 0 && s.Name[0] == '@'; var text = Translations.Display(s.Name);
        if ((generated || text.Length == 0) && s.Kind != SpaceKind.Room) text = Translations.Enum_(s.Kind);
        else if (text.Length == 0) text = Translations.Enum_(s.Kind);
        if (!string.IsNullOrWhiteSpace(s.Number) && !text.Contains(s.Number, StringComparison.OrdinalIgnoreCase)) text += " " + s.Number;
        return text;
    }

    /// <summary>Points where the name can float: the middle of the room (moved inside it when the room is L-shaped), and a point near each end when the room is long.</summary>
    public static List<PointMm> AnchorsOf(IReadOnlyList<PointMm> outline)
    {
        var res = new List<PointMm>(); if (outline.Count < 3) return res;
        int x0 = outline.Min(p => p.X), x1 = outline.Max(p => p.X), y0 = outline.Min(p => p.Y), y1 = outline.Max(p => p.Y);
        double a2 = 0, cx = 0, cy = 0;
        for (var i = 0; i < outline.Count; i++) { var p = outline[i]; var q = outline[(i + 1) % outline.Count]; var cr = (double)p.X * q.Y - (double)q.X * p.Y; a2 += cr; cx += (p.X + q.X) * cr; cy += (p.Y + q.Y) * cr; }
        var centre = Math.Abs(a2) < 1 ? new PointMm((x0 + x1) / 2, (y0 + y1) / 2) : new PointMm((int)Math.Round(cx / (3 * a2)), (int)Math.Round(cy / (3 * a2)));
        res.Add(Inside(outline, centre, x0, y0, x1, y1));
        double w = x1 - x0, h = y1 - y0;
        if (Math.Max(w, h) > 3 * Math.Max(Math.Min(w, h), 1))
            foreach (var t in new[] { 0.15, 0.85 })
            {
                var p = w >= h ? new PointMm((int)(x0 + w * t), centre.Y) : new PointMm(centre.X, (int)(y0 + h * t));
                var inside = Inside(outline, p, x0, y0, x1, y1); if (!res.Contains(inside)) res.Add(inside);
            }
        return res;
    }

    /// <summary>The point itself when it is inside the outline, otherwise the sample of a 24 × 24 grid over the bounding box that is inside and nearest to it.</summary>
    private static PointMm Inside(IReadOnlyList<PointMm> outline, PointMm p, int x0, int y0, int x1, int y1)
    {
        if (SpaceFinder.Contains(outline, p)) return p;
        PointMm best = p; var bestD = double.MaxValue;
        for (var i = 0; i < 24; i++) for (var j = 0; j < 24; j++)
        {
            var c = new PointMm(x0 + (x1 - x0) * (2 * i + 1) / 48, y0 + (y1 - y0) * (2 * j + 1) / 48); if (!SpaceFinder.Contains(outline, c)) continue;
            var d = Math.Pow(c.X - p.X, 2) + Math.Pow(c.Y - p.Y, 2); if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    private List<PointMm> Cached(Space s)
    {
        var sig = HashCode.Combine(s.Outline.Count, s.Outline.Count > 0 ? s.Outline[0] : default, s.Outline.Count > 2 ? s.Outline[2] : default);
        if (_anchors.TryGetValue(s.Id, out var c) && c.Sig == sig) return c.Anchors;
        var a = AnchorsOf(s.Outline); _anchors[s.Id] = (sig, a); return a;
    }

    /// <summary>The main anchor of a room (the middle, inside it), cached: used by the map.</summary>
    public PointMm AnchorFor(Space s) => Cached(s) is { Count: > 0 } a ? a[0] : default;

    /// <summary>The names to draw for the current view, nearest first, at most <paramref name="max"/>. Positions are in pixels of a viewport of the given size.</summary>
    public List<RoomLabel> Place(WalkState w, WalkCamera cam, double width, double height, int max = 12)
    {
        var res = new List<RoomLabel>(); if (width < 2 || height < 2) return res;
        var level = w.Level; var anchorZ = level.AltitudeMm + Math.Min(AnchorHeightMm, level.HeightMm - 200); var aspect = width / height; var eye = new WalkPoint(w.X, w.Y); var eyeMm = w.EyeAboveFloor;
        foreach (var s in level.Spaces)
        {
            if (s.Outline.Count < 3 || SpaceFinder.Contains(s.Outline, new PointMm((int)Math.Round(w.X), (int)Math.Round(w.Y)))) continue;               // standing in it: the top line says so
            RoomLabel? best = null;
            foreach (var a in Cached(s))
            {
                var d = Math.Sqrt(Math.Pow(a.X - w.X, 2) + Math.Pow(a.Y - w.Y, 2)); if (d > MaxDistanceMm || (best != null && d >= best.Distance)) continue;
                var (nx, ny, depth) = cam.Project(a.X / 1000.0, a.Y / 1000.0, anchorZ / 1000.0, aspect);
                if (depth < 0.3 || double.IsNaN(nx) || Math.Abs(nx) > 1.05 || Math.Abs(ny) > 1.05) continue;                                                // behind, or off screen
                if (!w.Ghost && !w.Area.LineOfSight(eye, new WalkPoint(a.X, a.Y), eyeMm)) continue;                                                        // behind a wall
                var alpha = d <= FullAlphaMm ? 1 : Math.Clamp(1 - (d - FullAlphaMm) / (MaxDistanceMm - FullAlphaMm) * 0.65, 0.35, 1);
                best = new RoomLabel(s.Id, RoomText(s), s.Kind, (nx + 1) / 2 * width, (1 - ny) / 2 * height, d, alpha);
            }
            if (best != null) res.Add(best);
        }
        res.Sort((p, q) => p.Distance.CompareTo(q.Distance)); if (res.Count > max) res.RemoveRange(max, res.Count - max);
        return res;
    }
}
