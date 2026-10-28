using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using ImmeubleCad.Core;
using ImmeubleCad.Scene;

namespace ImmeubleCad.Desktop;

/// <summary>
/// Everything about the 3D view that is not drawing: camera, input (orbit / pan / zoom / pick), scene cache, render options and the
/// animated plan ↔ 3D transition. Shared by the software viewport and the OpenGL one.
/// </summary>
public sealed class ViewportController
{
    public OrbitCamera Camera { get; } = new();
    public CameraPose LastPose3D { get; private set; } = new(Math.PI / 4, Math.PI / 5, 18, 0, 0, 1.5, 0);
    public List<SceneObject> Objects { get; private set; } = new();
    public bool Wireframe { get; set; }
    public bool XRay { get; set; }
    public LevelFilter Filter { get; set; } = LevelFilter.WholeBuilding;
    public bool CutAboveCurrent { get; set; } = true;
    public bool Animate { get; set; } = true;
    public double OrthoBlend { get; private set; }            // 1 = orthographic top view (plan framing), 0 = perspective
    public Action? Invalidate { get; set; }
    public Action<Guid, SceneKind>? Picked { get; set; }

    private Point? _last; private bool _pan; private Point _down;
    private DispatcherTimer? _timer; private DateTime _t0; private CameraPose _from, _to; private Action? _done;
    public bool IsAnimating => _timer != null;

    public void Rebuild(MainViewModel? vm)
    {
        if (vm == null || vm.Building.Levels.Count == 0) { Objects = new(); return; }
        Objects = SceneBuilder.Build(SceneMapper.Map(vm.Building), new SceneOptions(vm.CurrentLevel?.Id, Filter, CutAboveCurrent, XRay, true, vm.SelectedWalls));
        var pts = Objects.Where(o => o.Kind == SceneKind.Wall).ToList();
        if (pts.Count > 0)
        {
            var min = (X: pts.Min(o => o.Mesh.Min().X), Y: pts.Min(o => o.Mesh.Min().Y)); var max = (X: pts.Max(o => o.Mesh.Max().X), Y: pts.Max(o => o.Mesh.Max().Y));
            Camera.Target = ((min.X + max.X) / 2, (min.Y + max.Y) / 2, (vm.CurrentLevel?.AltitudeMm ?? 0) / 1000.0 + 1.35);   // level bar moves the camera target vertically
            if (!_fitted) { Camera.Distance = Math.Max(max.X - min.X, max.Y - min.Y) * 1.8 + 4; _fitted = true; }
        }
    }
    private bool _fitted;
    public void FitAll() { _fitted = false; }

    // ---- input ----
    public void Down(Point p, bool rightOrMiddle) { _last = p; _down = p; _pan = rightOrMiddle; }
    public void Up(Point p, MainViewModel? vm, double width, double height)
    {
        _last = null;
        if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) < 4 && !_pan) Pick(p, width, height);
    }
    public void Move(Point p)
    {
        if (_last is not { } l || IsAnimating) return;
        if (_pan) { var k = Camera.Distance * 0.0012; double dx = (p.X - l.X) * k, dy = (p.Y - l.Y) * k; var (cy, sy) = (Math.Cos(Camera.Yaw), Math.Sin(Camera.Yaw)); Camera.Target = (Camera.Target.X - dx * cy + dy * Math.Sin(Camera.Pitch) * sy, Camera.Target.Y + dx * sy + dy * Math.Sin(Camera.Pitch) * cy, Camera.Target.Z); }
        else Camera.Orbit(-(p.X - l.X) * 0.01, (p.Y - l.Y) * 0.01);
        _last = p; Invalidate?.Invoke();
    }
    public void Wheel(double dy) { if (IsAnimating) return; Camera.Zoom(dy > 0 ? 0.9 : 1.1); Invalidate?.Invoke(); }
    public void Preset(string name)
    {
        switch (name) { case "top": Camera.Top(); break; case "front": Camera.Yaw = 0; Camera.Pitch = 0.02; break; case "side": Camera.Yaw = Math.PI / 2; Camera.Pitch = 0.02; break; default: Camera.Iso(); break; }
        Invalidate?.Invoke();
    }

    /// <summary>Ray through a screen point → nearest scene object → id (wall, pipe, fitting, equipment).</summary>
    public void Pick(Point p, double width, double height)
    {
        var half = Math.Min(width, height) / 2; double sx = (p.X - width / 2) / half, sy = -(p.Y - height / 2) / half;
        var (o, d) = Camera.Unproject(sx, sy, 1);
        if (SceneBuilder.Pick(Objects, ((float)o.X, (float)o.Y, (float)o.Z), ((float)d.X, (float)d.Y, (float)d.Z)) is { } hit) Picked?.Invoke(hit.Id, hit.Kind);
    }

    // ---- plan ↔ 3D transitions ----
    /// <summary>From the plan's framing (look-at in mm, px per mm, viewport height) to the last 3D angle.</summary>
    public void EnterFromPlan(double lookAtXmm, double lookAtYmm, double planScale, double viewportH, double altitudeM, Action? done = null)
    {
        var top = CameraTransition.TopFromPlan(lookAtXmm, lookAtYmm, planScale, viewportH, altitudeM);
        _fitted = true;
        var to = new CameraPose(LastPose3D.Yaw, LastPose3D.Pitch, Math.Max(LastPose3D.Distance, 2), Camera.Target.X, Camera.Target.Y, Camera.Target.Z, 0);
        Start(top, to, done);
    }

    public void ReturnToPlan(double lookAtXmm, double lookAtYmm, double planScale, double viewportH, double altitudeM, Action done)
    {
        LastPose3D = Current();
        Start(Current(), CameraTransition.TopFromPlan(lookAtXmm, lookAtYmm, planScale, viewportH, altitudeM), done);
    }

    public CameraPose Current() => new(Camera.Yaw, Camera.Pitch, Camera.Distance, Camera.Target.X, Camera.Target.Y, Camera.Target.Z, OrthoBlend);
    private void Apply(CameraPose p) { Camera.Yaw = p.Yaw; Camera.Pitch = p.Pitch; Camera.Distance = p.Distance; Camera.Target = (p.TargetX, p.TargetY, p.TargetZ); OrthoBlend = p.OrthoBlend; Invalidate?.Invoke(); }

    private void Start(CameraPose from, CameraPose to, Action? done)
    {
        _timer?.Stop(); _timer = null;
        if (!Animate) { Apply(to); done?.Invoke(); return; }
        _from = from; _to = to; _done = done; _t0 = DateTime.UtcNow; Apply(from);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            var e = (DateTime.UtcNow - _t0).TotalSeconds; Apply(CameraTransition.At(_from, _to, e));
            if (e >= CameraTransition.Duration) { _timer!.Stop(); _timer = null; _done?.Invoke(); }
        };
        _timer.Start();
    }
}
