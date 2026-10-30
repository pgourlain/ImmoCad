using CommunityToolkit.Mvvm.ComponentModel;
using ImmoCad.Core;
using ImmoCad.Core.History;
using ImmoCad.Core.Import;
using ImmoCad.Core.Model;
using ImmoCad.Plan;

namespace ImmoCad.Desktop;

public enum WizardStep { Straighten, Calibrate, Detect, Review }

/// <summary>An already imported level whose picture and scale can calibrate the new plan.</summary>
public sealed record WizardReference(string Name, byte[] Image, double[] Transform)
{
    public override string ToString() => Name;
}

/// <summary>State and logic of the import wizard (one level at a time). UI-free so it can be unit-tested.</summary>
public partial class ImportWizardViewModel : ObservableObject
{
    public byte[] Original { get; }
    [ObservableProperty] private byte[] _image;
    [ObservableProperty] private WizardStep _step = WizardStep.Straighten;
    [ObservableProperty] private double _angle;
    [ObservableProperty] private double _distanceMetres = 5;
    [ObservableProperty] private bool _twoAxes;
    [ObservableProperty] private double _scaleDenominator = 100;   // printed scale 1:N, often written on the plan
    [ObservableProperty] private double _dpi;                     // scan resolution; from the file when it carries one
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

    // scans of plans are ~300 dpi; a density of 72/96 is just the default written by image editors
    public ImportWizardViewModel(byte[] data, IReadOnlyList<WizardReference>? references = null) { Original = data; _image = data; _dpi = PlanImport.DensityDpi(data) is > 96 and var d ? Math.Round(d) : 300; Gray = PlanImport.Decode(data); References = references ?? []; }

    // ---- calibration against an imported level: two points on the reference, the same two features on the new plan
    public IReadOnlyList<WizardReference> References { get; }
    private WizardReference? _reference;
    public WizardReference? Reference
    {
        get => _reference;
        set { _reference = value; ReferencePx.Clear(); ReferenceMm.Clear(); CalibrationPoints.Clear(); Calibration = null; Offset = (0, 0); OnPropertyChanged(); OnPropertyChanged(nameof(UsesReference)); }
    }
    public bool UsesReference => Reference != null;
    public List<(double X, double Y)> ReferencePx { get; } = new();   // clicks on the reference picture
    public List<(double X, double Y)> ReferenceMm { get; } = new();   // the same points in plan millimetres
    /// <summary>True while the reference picture (not the new plan) is the one to click on.</summary>
    public bool ShowingReference => UsesReference && ReferenceMm.Count < 2;
    /// <summary>Millimetres added to the converted plan so that it lands on the reference.</summary>
    public (double X, double Y) Offset { get; private set; }

    /// <summary>Calibration click: on the reference first (2 points), then on the new plan (2 points).</summary>
    public void AddPoint(double x, double y)
    {
        if (!UsesReference) { AddCalibrationPoint(x, y); return; }
        if (ReferenceMm.Count < 2)
        {
            var t = Reference!.Transform; ReferencePx.Add((x, y));
            ReferenceMm.Add((t[0] * x + t[1] * y + t[2], t[3] * x + t[4] * y + t[5])); return;
        }
        if (CalibrationPoints.Count >= 2) CalibrationPoints.Clear();
        CalibrationPoints.Add((x, y)); Calibration = null;
        if (CalibrationPoints.Count == 2) ComputeReferenceCalibration();
    }

    private void ComputeReferenceCalibration()
    {
        var (a, b) = (CalibrationPoints[0], CalibrationPoints[1]); var (ma, mb) = (ReferenceMm[0], ReferenceMm[1]);
        var mm = Math.Sqrt((ma.X - mb.X) * (ma.X - mb.X) + (ma.Y - mb.Y) * (ma.Y - mb.Y));
        var px = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        if (mm < 1 || px < 1) { Message = ""; return; }
        var sc = px / mm; Calibration = new(sc, sc);
        Offset = (ma.X - a.X / sc, ma.Y - (Height - a.Y) / sc);   // the first new point lands on the first reference point
        Message = $"{sc:0.####} px/mm";
    }

    /// <summary>Forgets the clicks of the current calibration (back to the reference picture when one is used).</summary>
    public void ResetCalibration() { ReferencePx.Clear(); ReferenceMm.Clear(); CalibrationPoints.Clear(); Calibration = null; Offset = (0, 0); }

    public int Width => PlanImport.Size(Image).Width;
    public int Height => PlanImport.Size(Image).Height;

    public void AutoDeskew() { Angle = Deskew.EstimateAngle(Gray!); Message = $"{Angle:0.0}°"; ApplyRotation(); }

    /// <summary>Rotates the original by -Angle (straightens a tilt of Angle) and redecodes.</summary>
    public void ApplyRotation()
    {
        Image = PlanImport.Rotate(Original, 90 * QuarterTurns - Angle); AppliedAngle = Angle; Gray = PlanImport.Decode(Image); CalibrationPoints.Clear(); Calibration = null;
    }

    /// <summary>Clockwise quarter turns (0-3) applied to the original before the fine angle.</summary>
    public int QuarterTurns { get; private set; }

    /// <summary>Turns the picture by 90° (+1 clockwise, -1 counter-clockwise), keeping the fine angle.</summary>
    public void RotateQuarter(int direction) { QuarterTurns = ((QuarterTurns + direction) % 4 + 4) % 4; ApplyRotation(); }

    /// <summary>Angle currently baked into <see cref="Image"/> (the canvas previews the difference live while the slider moves).</summary>
    public double AppliedAngle { get; private set; }

    /// <summary>Tilt (degrees, clockwise in image coordinates) of the line a→b relative to the nearest horizontal or vertical axis.</summary>
    public static double TiltOf((double X, double Y) a, (double X, double Y) b)
    {
        var theta = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI; return theta - 90 * Math.Round(theta / 90);
    }

    /// <summary>Rotates the picture so that the line a→b (two points along a wall) becomes exactly horizontal or vertical.</summary>
    public void AlignToLine((double X, double Y) a, (double X, double Y) b)
    {
        if (Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) < 2) return;
        Angle = TiltOf(a, b); Message = $"{Angle:0.00}°"; ApplyRotation();
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

    /// <summary>Calibration skipped: assumes 0.1 px/mm (10 mm per pixel, about a 1:100 scan at 250 dpi); the picture can be stretched to the right size afterwards.</summary>
    public void UseDefaultCalibration() { Calibration = new(0.1, 0.1); Offset = (0, 0); Message = "0.1 px/mm"; }

    /// <summary>Calibration from the printed scale (1:N) and the scan resolution.</summary>
    public void ApplyPrintedScale()
    {
        if (ScaleDenominator < 1 || Dpi < 1) return;
        var s = PlanImport.PxPerMmFromScale(Dpi, ScaleDenominator); Calibration = new(s, s); Offset = (0, 0); CalibrationPoints.Clear(); Message = $"{s:0.####} px/mm";
    }

    public bool IsCalibrated => Calibration != null;

    // labels with their values, in the current language (bound by the view)
    public string AngleLabel => Translations.T("wiz.angle", Angle.ToString("0.0"));
    public string ThresholdLabel => Translations.T("wiz.threshold", Threshold);
    public string MinThicknessLabel => Translations.T("wiz.minthick", MinThickness);
    public string BridgeLabel => Translations.T("wiz.bridge", BridgeGap);
    public string MinAreaLabel => Translations.T("wiz.minarea", MinArea);
    partial void OnAngleChanged(double value) => OnPropertyChanged(nameof(AngleLabel));
    partial void OnThresholdChanged(int value) => OnPropertyChanged(nameof(ThresholdLabel));
    partial void OnMinThicknessChanged(int value) => OnPropertyChanged(nameof(MinThicknessLabel));
    partial void OnBridgeGapChanged(int value) => OnPropertyChanged(nameof(BridgeLabel));
    partial void OnMinAreaChanged(int value) => OnPropertyChanged(nameof(MinAreaLabel));

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
        var shapes = keep.Select(w => ((IEnumerable<PointMm>)Move(PlanImport.ToMm(S(w.Outline), cal, Height)),
            (IEnumerable<IEnumerable<PointMm>>)w.Holes.Select(h => (IEnumerable<PointMm>)Move(PlanImport.ToMm(S(h), cal, Height))).ToList())).ToList();
        List<PointMm> Move(List<PointMm> o) => Offset == (0, 0) ? o : o.Select(p => new PointMm((int)Math.Round(p.X + Offset.X), (int)Math.Round(p.Y + Offset.Y))).ToList();
        var name = $"{level.Id:N}.png"; images[name] = Image;
        var m = PlanImport.TransformFor(cal, Height); m[2] += Offset.X; m[5] += Offset.Y;
        actions.SetBackground(level.Id, name, m);
        actions.ImportWallShapes(level.Id, shapes);
        return shapes.Count;
    }
}
