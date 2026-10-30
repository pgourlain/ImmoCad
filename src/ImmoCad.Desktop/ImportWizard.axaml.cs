using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

public partial class ImportWizard : Window
{
    private static readonly ILogger Log = App.Log.CreateLogger("ImportWizard");
    private ImportWizardViewModel Vm => (ImportWizardViewModel)DataContext!;
    public bool Accepted { get; private set; }

    private readonly Avalonia.Threading.DispatcherTimer _detectTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public ImportWizard() { InitializeComponent(); }

    public ImportWizard(byte[] image, IReadOnlyList<WizardReference>? references = null) : this()
    {
        DataContext = new ImportWizardViewModel(image, references);
        _detectTimer.Tick += (_, _) => { _detectTimer.Stop(); if (Vm.Step == WizardStep.Detect) PreviewMask(); };
        Vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ImportWizardViewModel.Angle): Canvas.PreviewDeg = -(Vm.Angle - Vm.AppliedAngle); Canvas.InvalidateVisual(); break;
                case nameof(ImportWizardViewModel.DistanceMetres): if (Vm.Step == WizardStep.Calibrate && Vm.CalibrationPoints.Count == 2) { Vm.ComputeCalibration(); UpdateCal(); } break;
                case nameof(ImportWizardViewModel.ScaleDenominator): if (Vm.Step == WizardStep.Calibrate) { Vm.ApplyPrintedScale(); UpdateCal(); } break;
                // detection is redone whenever a parameter moves (debounced while a slider is dragged)
                case nameof(ImportWizardViewModel.Threshold) or nameof(ImportWizardViewModel.MinThickness) or nameof(ImportWizardViewModel.BridgeGap) or nameof(ImportWizardViewModel.MinArea):
                    _detectTimer.Stop(); _detectTimer.Start(); break;
            }
        };
        Canvas.SetImage(Vm.Image); Canvas.PixelClicked += OnPixel; Canvas.OutlineClicked += i => { Vm.Reject(i); Canvas.InvalidateVisual(); UpdateReview(); };
        ShowStep();
    }

    private double _baseScale = 0.1;   // px/mm before the measure: the first two clicks show what the plan currently measures

    /// <summary>Measure to adjust: two clicks on a known distance, then the real distance is typed (the field starts at what the plan measures now).</summary>
    private void OnPixel((double X, double Y) p)
    {
        if (Vm.Step != WizardStep.Calibrate) return;
        var n = Vm.CalibrationPoints.Count;
        if (n != 1) _baseScale = Vm.Calibration?.PxPerMmX ?? _baseScale;
        if (n == 1) { var a = Vm.CalibrationPoints[0]; Vm.DistanceMetres = Math.Round(Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y)) / _baseScale / 1000, 2); }
        Vm.AddCalibrationPoint(p.X, p.Y);
        Canvas.Points.Clear(); Canvas.Points.AddRange(Vm.CalibrationPoints); Canvas.InvalidateVisual(); UpdateCal();
    }

    private void UpdateCal() { CalText.Text = Vm.IsCalibrated ? T("wiz.cal.ok", Vm.Calibration!.Value.PxPerMmX.ToString("0.####")) : ""; NextBtn.IsEnabled = true; }

    private void ShowStep()
    {
        PStraighten.IsVisible = Vm.Step == WizardStep.Straighten; PCalibrate.IsVisible = Vm.Step == WizardStep.Calibrate;
        PDetect.IsVisible = Vm.Step == WizardStep.Detect; PReview.IsVisible = Vm.Step == WizardStep.Review;
        (Title.Text, Help.Text) = Vm.Step switch
        {
            WizardStep.Straighten => (T("wiz.s1.title"), T("wiz.s1.help")),
            WizardStep.Calibrate => (T("wiz.s2.title"), T("wiz.s2.help")),
            WizardStep.Detect => (T("wiz.s3.title"), T("wiz.s3.help")),
            _ => (T("wiz.s4.title"), T("wiz.s4.help")),
        };
        NextBtn.Content = Vm.Step == WizardStep.Review ? T("wiz.confirm") : T("wiz.next"); BackBtn.IsEnabled = Vm.Step != WizardStep.Straighten;
        Canvas.Fade = Vm.Step == WizardStep.Detect ? 0.35 : 1; Canvas.Outlines = Vm.Step == WizardStep.Review ? Vm.Detected : null; Canvas.Rejected = Vm.Rejected;
        Canvas.RefPoints.Clear(); Canvas.Points.Clear(); Canvas.Guides = Vm.Step == WizardStep.Straighten;   // alignment guides: always on while straightening
        Canvas.SetImage(Vm.Image, keepView: true);
        if (Vm.Step == WizardStep.Calibrate) Vm.ApplyPrintedScale();
        if (Vm.Step == WizardStep.Detect) PreviewMask(); else Canvas.ClearMask();
        UpdateCal(); UpdateReview(); Canvas.InvalidateVisual();
    }

    private void PreviewMask() { var m = Vm.PreviewMask(); Canvas.SetMask(m, Vm.Gray!.Width, Vm.Gray.Height); }
    private void UpdateReview() => ReviewText.Text = T("wiz.review.count", Vm.Detected.Count - Vm.Rejected.Count, Vm.Detected.Count);

    private void OnAngleChanged(object? s, Avalonia.Input.PointerReleasedEventArgs e) { Vm.ApplyRotation(); Canvas.SetImage(Vm.Image, keepView: true); }
    private void NudgeAngle(double delta) { Vm.Angle = Math.Round(Math.Clamp(Vm.Angle + delta, -45, 45), 1); Vm.ApplyRotation(); Canvas.SetImage(Vm.Image, keepView: true); }
    private void OnTurnLeft(object? s, RoutedEventArgs e) { Vm.RotateQuarter(-1); Canvas.SetImage(Vm.Image, keepView: false); }
    private void OnTurnRight(object? s, RoutedEventArgs e) { Vm.RotateQuarter(1); Canvas.SetImage(Vm.Image, keepView: false); }
    private void OnAngleDown(object? s, RoutedEventArgs e) => NudgeAngle(-0.1);
    private void OnAngleUp(object? s, RoutedEventArgs e) => NudgeAngle(0.1);
    private void OnZoomIn(object? s, RoutedEventArgs e) => Canvas.ZoomBy("in");
    private void OnZoomOut(object? s, RoutedEventArgs e) => Canvas.ZoomBy("out");
    private void OnFit(object? s, RoutedEventArgs e) => Canvas.ZoomBy("fit");
    private void OnAutoThickness(object? s, RoutedEventArgs e) { Vm.AutoThickness(); PreviewMask(); }

    private void OnBack(object? s, RoutedEventArgs e) { Vm.Step = Vm.Step - 1; ShowStep(); }
    private void OnNext(object? s, RoutedEventArgs e)
    {
        try
        {
            if (Vm.Step == WizardStep.Review) { Accepted = true; Close(); return; }
            if (Vm.Step == WizardStep.Calibrate && !Vm.IsCalibrated) Vm.ApplyPrintedScale();
            if (Vm.Step == WizardStep.Detect) Vm.Detect();
            Vm.Step += 1; ShowStep();
        }
        catch (Exception ex) { Log.LogError(ex, "Wizard step failed"); Title.Text = T("error"); Help.Text = ex.Message; }
    }

    public ImportWizardViewModel Result => Vm;

    // test hooks (headless tests drive the wizard without a mouse)
    internal ImageCanvas Canvas_ForTest() => Canvas;
    internal void Next_ForTest() => OnNext(null, new RoutedEventArgs());
}
