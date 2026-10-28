namespace ImmeubleCad.Scene;

/// <summary>Orbit camera. Angles in radians; target in metres. Pure math, shared by all renderers.</summary>
public sealed class OrbitCamera
{
    public double Yaw { get; set; } = Math.PI / 4;
    public double Pitch { get; set; } = Math.PI / 5;
    public double Distance { get; set; } = 15;
    public (double X, double Y, double Z) Target { get; set; }

    public void Orbit(double dYaw, double dPitch) { Yaw += dYaw; Pitch = Math.Clamp(Pitch + dPitch, -Math.PI / 2 + 0.01, Math.PI / 2 - 0.01); }
    public void Zoom(double factor) => Distance = Math.Clamp(Distance * factor, 0.5, 500);

    /// <summary>Isometric-ish preset, top-down preset (pitch ~90°) matches plan framing.</summary>
    public void Top() { Pitch = Math.PI / 2 - 0.01; Yaw = 0; }
    public void Iso() { Pitch = Math.PI / 5; Yaw = Math.PI / 4; }

    /// <summary>Projects a world point (metres) to normalized screen coordinates (x right, y up, ~[-1,1] at Distance); depth returned for sorting.</summary>
    public (double X, double Y, double Depth) Project(double x, double y, double z, double aspect = 1)
    {
        double dx = x - Target.X, dy = y - Target.Y, dz = z - Target.Z;
        var (cy, sy) = (Math.Cos(Yaw), Math.Sin(Yaw)); var (cp, sp) = (Math.Cos(Pitch), Math.Sin(Pitch));
        // rotate around Z by -yaw, then tilt
        var rx = dx * cy + dy * sy; var ry = -dx * sy + dy * cy;
        var sxn = rx; var syn = dz * cp + ry * sp; var depth = ry * cp - dz * sp;
        var scale = 2.0 / Distance;
        return (sxn * scale / aspect, syn * scale, depth);
    }
}
