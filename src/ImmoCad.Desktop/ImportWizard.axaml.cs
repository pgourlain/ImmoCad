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

    public ImportWizard() { InitializeComponent(); }

    public ImportWizard(byte[] image, IReadOnlyList<WizardReference>? references = null) : this()
    {
        DataContext = new ImportWizardViewModel(image, references);
        RefBox.Items.Add(T("wiz.ref.none")); RefBox.SelectedIndex = 0;
        foreach (var r in Vm.References) RefBox.Items.Add(r);
        PRefPick.IsVisible = Vm.References.Count > 0;
        Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImportWizardViewModel.Angle)) { Canvas.PreviewDeg = -(Vm.Angle - Vm.AppliedAngle); Canvas.InvalidateVisual(); } };
        Canvas.SetImage(Vm.Image); Canvas.PixelClicked += OnPixel; Canvas.OutlineClicked += i => { Vm.Reject(i); Canvas.InvalidateVisual(); UpdateReview(); };
        ShowStep();
    }

    private void OnPixel((double X, double Y) p)
    {
        if (Vm.Step == WizardStep.Straighten)
        {   // two clicks along a wall: it becomes horizontal or vertical
            if (Canvas.RefPoints.Count >= 2) Canvas.RefPoints.Clear();
            Canvas.RefPoints.Add(p);
            if (Canvas.RefPoints.Count == 2) { var (a, b) = (Canvas.RefPoints[0], Canvas.RefPoints[1]); Canvas.RefPoints.Clear(); Vm.AlignToLine(a, b); Canvas.SetImage(Vm.Image, keepView: true); Help.Text = T("wiz.s1.help"); }
            else Help.Text = T("wiz.ref.points", 1);
            Canvas.InvalidateVisual(); return;
        }
        if (Vm.Step != WizardStep.Calibrate) return;
        var wasRef = Vm.ShowingReference; Vm.AddPoint(p.X, p.Y);
        if (wasRef && !Vm.ShowingReference) Canvas.SetImage(Vm.Image);   // both reference points set: now click the same features on the new plan
        ShowCalPoints(); UpdateCal();
    }

    private void ShowCalPoints()
    {
        Canvas.Points.Clear(); Canvas.Points.AddRange(Vm.ShowingReference ? Vm.ReferencePx : Vm.CalibrationPoints); Canvas.InvalidateVisual();
    }

    private void OnReferenceChanged(object? s, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        Vm.Reference = RefBox.SelectedItem as WizardReference; PManual.IsVisible = !Vm.UsesReference;
        ShowCalibrationImage(); ShowCalPoints(); UpdateCal();
    }

    /// <summary>The picture to click on: the reference level first, then the new plan.</summary>
    private void ShowCalibrationImage() => Canvas.SetImage(Vm.Step == WizardStep.Calibrate && Vm.ShowingReference ? Vm.Reference!.Image : Vm.Image);

    private void UpdateCal() { CalText.Text = Vm.IsCalibrated ? T("wiz.cal.ok", Vm.Calibration!.Value.PxPerMmX.ToString("0.####")) : Vm.UsesReference ? T(Vm.ShowingReference ? "wiz.ref.onref" : "wiz.ref.onnew", Vm.ShowingReference ? Vm.ReferenceMm.Count : Vm.CalibrationPoints.Count) : T("wiz.cal.points", Vm.CalibrationPoints.Count); NextBtn.IsEnabled = Vm.Step != WizardStep.Calibrate || Vm.IsCalibrated; }

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
        Canvas.RefPoints.Clear(); Canvas.Guides = GuidesBox.IsChecked == true && Vm.Step is WizardStep.Straighten or WizardStep.Calibrate;
        Canvas.SetImage(Vm.Step == WizardStep.Calibrate && Vm.ShowingReference ? Vm.Reference!.Image : Vm.Image, keepView: true);
        Canvas.Points.Clear(); if (Vm.Step == WizardStep.Calibrate) Canvas.Points.AddRange(Vm.ShowingReference ? Vm.ReferencePx : Vm.CalibrationPoints);
        if (Vm.Step == WizardStep.Detect) PreviewMask(); else Canvas.ClearMask();
        UpdateCal(); UpdateReview(); Canvas.InvalidateVisual();
    }

    private void PreviewMask() { var m = Vm.PreviewMask(); Canvas.SetMask(m, Vm.Gray!.Width, Vm.Gray.Height); }
    private void UpdateReview() => ReviewText.Text = T("wiz.review.count", Vm.Detected.Count - Vm.Rejected.Count, Vm.Detected.Count);

    private void OnAutoDeskew(object? s, RoutedEventArgs e) { Vm.AutoDeskew(); Canvas.SetImage(Vm.Image, keepView: true); }
    private void OnAngleChanged(object? s, Avalonia.Input.PointerReleasedEventArgs e) { Vm.ApplyRotation(); Canvas.SetImage(Vm.Image, keepView: true); }
    private void NudgeAngle(double delta) { Vm.Angle = Math.Round(Math.Clamp(Vm.Angle + delta, -45, 45), 1); Vm.ApplyRotation(); Canvas.SetImage(Vm.Image, keepView: true); }
    private void OnAngleDown(object? s, RoutedEventArgs e) => NudgeAngle(-0.1);
    private void OnAngleUp(object? s, RoutedEventArgs e) => NudgeAngle(0.1);
    private void OnZoomIn(object? s, RoutedEventArgs e) => Canvas.ZoomBy("in");
    private void OnZoomOut(object? s, RoutedEventArgs e) => Canvas.ZoomBy("out");
    private void OnFit(object? s, RoutedEventArgs e) => Canvas.ZoomBy("fit");
    private void OnGuides(object? s, RoutedEventArgs e) { Canvas.Guides = GuidesBox.IsChecked == true && Vm.Step is WizardStep.Straighten or WizardStep.Calibrate; Canvas.InvalidateVisual(); }
    private void OnAutoThickness(object? s, RoutedEventArgs e) { Vm.AutoThickness(); PreviewMask(); }
    private void OnDetectParam(object? s, Avalonia.Input.PointerReleasedEventArgs e) => PreviewMask();

    private void OnBack(object? s, RoutedEventArgs e) { Vm.Step = Vm.Step - 1; ShowStep(); }
    private void OnNext(object? s, RoutedEventArgs e)
    {
        try
        {
            if (Vm.Step == WizardStep.Review) { Accepted = true; Close(); return; }
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
