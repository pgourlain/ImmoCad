using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using ImmoCad.Core;
using ImmoCad.Core.Walk;
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

    // ---- walk mode (first person): perspective camera driven by the view model's WalkState; OpenGL only ----
    public WalkCamera WalkCam { get; } = new();
    public bool WalkMode { get; set; }
    public bool Ghost => WalkSource?.Invoke()?.Ghost == true;
    public Func<WalkState?>? WalkSource { get; set; }
    /// <summary>A click on the floor: where to glide to, in millimetres.</summary>
    public Action<double, double>? WalkGoTo { get; set; }
    public Guid? HoverId { get; private set; }
    /// <summary>The object under the cursor (null when none) and the cursor position, for the label.</summary>
    public Action<(Guid Id, SceneKind Kind)?, Point>? Hovered { get; set; }
    private DateTime _lastHover;
    private double _aspect = 1;

    /// <summary>Puts the camera where the walk state is.</summary>
    public void ApplyWalk(WalkState w) { WalkCam.Eye = (w.X / 1000, w.Y / 1000, w.EyeZ / 1000); WalkCam.Yaw = w.Yaw; WalkCam.SetPitch(w.Pitch); }
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
        if (vm.IsWalk && vm.Walk is { } walk)
        {   // the current level only, floor and ceiling, what crosses its height band; the camera is the walker's eyes
            Objects = SceneBuilder.Build(SceneMapper.Map(vm.Building), new SceneOptions(walk.LevelId, LevelFilter.CurrentOnly, false, false, false, vm.SelectedWalls, vm.HiddenLevels, true, ((int)walk.Area.Bounds.MinX, (int)walk.Area.Bounds.MinY, (int)walk.Area.Bounds.MaxX, (int)walk.Area.Bounds.MaxY)));
            ApplyWalk(walk); return;
        }
        Objects = SceneBuilder.Build(SceneMapper.Map(vm.Building), new SceneOptions(vm.CurrentLevel?.Id, Filter, CutAboveCurrent, XRay, true, vm.SelectedWalls, vm.HiddenLevels));
        var pts = Objects.Where(o => o.Kind == SceneKind.Wall).ToList();
        if (pts.Count > 0)
        {
            var min = (X: pts.Min(o => o.Mesh.Min().X), Y: pts.Min(o => o.Mesh.Min().Y)); var max = (X: pts.Max(o => o.Mesh.Max().X), Y: pts.Max(o => o.Mesh.Max().Y));
            var levelZ = (vm.CurrentLevel?.AltitudeMm ?? 0) / 1000.0 + 1.35;
            if (!_fitted) { Camera.Target = ((min.X + max.X) / 2, (min.Y + max.Y) / 2, levelZ); Camera.Distance = Math.Max(max.X - min.X, max.Y - min.Y) * 1.8 + 4; _fitted = true; }
            else if (Math.Abs(levelZ - _levelZ) > 1e-9) Camera.Target = (Camera.Target.X, Camera.Target.Y, Camera.Target.Z + levelZ - _levelZ);   // level bar moves the camera target vertically; a pan or zoom is kept
            _levelZ = levelZ;
        }
    }
    private bool _fitted; private double _levelZ;
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
    public void Down(Point p, bool rightOrMiddle) { if (WalkMode) { _last = p; _down = p; return; } _last = p; _down = p; _pan = rightOrMiddle; if (!rightOrMiddle && HitHandle(p, _width, _height) is { } h) { ActiveHandle = h; HandlePreview = h.End; } }
    public void Size(double width, double height) { _width = width; _height = height; if (height > 0) _aspect = width / height; }
    /// <summary>True while the view is being turned or panned with the mouse: the software viewport draws a lighter frame meanwhile.</summary>
    public bool Dragging => _last != null && ActiveHandle == null;
    public void Up(Point p, MainViewModel? vm, double width, double height)
    {
        if (WalkMode) { var click = _last != null && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) < 4; _last = null; Size(width, height); if (click) WalkClick(p, width, height); return; }
        var wasDragging = Dragging; _last = null; Size(width, height); if (wasDragging) Invalidate?.Invoke();      // full-quality frame once the mouse is released
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
        if (WalkMode)
        {
            if (WalkSource?.Invoke() is not { } w) return;
            if (_last is { } l0) { w.Turn(-(p.X - l0.X) * 0.004, -(p.Y - l0.Y) * 0.004); _last = p; ApplyWalk(w); Invalidate?.Invoke(); }     // drag = look, the pointer is never captured away from the screen
            else Hover(p);
            return;
        }
        if (ActiveHandle is { } ah2) { HandlePreview = OnAxis(ah2, p, _width, _height); Invalidate?.Invoke(); return; }
        if (_last is not { } l || IsAnimating) return;
        if (_pan)
        {   // the model follows the mouse: the target moves against the drag along the screen's right and up directions, one pixel = Distance / viewport size
            var half = Math.Min(_width, _height) / 2; var k = half > 1 ? Camera.Distance / (2 * half) : Camera.Distance * 0.0012; double mx = (p.X - l.X) * k, my = (p.Y - l.Y) * k;
            var (cy, sy) = (Math.Cos(Camera.Yaw), Math.Sin(Camera.Yaw)); var (cp, sp) = (Math.Cos(Camera.Pitch), Math.Sin(Camera.Pitch));
            Camera.Target = (Camera.Target.X - mx * cy - my * sy * sp, Camera.Target.Y - mx * sy + my * cy * sp, Camera.Target.Z + my * cp);
        }
        else Camera.Orbit((p.X - l.X) * 0.01, -(p.Y - l.Y) * 0.01);          // the camera follows the mouse: drag right turns the view to the right, drag up looks from higher
        _last = p; Invalidate?.Invoke();
    }
    /// <summary>Zoom in proportion to the wheel delta: a mouse notch (1) is about 8 %, a trackpad sends many small deltas and zooms smoothly.</summary>
    public void Wheel(double dy) { if (WalkMode) { WalkCam.SetFov(WalkCam.FovDegrees - Math.Clamp(dy, -3, 3) * 3); Invalidate?.Invoke(); return; } if (IsAnimating) return; Camera.Zoom(Math.Exp(-Math.Clamp(dy, -3, 3) * 0.08)); Invalidate?.Invoke(); }
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

    // ---- walk: rays from the eye ----
    private ((double X, double Y, double Z) O, (double X, double Y, double Z) D) WalkRay(Point p, double width, double height)
        => WalkCam.Unproject(p.X / Math.Max(width, 1) * 2 - 1, 1 - p.Y / Math.Max(height, 1) * 2, width / Math.Max(height, 1));

    private (Guid Id, SceneKind Kind, float Distance)? WalkHit(Point p, double width, double height)
    {
        var (o, d) = WalkRay(p, width, height);
        return SceneBuilder.Pick(Objects, ((float)o.X, (float)o.Y, (float)o.Z), ((float)d.X, (float)d.Y, (float)d.Z));
    }

    /// <summary>A click: an object under the cursor is selected; the floor is a destination (what is visible is reachable, so no path finding).</summary>
    private void WalkClick(Point p, double width, double height)
    {
        var (o, d) = WalkRay(p, width, height); var hit = WalkHit(p, width, height); var floorZ = PlaneAltitudeM?.Invoke() ?? 0;
        double? tFloor = d.Z < -1e-6 && (floorZ - o.Z) / d.Z is var t && t > 0 ? t : null;
        if (hit is { } h && (tFloor == null || h.Distance < tFloor)) { Picked?.Invoke(h.Id, h.Kind); return; }
        if (tFloor is { } tf) WalkGoTo?.Invoke((o.X + d.X * tf) * 1000, (o.Y + d.Y * tf) * 1000);
    }

    private void Hover(Point p)
    {
        if ((DateTime.UtcNow - _lastHover).TotalMilliseconds < 60) return; _lastHover = DateTime.UtcNow;
        var hit = WalkHit(p, _width, _height); var id = hit?.Id; if (id != HoverId) { HoverId = id; Invalidate?.Invoke(); }
        Hovered?.Invoke(hit is { } h ? (h.Id, h.Kind) : null, p);
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
        _fitted = true; _levelZ = altitudeM + 1.35;
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
