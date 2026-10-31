using ImmoCad.Core.Model;

namespace ImmoCad.Core.Walk;

/// <summary>What the user asks for during one frame: each axis in [-1, 1].</summary>
public readonly record struct WalkInput(double Forward, double Strafe, double Vertical, bool Run)
{
    public bool Any => Forward != 0 || Strafe != 0 || Vertical != 0;
}

public enum WalkNoticeKind
{
    /// <summary>The position was moved to the nearest place where the user can stand (Value = metres).</summary>
    Shifted,
    /// <summary>A level change was refused: the target level has no walls.</summary>
    LevelHasNoWalls,
    /// <summary>The level followed the eye's altitude (ghost mode); the view must follow.</summary>
    LevelChanged,
}
public readonly record struct WalkNotice(WalkNoticeKind Kind, double Value = 0);

/// <summary>
/// First-person state: position on the plan (mm), eye altitude (mm), view direction, ghost mode. Pure logic, unit-tested without any view.
/// Moving changes nothing in the building: it is view state, never a history step.
/// </summary>
public sealed class WalkState
{
    public const double DefaultEyeMm = 1600, SpeedMmPerS = 1400, RunFactor = 2.5, VerticalSpeedMmPerS = 500, MinAboveFloorMm = 200, BelowCeilingMm = 100;
    private const double EaseSeconds = 0.12;

    public Building Building { get; }
    public Level Level { get; private set; }
    public Guid LevelId => Level.Id;
    public WalkArea Area { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    /// <summary>Altitude of the eye in mm (absolute, like level altitudes).</summary>
    public double EyeZ { get; private set; }
    public double Yaw { get; set; }
    public double Pitch { get; set; }
    public bool Ghost { get; private set; }
    public WalkPoint? Goal { get; private set; }
    public double EyeAboveFloor => EyeZ - Level.AltitudeMm;
    public WalkPoint Position => new(X, Y);
    public event Action<WalkNotice>? Notice;

    private double _vx, _vy;
    private WalkPoint? _want;                                   // XY asked for at the last level change, kept until the next move so that up then down returns exactly
    private (double X, double Y, double Z)? _ghostReturn;       // where ghost mode was left from, to go back to it if nothing moved since

    public WalkState(Building building, Level level, WalkPoint? start = null, double yaw = 0)
    {
        Building = building; Level = level; Area = new WalkArea(building, level); Yaw = yaw;
        EyeZ = level.AltitudeMm + ClampAbove(DefaultEyeMm, level);
        var p = start ?? DefaultStart(); var s = Area.NearestWalkable(p, EyeAboveFloor); X = s.X; Y = s.Y;
    }

    /// <summary>After an undo or a redo the model objects are new ones: finds the level again by id and rebuilds what blocks. Returns false when the level no longer exists.</summary>
    public bool Rebind()
    {
        var l = Building.Levels.FirstOrDefault(x => x.Id == Level.Id); if (l == null) return false;
        Level = l; Area = new WalkArea(Building, l); if (!Ghost && Area.Collides(Position, EyeAboveFloor)) { var to = Area.NearestWalkable(Position, EyeAboveFloor); X = to.X; Y = to.Y; }
        return true;
    }

    private static double ClampAbove(double mm, Level l) => Math.Clamp(mm, MinAboveFloorMm, Math.Max(MinAboveFloorMm, l.HeightMm - BelowCeilingMm));

    /// <summary>The middle of the level's walls (a point that is free is found from there).</summary>
    private WalkPoint DefaultStart()
    {
        var pts = Level.Walls.SelectMany(w => w.Footprint).ToList(); if (pts.Count == 0) return new(0, 0);
        return new((pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0, (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0);
    }

    public (double X, double Y, double Z) Forward => (-Math.Sin(Yaw) * Math.Cos(Pitch), Math.Cos(Yaw) * Math.Cos(Pitch), Math.Sin(Pitch));

    public void Turn(double dYaw, double dPitch)
    {
        Yaw += dYaw; Pitch = Math.Clamp(Pitch + dPitch, -85 * Math.PI / 180, 85 * Math.PI / 180);
    }

    public void GoTo(WalkPoint p) { Goal = Area.Bounds.Clamp(p); }
    public void Stop() { Goal = null; _vx = _vy = 0; }

    /// <summary>Back to the default eye height (Home).</summary>
    public void ResetEye() { EyeZ = Level.AltitudeMm + ClampAbove(DefaultEyeMm, Level); _ghostReturn = null; }

    /// <summary>One frame: eases towards the wanted velocity, moves with sliding, follows a click-to-go target, raises or lowers the eye.</summary>
    public void Update(double dt, WalkInput input)
    {
        if (dt <= 0) return;
        double wx = 0, wy = 0;
        if (input.Any) { Goal = null; _want = null; _ghostReturn = null; }
        var speed = SpeedMmPerS * (input.Run ? RunFactor : 1);
        if (input.Forward != 0 || input.Strafe != 0)
        {   // forward follows the horizontal part of the view direction, strafe is perpendicular to it
            double fx = -Math.Sin(Yaw), fy = Math.Cos(Yaw), rx = Math.Cos(Yaw), ry = Math.Sin(Yaw);
            wx = fx * input.Forward + rx * input.Strafe; wy = fy * input.Forward + ry * input.Strafe;
            var l = Math.Sqrt(wx * wx + wy * wy); if (l > 1) { wx /= l; wy /= l; }
            wx *= speed; wy *= speed;
        }
        else if (Goal is { } g)
        {
            double dx = g.X - X, dy = g.Y - Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 40) { X = g.X; Y = g.Y; Goal = null; _vx = _vy = 0; }                       // arrived: land exactly on the clicked point
            else { var sp = Math.Min(speed, Math.Max(d * 4, 200)); wx = dx / d * sp; wy = dy / d * sp; }   // slows down on approach
        }
        var k = 1 - Math.Exp(-dt / EaseSeconds);
        _vx += (wx - _vx) * k; _vy += (wy - _vy) * k;
        if (Math.Abs(_vx) < 1 && Math.Abs(_vy) < 1 && wx == 0 && wy == 0) { _vx = _vy = 0; }
        if (_vx != 0 || _vy != 0)
        {
            var from = Position; var to = Area.Move(from, _vx * dt, _vy * dt, EyeAboveFloor, Ghost); X = to.X; Y = to.Y;
            if (Goal != null && Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y) < 0.05 * Math.Abs(_vx * dt) + 0.05 * Math.Abs(_vy * dt) + 0.01) { Goal = null; _vx = _vy = 0; }   // against a wall: stop
        }
        if (input.Vertical != 0) MoveEye(input.Vertical * VerticalSpeedMmPerS * dt);
    }

    private void MoveEye(double dz)
    {
        if (!Ghost)
        {
            EyeZ = Level.AltitudeMm + Math.Clamp(EyeAboveFloor + dz, MinAboveFloorMm, Math.Max(MinAboveFloorMm, Level.HeightMm - BelowCeilingMm)); return;
        }
        // ghost: the eye crosses slabs, the current level follows its altitude
        var b = Area.Bounds; EyeZ = Math.Clamp(EyeZ + dz, b.MinZ + 100, b.MaxZ - 100);
        var at = LevelAtAltitude(EyeZ);
        if (at != null && at != Level) { Level = at; Area = new WalkArea(Building, Level); Notice?.Invoke(new(WalkNoticeKind.LevelChanged)); }
    }

    private Level? LevelAtAltitude(double z)
    {
        if (Building.Levels.Count == 0) return null;
        return Building.Levels.FirstOrDefault(l => z >= l.AltitudeMm && z < l.AltitudeMm + l.HeightMm)
            ?? Building.Levels.OrderBy(l => Math.Min(Math.Abs(z - l.AltitudeMm), Math.Abs(z - (l.AltitudeMm + l.HeightMm)))).First();
    }

    /// <summary>
    /// Elevator: the same XY on another level (the XY asked for at the first change is remembered until the next move, so up then down comes back exactly),
    /// moved to the nearest free place when it falls in a wall. Refused for a level without walls. Returns false when refused.
    /// </summary>
    public bool ChangeLevel(Level target)
    {
        if (target == Level) return true;
        if (target.Walls.Count == 0) { Notice?.Invoke(new(WalkNoticeKind.LevelHasNoWalls)); return false; }
        _want ??= Position; var wasInside = Area.WallBox() is { } ob && X >= ob.X0 && X <= ob.X1 && Y >= ob.Y0 && Y <= ob.Y1;       // in the building on the level being left
        var above = Math.Clamp(EyeAboveFloor, MinAboveFloorMm, Math.Max(MinAboveFloorMm, target.HeightMm - BelowCeilingMm));
        var before = Position; Level = target; Area = new WalkArea(Building, target); EyeZ = target.AltitudeMm + above; Goal = null; _vx = _vy = 0; _ghostReturn = null;
        var want = _want.Value;
        if (Ghost) { X = want.X; Y = want.Y; return true; }
        var to = Area.NearestWalkable(want, EyeAboveFloor, before, insideWalls: wasInside);                                      // a smaller level does not drop the user outside
        X = to.X; Y = to.Y; var shift = Math.Sqrt((to.X - want.X) * (to.X - want.X) + (to.Y - want.Y) * (to.Y - want.Y));
        if (shift > 20) Notice?.Invoke(new(WalkNoticeKind.Shifted, shift / 1000));
        return true;
    }

    /// <summary>
    /// Turns ghost mode on or off. Leaving it inside a wall moves to the nearest free place (with a notice); leaving it outside the building keeps the position,
    /// the user floats there; an eye in a slab is brought back into the level's height range. Switching it back on before any move returns to where it was left.
    /// </summary>
    public void SetGhost(bool on)
    {
        if (on == Ghost) return;
        if (on)
        {
            Ghost = true;
            if (_ghostReturn is { } r) { X = r.X; Y = r.Y; EyeZ = r.Z; var at = LevelAtAltitude(EyeZ); if (at != null && at != Level) { Level = at; Area = new WalkArea(Building, Level); Notice?.Invoke(new(WalkNoticeKind.LevelChanged)); } }
            _ghostReturn = null; return;
        }
        _ghostReturn = (X, Y, EyeZ); Ghost = false; Goal = null; _vx = _vy = 0;
        var lvl = LevelAtAltitude(EyeZ) ?? Level;
        if (lvl != Level) { Level = lvl; Area = new WalkArea(Building, Level); Notice?.Invoke(new(WalkNoticeKind.LevelChanged)); }
        EyeZ = Level.AltitudeMm + ClampAbove(EyeAboveFloor, Level);
        if (Area.Collides(Position, EyeAboveFloor))
        {
            var to = Area.NearestWalkable(Position, EyeAboveFloor); var d = Math.Sqrt((to.X - X) * (to.X - X) + (to.Y - Y) * (to.Y - Y)); X = to.X; Y = to.Y;
            if (d > 20) Notice?.Invoke(new(WalkNoticeKind.Shifted, d / 1000));
        }
    }

    /// <summary>Puts the user at a point (minimap jump, "walk from here"): the nearest free place, unless in ghost mode.</summary>
    public void Jump(WalkPoint p)
    {
        Goal = null; _vx = _vy = 0; _want = null; _ghostReturn = null;
        var to = Ghost ? Area.Bounds.Clamp(p) : Area.NearestWalkable(p, EyeAboveFloor); X = to.X; Y = to.Y;
    }
}
