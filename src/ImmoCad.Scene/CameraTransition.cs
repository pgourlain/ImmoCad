namespace ImmoCad.Scene;

/// <summary>Plan ↔ 3D camera transition: ortho top view matching the plan framing, tilting to the last 3D angle.</summary>
public readonly record struct CameraPose(double Yaw, double Pitch, double Distance, double TargetX, double TargetY, double TargetZ, double OrthoBlend)
{
    public static CameraPose Lerp(CameraPose a, CameraPose b, double t) => new(
        a.Yaw + (b.Yaw - a.Yaw) * t, a.Pitch + (b.Pitch - a.Pitch) * t, a.Distance + (b.Distance - a.Distance) * t,
        a.TargetX + (b.TargetX - a.TargetX) * t, a.TargetY + (b.TargetY - a.TargetY) * t, a.TargetZ + (b.TargetZ - a.TargetZ) * t, a.OrthoBlend + (b.OrthoBlend - a.OrthoBlend) * t);
}

public static class CameraTransition
{
    public const double Duration = 0.5;
    public static double EaseInOut(double t) { t = Math.Clamp(t, 0, 1); return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2; }

    /// <summary>
    /// The pose that looks straight down at the plan's look-at point (mm) at the plan's scale (px per mm) in a viewport of the given height:
    /// an orthographic top view covering exactly what the 2D view shows.
    /// </summary>
    public static CameraPose TopFromPlan(double lookAtXmm, double lookAtYmm, double planScalePxPerMm, double viewportHeightPx, double altitudeM = 0) =>
        new(0, Math.PI / 2 - 0.0001, viewportHeightPx / planScalePxPerMm / 1000.0, lookAtXmm / 1000.0, lookAtYmm / 1000.0, altitudeM, 1);

    public static (double X, double Y, double Scale) PlanFromTop(CameraPose p, double viewportHeightPx) => (p.TargetX * 1000.0, p.TargetY * 1000.0, viewportHeightPx / (p.Distance * 1000.0));

    public static CameraPose At(CameraPose from, CameraPose to, double elapsedSeconds, bool animate = true) => animate ? CameraPose.Lerp(from, to, EaseInOut(elapsedSeconds / Duration)) : to;
}
