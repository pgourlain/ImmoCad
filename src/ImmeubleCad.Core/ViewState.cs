using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Core;

public enum RenderMode { Plan, Solid, XRay, Wireframe }

/// <summary>State shared by plan and 3D views. Neither view owns it.</summary>
public sealed class ViewState
{
    public Guid? CurrentLevel { get; private set; }
    public HashSet<Guid> Selection { get; } = new();
    public PointMm LookAt { get; private set; }
    public double Scale { get; private set; } = 0.1; // px per mm
    public RenderMode Mode { get; private set; } = RenderMode.Plan;
    public bool CutAboveCurrent { get; private set; } = true;
    public event Action<string>? ViewStateChanged;

    public void SetLevel(Guid id) { CurrentLevel = id; Raise(nameof(CurrentLevel)); }
    public void SetLookAt(PointMm p) { LookAt = p; Raise(nameof(LookAt)); }
    public void SetScale(double s) { Scale = Math.Clamp(s, 0.001, 10); Raise(nameof(Scale)); }
    public void SetMode(RenderMode m) { Mode = m; Raise(nameof(Mode)); }
    public void SetCut(bool on) { CutAboveCurrent = on; Raise(nameof(CutAboveCurrent)); }
    public void Select(Guid id, bool add = false) { if (!add) Selection.Clear(); Selection.Add(id); Raise(nameof(Selection)); }
    private void Raise(string what) => ViewStateChanged?.Invoke(what);
}
