namespace ImmoCad.Scene;

/// <summary>
/// First-person camera. Eye in metres, angles in radians: yaw 0 looks along +Y and grows counter-clockwise (turning left), pitch grows upwards.
/// Same axes as <see cref="OrbitCamera"/> (depth is the distance along the view direction), but with a perspective division and a near plane.
/// </summary>
public sealed class WalkCamera
{
    public const double MaxPitch = 85 * Math.PI / 180;
    public (double X, double Y, double Z) Eye { get; set; }
    public double Yaw { get; set; }
    public double Pitch { get; private set; }
    /// <summary>Vertical field of view in degrees (the wheel moves it between 30 and 80).</summary>
    public double FovDegrees { get; private set; } = 70;
    public double Near { get; set; } = 0.05;
    public double Far { get; set; } = 300;

    public double TanHalfFov => Math.Tan(FovDegrees * Math.PI / 360);
    public void SetPitch(double pitch) => Pitch = Math.Clamp(pitch, -MaxPitch, MaxPitch);
    public void SetFov(double degrees) => FovDegrees = Math.Clamp(degrees, 30, 80);
    public void Turn(double dYaw, double dPitch) { Yaw += dYaw; SetPitch(Pitch + dPitch); }

    public (double X, double Y, double Z) Forward => (-Math.Sin(Yaw) * Math.Cos(Pitch), Math.Cos(Yaw) * Math.Cos(Pitch), Math.Sin(Pitch));
    public (double X, double Y, double Z) Right => (Math.Cos(Yaw), Math.Sin(Yaw), 0);
    public (double X, double Y, double Z) Up => (Math.Sin(Yaw) * Math.Sin(Pitch), -Math.Cos(Yaw) * Math.Sin(Pitch), Math.Cos(Pitch));

    /// <summary>
    /// Projects a world point (metres) to normalized device coordinates (x right, y up, both in [-1, 1] on screen) plus the depth along the view direction
    /// (negative behind the eye: such a point is not visible).
    /// </summary>
    public (double X, double Y, double Depth) Project(double x, double y, double z, double aspect = 1)
    {
        double dx = x - Eye.X, dy = y - Eye.Y, dz = z - Eye.Z; var f = Forward; var r = Right; var u = Up;
        var depth = dx * f.X + dy * f.Y + dz * f.Z;
        var k = 1 / TanHalfFov;
        if (depth < 1e-9) return (double.NaN, double.NaN, depth);
        return ((dx * r.X + dy * r.Y) * k / aspect / depth, (dx * u.X + dy * u.Y + dz * u.Z) * k / depth, depth);
    }

    /// <summary>The ray through a normalized screen point (as returned by <see cref="Project"/>): origin at the eye, unit direction.</summary>
    public ((double X, double Y, double Z) Origin, (double X, double Y, double Z) Dir) Unproject(double sx, double sy, double aspect = 1)
    {
        var f = Forward; var r = Right; var u = Up; var t = TanHalfFov;
        double a = sx * aspect * t, b = sy * t;
        double dx = f.X + r.X * a + u.X * b, dy = f.Y + r.Y * a + u.Y * b, dz = f.Z + r.Z * a + u.Z * b;
        var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (Eye, (dx / len, dy / len, dz / len));
    }
}
