using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;

namespace ImmeubleCad.Desktop;

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

    private void UpdateCal() { CalText.Text = Vm.IsCalibrated ? $"✓ {Vm.Calibration!.Value.PxPerMmX:0.####} px/mm" : $"{Vm.CalibrationPoints.Count} point(s) — cliquez sur le plan"; NextBtn.IsEnabled = Vm.Step != WizardStep.Calibrate || Vm.IsCalibrated; }

    private void ShowStep()
    {
        PStraighten.IsVisible = Vm.Step == WizardStep.Straighten; PCalibrate.IsVisible = Vm.Step == WizardStep.Calibrate;
        PDetect.IsVisible = Vm.Step == WizardStep.Detect; PReview.IsVisible = Vm.Step == WizardStep.Review;
        (Title.Text, Help.Text) = Vm.Step switch
        {
            WizardStep.Straighten => ("1. Redresser", "Si le plan est penché, redressez-le automatiquement ou avec le curseur."),
            WizardStep.Calibrate => ("2. Échelle", "Cliquez deux points dont vous connaissez la distance réelle, puis saisissez-la."),
            WizardStep.Detect => ("3. Détection", "Réglez jusqu'à ce que les murs soient en rouge et le texte absent."),
            _ => ("4. Vérification", "Cliquez un mur détecté pour l'exclure. Valider ajoute tout en une seule étape annulable."),
        };
        NextBtn.Content = Vm.Step == WizardStep.Review ? "Valider" : "Suivant"; BackBtn.IsEnabled = Vm.Step != WizardStep.Straighten;
        Canvas.Fade = Vm.Step == WizardStep.Detect ? 0.35 : 1; Canvas.Outlines = Vm.Step == WizardStep.Review ? Vm.Detected : null; Canvas.Rejected = Vm.Rejected;
        Canvas.Points.Clear(); if (Vm.Step == WizardStep.Calibrate) Canvas.Points.AddRange(Vm.CalibrationPoints);
        if (Vm.Step == WizardStep.Detect) PreviewMask(); else Canvas.ClearMask();
        UpdateCal(); UpdateReview(); Canvas.InvalidateVisual();
    }

    private void PreviewMask() { var m = Vm.PreviewMask(); Canvas.SetMask(m, Vm.Gray!.Width, Vm.Gray.Height); }
    private void UpdateReview() => ReviewText.Text = $"{Vm.Detected.Count - Vm.Rejected.Count} groupe(s) de murs retenu(s) sur {Vm.Detected.Count}";

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
        catch (Exception ex) { Log.LogError(ex, "Wizard step failed"); Title.Text = "Erreur"; Help.Text = ex.Message; }
    }

    public ImportWizardViewModel Result => Vm;

    // test hooks (headless tests drive the wizard without a mouse)
    internal ImageCanvas Canvas_ForTest() => Canvas;
    internal void Next_ForTest() => OnNext(null, new RoutedEventArgs());
}
