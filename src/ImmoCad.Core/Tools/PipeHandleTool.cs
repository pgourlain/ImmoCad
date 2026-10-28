using ImmoCad.Core.History;
using ImmoCad.Core.Model;

namespace ImmoCad.Core.Tools;

public enum PipeHandle { EndA, EndB, Mid, Body }

/// <summary>
/// Direct manipulation of a pipe segment in the plan (X/Y at the segment's altitude). Hybrid input comes from the base class:
/// drag a handle (Paint style) or click it then click the destination (AutoCAD style). The model is only touched on commit (one history step);
/// the preview is computed without changing anything.
/// End handle: drag along the axis = extend; drag well off the axis (or <see cref="ForceElbow"/>) = elbow along the other plan axis.
/// Mid handle: tee with a perpendicular branch. Body: move the whole run perpendicular to itself.
/// </summary>
public sealed class PipeHandleTool(BuildingActions actions, Guid networkId, Guid segmentId, PipeHandle handle, int gridMm = 50, int elbowThresholdMm = 200) : ToolStateMachine
{
    public PipeHandle Handle => handle;
    public bool ForceElbow { get; set; }
    public int? TypedLengthMm { get; set; }
    /// <summary>What the commit would do, for the overlay: the new/changed end and its live length (mm).</summary>
    public (PointMm3 From, PointMm3 To, int LengthMm)? Proposal { get; private set; }
    public bool Committed { get; private set; }
    public string? Refused { get; private set; }

    private Network Net => actions.Building.Networks.First(n => n.Id == networkId);
    private Segment Seg => Net.Segments.First(s => s.Id == segmentId);
    private PointMm3 PA => Net.Nodes.First(n => n.Id == Seg.A).Position;
    private PointMm3 PB => Net.Nodes.First(n => n.Id == Seg.B).Position;

    private int Snap(int v) => gridMm <= 0 ? v : (int)Math.Round((double)v / gridMm) * gridMm;

    protected override void OnPreview(ToolPointer p) => Proposal = Compute(p.Mm);
    protected override void OnCommit(ToolPointer p)
    {
        Refused = null; var prop = Compute(p.Mm); Proposal = null;
        if (prop is not { } pr) { Refused = "Nothing to do"; return; }
        var net = Net; var seg = Seg;
        bool ok = true;
        switch (handle)
        {
            case PipeHandle.EndA or PipeHandle.EndB:
            {
                var endId = handle == PipeHandle.EndA ? seg.A : seg.B; var end = net.Nodes.First(n => n.Id == endId).Position;
                var otherEnd = handle == PipeHandle.EndA ? PB : PA;
                var onAxis = IsOnAxis(otherEnd, end, pr.To);
                ok = onAxis ? actions.ExtendEnd(networkId, endId, pr.To) : actions.AddElbow(networkId, endId, pr.To) != null; break;
            }
            case PipeHandle.Mid: ok = actions.AddTeeBranch(networkId, segmentId, pr.From, pr.To) != null; break;
            case PipeHandle.Body: ok = actions.MoveRun(networkId, segmentId, pr.To.X - pr.From.X, pr.To.Y - pr.From.Y, 0); break;
        }
        Committed = ok; if (!ok) Refused = "Refused: the pipe would stop being axis-aligned";
    }

    private static bool IsOnAxis(PointMm3 other, PointMm3 end, PointMm3 to) =>
        (other.X != end.X ? to.Y == end.Y && to.Z == end.Z : true) && (other.Y != end.Y ? to.X == end.X && to.Z == end.Z : true) && (other.Z != end.Z ? to.X == end.X && to.Y == end.Y : true);

    private (PointMm3 From, PointMm3 To, int LengthMm)? Compute(PointMm pointer)
    {
        var a = PA; var b = PB; var z = a.Z; var px = Snap(pointer.X); var py = Snap(pointer.Y);
        var horizontalSeg = a.Z == b.Z; var alongX = a.X != b.X;
        switch (handle)
        {
            case PipeHandle.EndA or PipeHandle.EndB when horizontalSeg:
            {
                var end = handle == PipeHandle.EndA ? a : b; var other = handle == PipeHandle.EndA ? b : a;
                var perp = alongX ? Math.Abs(pointer.Y - end.Y) : Math.Abs(pointer.X - end.X);
                if (ForceElbow || perp > elbowThresholdMm)
                {   // elbow: leave along the other plan axis
                    var to = alongX ? new PointMm3(end.X, py, z) : new PointMm3(px, end.Y, z);
                    if (TypedLengthMm is { } t) to = alongX ? new(end.X, end.Y + Math.Sign(pointer.Y - end.Y == 0 ? 1 : pointer.Y - end.Y) * t, z) : new(end.X + Math.Sign(pointer.X - end.X == 0 ? 1 : pointer.X - end.X) * t, end.Y, z);
                    return to == end ? null : (end, to, Len(end, to));
                }
                var ext = alongX ? new PointMm3(px, end.Y, z) : new PointMm3(end.X, py, z);
                if (TypedLengthMm is { } tl) { var dir = alongX ? Math.Sign(end.X - other.X) : Math.Sign(end.Y - other.Y); ext = alongX ? new(other.X + dir * tl, end.Y, z) : new(end.X, other.Y + dir * tl, z); }
                return ext == end ? null : (end, ext, Len(other, ext));
            }
            case PipeHandle.Mid when horizontalSeg:
            {   // branch perpendicular to the segment from the point on the segment nearest to the pointer
                var tee = alongX ? new PointMm3(Math.Clamp(px, Math.Min(a.X, b.X) + 1, Math.Max(a.X, b.X) - 1), a.Y, z) : new PointMm3(a.X, Math.Clamp(py, Math.Min(a.Y, b.Y) + 1, Math.Max(a.Y, b.Y) - 1), z);
                var to = alongX ? new PointMm3(tee.X, py, z) : new PointMm3(px, tee.Y, z);
                if (TypedLengthMm is { } t) to = alongX ? new(tee.X, tee.Y + (pointer.Y >= tee.Y ? 1 : -1) * t, z) : new(tee.X + (pointer.X >= tee.X ? 1 : -1) * t, tee.Y, z);
                return to == tee ? null : (tee, to, Len(tee, to));
            }
            case PipeHandle.Body when horizontalSeg:
            {
                var s = new PointMm3(Snap(Start.Mm.X), Snap(Start.Mm.Y), z);
                // move perpendicular to the segment only
                var to = alongX ? new PointMm3(s.X, py, z) : new PointMm3(px, s.Y, z);
                return to == s ? null : (s, to, Len(s, to));
            }
        }
        return null;   // vertical segments are handled in the elevation view
    }

    private static int Len(PointMm3 a, PointMm3 b) => (int)Math.Round(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2)));
}
