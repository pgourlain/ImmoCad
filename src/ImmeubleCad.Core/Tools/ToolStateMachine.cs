using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core.Tools;

public enum ToolState { Idle, Pressed, Dragging, Placing, Committed }
public readonly record struct ToolPointer(PointMm Mm, double ScreenX, double ScreenY);

/// <summary>Hybrid input: press+release without moving = click mode (Placing); press+move = drag mode.</summary>
public abstract class ToolStateMachine
{
    public ToolState State { get; protected set; } = ToolState.Idle;
    public double DragThresholdPx { get; set; } = 4;
    protected ToolPointer Start;
    protected ToolPointer Last;

    public void Down(ToolPointer p)
    {
        if (State == ToolState.Placing) { Last = p; OnPlace(p); return; }
        Start = Last = p; State = ToolState.Pressed; OnStart(p);
    }

    public void Move(ToolPointer p)
    {
        Last = p;
        if (State == ToolState.Pressed && Dist(Start, p) > DragThresholdPx) State = ToolState.Dragging;
        if (State is ToolState.Dragging or ToolState.Placing) OnPreview(p);
    }

    public void Up(ToolPointer p)
    {
        Last = p;
        if (State == ToolState.Pressed) State = ToolState.Placing;      // click mode
        else if (State == ToolState.Dragging) { OnCommit(p); State = ToolState.Committed; }
    }

    public void Cancel() { OnCancel(); State = ToolState.Idle; }
    public void Reset() => State = ToolState.Idle;
    /// <summary>Called by chained tools to finish the gesture while in click mode.</summary>
    public void Finish() { if (State == ToolState.Placing) { OnCommit(Last); State = ToolState.Committed; } }

    protected virtual void OnStart(ToolPointer p) { }
    protected virtual void OnPlace(ToolPointer p) { OnCommit(p); State = ToolState.Committed; }
    protected virtual void OnPreview(ToolPointer p) { }
    protected virtual void OnCommit(ToolPointer p) { }
    protected virtual void OnCancel() { }
    private static double Dist(ToolPointer a, ToolPointer b) => Math.Sqrt((a.ScreenX - b.ScreenX) * (a.ScreenX - b.ScreenX) + (a.ScreenY - b.ScreenY) * (a.ScreenY - b.ScreenY));
}

/// <summary>Chained wall/pipe drawing: each click adds a point; Finish commits the whole run as one history step.</summary>
public sealed class DrawChainTool(Action<IReadOnlyList<PointMm>> commit) : ToolStateMachine
{
    private readonly List<PointMm> _pts = new();
    public IReadOnlyList<PointMm> Points => _pts;
    public PointMm? Preview { get; private set; }

    protected override void OnStart(ToolPointer p) { if (_pts.Count == 0) _pts.Add(p.Mm); }
    protected override void OnPlace(ToolPointer p) { _pts.Add(p.Mm); }
    protected override void OnPreview(ToolPointer p) => Preview = p.Mm;
    protected override void OnCommit(ToolPointer p)
    {
        if (State == ToolState.Dragging) _pts.Add(p.Mm);
        if (_pts.Count >= 2) commit(_pts.ToList());
        _pts.Clear(); Preview = null;
    }
    protected override void OnCancel() { _pts.Clear(); Preview = null; }
    public void Backspace() { if (_pts.Count > 1) _pts.RemoveAt(_pts.Count - 1); }
    /// <summary>Typed length along the current direction (mm).</summary>
    public PointMm? ApplyLength(int lengthMm)
    {
        if (_pts.Count == 0 || Preview is not { } pv) return null;
        var o = _pts[^1]; double dx = pv.X - o.X, dy = pv.Y - o.Y; var l = Math.Sqrt(dx * dx + dy * dy);
        if (l < 1) return null;
        var np = new PointMm(o.X + (int)Math.Round(dx / l * lengthMm), o.Y + (int)Math.Round(dy / l * lengthMm));
        _pts.Add(np); return np;
    }
    public static bool TryParseLength(string s, out int mm)
    {
        s = s.Trim().Replace(',', '.');
        if (s.EndsWith("mm", StringComparison.OrdinalIgnoreCase)) return int.TryParse(s[..^2], out mm);
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m)) { mm = (int)Math.Round(m * 1000); return true; }
        mm = 0; return false;
    }
}
