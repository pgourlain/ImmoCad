using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using ImmoCad.Core;
using ImmoCad.Scene;

namespace ImmoCad.Desktop;

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
    /// <summary>When the active tool works on the plan (riser, palette object, door…), a click is converted to millimetres on the current level's plane and given to the same view-model methods the plan uses.</summary>
    public Func<bool>? PlaneToolActive { get; set; }
    public Func<double>? PlaneAltitudeM { get; set; }
    public Action<ImmoCad.Core.Model.PointMm>? PlaneClicked { get; set; }

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

    // ---- pipe end handles: drag constrained to the pipe's axis, gizmo drawn by the viewport ----
    public sealed record PipeHandle3D(Guid NetworkId, Guid NodeId, (double X, double Y, double Z) End, (double X, double Y, double Z) Axis, (double X, double Y, double Z) Other);
    public Func<PipeHandle3D[]>? Handles { get; set; }
    public Action<Guid, Guid, (int X, int Y, int Z)>? HandleDropped { get; set; }
    public PipeHandle3D? ActiveHandle { get; private set; }
    public (double X, double Y, double Z)? HandlePreview { get; private set; }
    private double _width, _height;

    public PipeHandle3D? HitHandle(Point p, double width, double height)
    {
        var half = Math.Min(width, height) / 2;
        foreach (var h in Handles?.Invoke() ?? [])
        { var (px, py, _) = Camera.Project(h.End.X, h.End.Y, h.End.Z); var s = new Point(width / 2 + px * half, height / 2 - py * half); if (Math.Abs(s.X - p.X) <= 10 && Math.Abs(s.Y - p.Y) <= 10) return h; }
        return null;
    }

    /// <summary>Point of the pipe's axis line closest to the mouse ray: the drag cannot leave the axis.</summary>
    public (double X, double Y, double Z) OnAxis(PipeHandle3D h, Point p, double width, double height)
    {
        var half = Math.Min(width, height) / 2; double sx = (p.X - width / 2) / half, sy = -(p.Y - height / 2) / half; var (o, d) = Camera.Unproject(sx, sy, 1);
        var a = h.Axis; var w0 = (X: h.End.X - o.X, Y: h.End.Y - o.Y, Z: h.End.Z - o.Z);
        double aa = a.X * a.X + a.Y * a.Y + a.Z * a.Z, ab = a.X * d.X + a.Y * d.Y + a.Z * d.Z, bb = d.X * d.X + d.Y * d.Y + d.Z * d.Z, aw = a.X * w0.X + a.Y * w0.Y + a.Z * w0.Z, bw = d.X * w0.X + d.Y * w0.Y + d.Z * w0.Z;
        var den = aa * bb - ab * ab; var t = Math.Abs(den) < 1e-9 ? 0 : (ab * bw - bb * aw) / den;       // parameter along the axis
        return (h.End.X + a.X * t, h.End.Y + a.Y * t, h.End.Z + a.Z * t);
    }

    // ---- input ----
    public void Down(Point p, bool rightOrMiddle) { _last = p; _down = p; _pan = rightOrMiddle; if (!rightOrMiddle && HitHandle(p, _width, _height) is { } h) { ActiveHandle = h; HandlePreview = h.End; } }
    public void Size(double width, double height) { _width = width; _height = height; }
    public void Up(Point p, MainViewModel? vm, double width, double height)
    {
        _last = null; Size(width, height);
        if (ActiveHandle is { } ah)
        {
            var at = HandlePreview ?? ah.End; ActiveHandle = null; HandlePreview = null; Invalidate?.Invoke();
            if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 3) HandleDropped?.Invoke(ah.NetworkId, ah.NodeId, ((int)Math.Round(at.X * 1000), (int)Math.Round(at.Y * 1000), (int)Math.Round(at.Z * 1000)));
            return;
        }
        if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) < 4 && !_pan) { if (PlaneToolActive?.Invoke() == true && PlaneAt(p, width, height, PlaneAltitudeM?.Invoke() ?? 0) is { } mm) PlaneClicked?.Invoke(mm); else Pick(p, width, height); }
    }
    public void Move(Point p)
    {
        if (ActiveHandle is { } ah2) { HandlePreview = OnAxis(ah2, p, _width, _height); Invalidate?.Invoke(); return; }
        if (_last is not { } l || IsAnimating) return;
        if (_pan) { var k = Camera.Distance * 0.0012; double dx = (p.X - l.X) * k, dy = (p.Y - l.Y) * k; var (cy, sy) = (Math.Cos(Camera.Yaw), Math.Sin(Camera.Yaw)); Camera.Target = (Camera.Target.X - dx * cy + dy * Math.Sin(Camera.Pitch) * sy, Camera.Target.Y + dx * sy + dy * Math.Sin(Camera.Pitch) * cy, Camera.Target.Z); }
        else Camera.Orbit(-(p.X - l.X) * 0.01, (p.Y - l.Y) * 0.01);
        _last = p; Invalidate?.Invoke();
    }
    public void Wheel(double dy) { if (IsAnimating) return; Camera.Zoom(dy > 0 ? 0.9 : 1.1); Invalidate?.Invoke(); }
    /// <summary>Looks from the given angles (animated, shortest way round), keeping target and distance.</summary>
    public void LookFrom(double yaw, double pitch)
    {
        var cur = Current(); var d = Math.IEEERemainder(yaw - cur.Yaw, 2 * Math.PI);
        Start(cur, cur with { Yaw = cur.Yaw + d, Pitch = pitch, OrthoBlend = 0 }, () => Invalidate?.Invoke()); Invalidate?.Invoke();
    }

    public void Preset(string name)
    {
        switch (name) { case "top": Camera.Top(); break; case "front": Camera.Yaw = 0; Camera.Pitch = 0.02; break; case "side": Camera.Yaw = Math.PI / 2; Camera.Pitch = 0.02; break; default: Camera.Iso(); break; }
        Invalidate?.Invoke();
    }

    /// <summary>The point of the horizontal plane z = <paramref name="zMeters"/> under a screen point, in millimetres (null when the ray is parallel to it).</summary>
    public ImmoCad.Core.Model.PointMm? PlaneAt(Point p, double width, double height, double zMeters)
    {
        var half = Math.Min(width, height) / 2; double sx = (p.X - width / 2) / half, sy = -(p.Y - height / 2) / half; var (o, d) = Camera.Unproject(sx, sy, 1);
        if (Math.Abs(d.Z) < 1e-9) return null; var t = (zMeters - o.Z) / d.Z; return new((int)Math.Round((o.X + d.X * t) * 1000), (int)Math.Round((o.Y + d.Y * t) * 1000));
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
