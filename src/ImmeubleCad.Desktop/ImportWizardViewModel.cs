using CommunityToolkit.Mvvm.ComponentModel;
using ImmeubleCad.Core.History;
using ImmeubleCad.Core.Import;
using ImmeubleCad.Core.Model;
using ImmeubleCad.Plan;

namespace ImmeubleCad.Desktop;

public enum WizardStep { Straighten, Calibrate, Detect, Review }

/// <summary>State and logic of the import wizard (one level at a time). UI-free so it can be unit-tested.</summary>
public partial class ImportWizardViewModel : ObservableObject
{
    public byte[] Original { get; }
    [ObservableProperty] private byte[] _image;
    [ObservableProperty] private WizardStep _step = WizardStep.Straighten;
    [ObservableProperty] private double _angle;
    [ObservableProperty] private double _distanceMetres = 5;
    [ObservableProperty] private bool _twoAxes;
    [ObservableProperty] private double _distanceVMetres = 5;
    [ObservableProperty] private int _threshold;          // 0 = automatic
    [ObservableProperty] private int _minThickness = 5;
    [ObservableProperty] private int _bridgeGap;
    [ObservableProperty] private int _minArea = 20;
    [ObservableProperty] private bool _straightenWalls = true;
    [ObservableProperty] private string _message = "";

    public List<(double X, double Y)> CalibrationPoints { get; } = new();   // 2 (or 4 with two axes) clicks, in pixels
    public List<WallOutline> Detected { get; private set; } = new();
    public HashSet<int> Rejected { get; } = new();                           // review step: shapes the user removed
    public GrayImage? Gray { get; private set; }
    public Calibration? Calibration { get; private set; }

    public ImportWizardViewModel(byte[] data) { Original = data; _image = data; Gray = PlanImport.Decode(data); }

    public int Width => PlanImport.Size(Image).Width;
    public int Height => PlanImport.Size(Image).Height;

    public void AutoDeskew() { Angle = Deskew.EstimateAngle(Gray!); Message = $"{Angle:0.0}°"; ApplyRotation(); }

    /// <summary>Rotates the original by -Angle (straightens a tilt of Angle) and redecodes.</summary>
    public void ApplyRotation()
    {
        Image = PlanImport.Rotate(Original, -Angle); Gray = PlanImport.Decode(Image); CalibrationPoints.Clear(); Calibration = null;
    }

    public void AddCalibrationPoint(double x, double y)
    {
        if (CalibrationPoints.Count >= (TwoAxes ? 4 : 2)) CalibrationPoints.Clear();
        CalibrationPoints.Add((x, y)); Calibration = null;
        if (CalibrationPoints.Count == (TwoAxes ? 4 : 2)) ComputeCalibration();
    }

    public void ComputeCalibration()
    {
        var p = CalibrationPoints;
        if (TwoAxes && p.Count == 4) Calibration = PlanImport.TwoAxis(p[0], p[1], DistanceMetres, p[2], p[3], DistanceVMetres);
        else if (p.Count >= 2) { var s = PlanImport.PxPerMm(p[0], p[1], DistanceMetres); Calibration = new(s, s); }
        Message = Calibration is { } c ? $"{c.PxPerMmX:0.####} px/mm" : "";
    }

    public bool IsCalibrated => Calibration != null;

    public void AutoThickness() { MinThickness = PlanWallDetector.SuggestThickness(Gray!, Threshold); Message = $"≥ {MinThickness} px"; }

    public WallDetectionOptions Options => new(Threshold, MinThickness, BridgeGap, MinArea);

    /// <summary>The wall mask (0/255) for the live preview drawn in red over the faded plan.</summary>
    public byte[] PreviewMask(CancellationToken ct = default) =>
        LineExtraction.Extract(Gray!.Pixels, Gray.Width, Gray.Height, new LineExtractionOptions(LineMode.Walls, Threshold, MinThickness, BridgeGap, MinArea), ct);

    public void Detect(CancellationToken ct = default)
    {
        Detected = new PlanWallDetectorAdapter().Detect(Gray!, Options, ct); Rejected.Clear();
        Message = $"{Detected.Count} shape(s)";
    }

    public void Reject(int index) { if (!Rejected.Remove(index)) Rejected.Add(index); }

    /// <summary>Adds the accepted walls in ONE history step and sets the plan as the level background.</summary>
    public int Apply(BuildingActions actions, Level level, IDictionary<string, byte[]> images)
    {
        var cal = Calibration ?? throw new InvalidOperationException("Not calibrated");
        var keep = Detected.Where((_, i) => !Rejected.Contains(i)).ToList();
        List<PointPx> S(List<PointPx> o) => StraightenWalls ? WallStraightener.Straighten(o, 3) : o;
        var shapes = keep.Select(w => ((IEnumerable<PointMm>)PlanImport.ToMm(S(w.Outline), cal, Height),
            (IEnumerable<IEnumerable<PointMm>>)w.Holes.Select(h => (IEnumerable<PointMm>)PlanImport.ToMm(S(h), cal, Height)).ToList())).ToList();
        var name = $"{level.Id:N}.png"; images[name] = Image;
        actions.SetBackground(level.Id, name, PlanImport.TransformFor(cal, Height));
        actions.ImportWallShapes(level.Id, shapes);
        return shapes.Count;
    }
}
