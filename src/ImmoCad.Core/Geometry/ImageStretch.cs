namespace ImmoCad.Core.Geometry;

public enum StretchHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Move, Rotate, Shear }

/// <summary>
/// Manual stretch of the background picture, like a picture in an image editor: 8 handles + move + rotate.
/// Works on the picture's 2×3 transform (pixels → mm). Corner handles scale both axes (Shift keeps the ratio), side handles one axis only
/// (length or width), always about the opposite corner/side so it stays put. The shear handle (right edge, just under the top-right corner) slides the
/// picture sideways in proportion to the height above its bottom edge: the bottom stays put, the top follows the mouse (a trapezoid-like correction, no rotation).
/// </summary>
public static class ImageStretch
{
    /// <summary>Where the shear handle sits on the right edge, as a fraction of the picture height from the top.</summary>
    public const double ShearAt = 0.12;

    public static Affine FromArray(double[]? m) => m is { Length: 6 } ? new(m[0], m[1], m[2], m[3], m[4], m[5]) : Affine.Identity;

    /// <summary>Corners of the picture in mm: TL, TR, BR, BL (image Y goes down, plan Y up, handled by the transform itself).</summary>
    public static (double X, double Y)[] Corners(Affine t, int widthPx, int heightPx) =>
        [t.Apply(0, 0), t.Apply(widthPx, 0), t.Apply(widthPx, heightPx), t.Apply(0, heightPx)];

    public static (double X, double Y) HandlePoint(Affine t, int w, int h, StretchHandle hd) => hd switch
    {
        StretchHandle.TopLeft => t.Apply(0, 0), StretchHandle.Top => t.Apply(w / 2.0, 0), StretchHandle.TopRight => t.Apply(w, 0), StretchHandle.Right => t.Apply(w, h / 2.0),
        StretchHandle.BottomRight => t.Apply(w, h), StretchHandle.Bottom => t.Apply(w / 2.0, h), StretchHandle.BottomLeft => t.Apply(0, h), StretchHandle.Left => t.Apply(0, h / 2.0),
        StretchHandle.Rotate => t.Apply(w / 2.0, -Math.Max(h, w) * 0.06), StretchHandle.Shear => t.Apply(w, h * ShearAt), _ => t.Apply(w / 2.0, h / 2.0),
    };

    /// <summary>The transform after dragging <paramref name="handle"/> from <paramref name="from"/> to <paramref name="to"/> (mm).</summary>
    public static Affine Drag(Affine t, int w, int h, StretchHandle handle, (double X, double Y) from, (double X, double Y) to, bool keepRatio = false)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        if (handle == StretchHandle.Move) return t with { Tx = t.Tx + dx, Ty = t.Ty + dy };
        var centre = t.Apply(w / 2.0, h / 2.0);
        if (handle == StretchHandle.Rotate)
        {
            var a0 = Math.Atan2(from.Y - centre.Y, from.X - centre.X); var a1 = Math.Atan2(to.Y - centre.Y, to.X - centre.X); return RotateAbout(t, centre, a1 - a0);
        }
        if (handle == StretchHandle.Shear)
        {
            var d0 = t.A * t.D - t.B * t.C; if (Math.Abs(d0) < 1e-12) return t;
            var k = (dx * t.D - dy * t.B) / d0 / (h * (1 - ShearAt));                       // pixels of sideways slide per pixel above the bottom row
            return new(t.A, t.B - k * t.A, t.Tx + t.A * k * h, t.C, t.D - k * t.C, t.Ty + t.C * k * h);
        }
        // local axes of the picture in mm (columns of the linear part)
        double ux = t.A, uy = t.C, vx = t.B, vy = t.D;                         // image x axis, image y axis
        var (px, py) = (handle switch { StretchHandle.TopLeft or StretchHandle.Left or StretchHandle.BottomLeft => w, StretchHandle.TopRight or StretchHandle.Right or StretchHandle.BottomRight => 0, _ => w / 2.0 },
                        handle switch { StretchHandle.TopLeft or StretchHandle.Top or StretchHandle.TopRight => h, StretchHandle.BottomLeft or StretchHandle.Bottom or StretchHandle.BottomRight => 0, _ => h / 2.0 });
        var pivot = t.Apply(px, py); var grabbed = HandlePoint(t, w, h, handle);
        var movesX = handle is StretchHandle.Left or StretchHandle.Right or StretchHandle.TopLeft or StretchHandle.TopRight or StretchHandle.BottomLeft or StretchHandle.BottomRight;
        var movesY = handle is StretchHandle.Top or StretchHandle.Bottom or StretchHandle.TopLeft or StretchHandle.TopRight or StretchHandle.BottomLeft or StretchHandle.BottomRight;
        // express pivot→grabbed and pivot→target in the picture's own axes, the ratio gives the scale per axis
        var det = ux * vy - vx * uy; if (Math.Abs(det) < 1e-12) return t;
        (double, double) Local(double X, double Y) { var rx = X - pivot.X; var ry = Y - pivot.Y; return ((rx * vy - ry * vx) / det, (ux * ry - uy * rx) / det); }
        var (gl0, gl1) = Local(grabbed.X, grabbed.Y); var (tl0, tl1) = Local(grabbed.X + dx, grabbed.Y + dy);
        double sx = movesX && Math.Abs(gl0) > 1e-9 ? tl0 / gl0 : 1, sy = movesY && Math.Abs(gl1) > 1e-9 ? tl1 / gl1 : 1;
        if (keepRatio && movesX && movesY) { var s = Math.Abs(sx) > Math.Abs(sy) ? sx : sy; sx = sy = s; }
        sx = Math.Clamp(sx, 0.05, 20); sy = Math.Clamp(sy, 0.05, 20);
        // new linear part: scale along the picture's own axes (image axis x scaled by sx, y by sy); translation keeps the pivot fixed
        var n = new Affine(t.A * sx, t.B * sy, 0, t.C * sx, t.D * sy, 0);
        var moved = n.Apply(px, py);
        return n with { Tx = pivot.X - (moved.X), Ty = pivot.Y - (moved.Y) };
    }

    public static Affine RotateAbout(Affine t, (double X, double Y) c, double radians)
    {
        double cs = Math.Cos(radians), sn = Math.Sin(radians);
        return new(cs * t.A - sn * t.C, cs * t.B - sn * t.D, cs * (t.Tx - c.X) - sn * (t.Ty - c.Y) + c.X, sn * t.A + cs * t.C, sn * t.B + cs * t.D, sn * (t.Tx - c.X) + cs * (t.Ty - c.Y) + c.Y);
    }

    /// <summary>Nudge by 1 mm (Shift: 10 mm).</summary>
    public static Affine Nudge(Affine t, int dxMm, int dyMm) => t with { Tx = t.Tx + dxMm, Ty = t.Ty + dyMm };

    /// <summary>Numeric fields of the Properties panel: scale X/Y (mm per pixel), rotation (degrees), offset (mm).</summary>
    public static (double ScaleX, double ScaleY, double RotationDeg, double OffsetX, double OffsetY) Decompose(Affine t)
    {
        var sx = Math.Sqrt(t.A * t.A + t.C * t.C); var sy = Math.Sqrt(t.B * t.B + t.D * t.D); var rot = Math.Atan2(t.C, t.A) * 180 / Math.PI;
        return (sx, sy, rot, t.Tx, t.Ty);
    }
    public static Affine Compose(double sx, double sy, double rotationDeg, double ox, double oy)
    {
        var r = rotationDeg * Math.PI / 180; double c = Math.Cos(r), s = Math.Sin(r);
        // R(θ)·diag(sx, −sy): the picture's y axis points down while the plan's points up
        return new(c * sx, s * sy, ox, s * sx, -c * sy, oy);
    }
}
