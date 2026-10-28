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

    public ImportWizard(byte[] image) : this()
    {
        DataContext = new ImportWizardViewModel(image);
        Canvas.SetImage(Vm.Image); Canvas.PixelClicked += OnPixel; Canvas.OutlineClicked += i => { Vm.Reject(i); Canvas.InvalidateVisual(); UpdateReview(); };
        ShowStep();
    }

    private void OnPixel((double X, double Y) p)
    {
        if (Vm.Step != WizardStep.Calibrate) return;
        Vm.AddCalibrationPoint(p.X, p.Y); Canvas.Points.Clear(); Canvas.Points.AddRange(Vm.CalibrationPoints); Canvas.InvalidateVisual(); UpdateCal();
    }

    private void UpdateCal() { CalText.Text = Vm.IsCalibrated ? T("wiz.cal.ok", Vm.Calibration!.Value.PxPerMmX.ToString("0.####")) : T("wiz.cal.points", Vm.CalibrationPoints.Count); NextBtn.IsEnabled = Vm.Step != WizardStep.Calibrate || Vm.IsCalibrated; }

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
        Canvas.Points.Clear(); if (Vm.Step == WizardStep.Calibrate) Canvas.Points.AddRange(Vm.CalibrationPoints);
        if (Vm.Step == WizardStep.Detect) PreviewMask(); else Canvas.ClearMask();
        UpdateCal(); UpdateReview(); Canvas.InvalidateVisual();
    }

    private void PreviewMask() { var m = Vm.PreviewMask(); Canvas.SetMask(m, Vm.Gray!.Width, Vm.Gray.Height); }
    private void UpdateReview() => ReviewText.Text = T("wiz.review.count", Vm.Detected.Count - Vm.Rejected.Count, Vm.Detected.Count);

    private void OnAutoDeskew(object? s, RoutedEventArgs e) { Vm.AutoDeskew(); Canvas.SetImage(Vm.Image); }
    private void OnAngleChanged(object? s, Avalonia.Input.PointerReleasedEventArgs e) { Vm.ApplyRotation(); Canvas.SetImage(Vm.Image); }
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
