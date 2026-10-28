using static ImmeubleCad.Core.Translations;
using Avalonia.Controls;
using ImmeubleCad.Core.Export;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Desktop;

public sealed record ExportRequest(string Format, ExportOptions Options);

/// <summary>Which formats exist and what each is for (beginner friendly).</summary>
public static class ExportFormats
{
    public static string Extension(string id) => id switch { "ifc" => "*.ifc", "freecad" => "*.FCMacro", "dxf" => "*.dxf", "obj" => "*.obj", "glb" => "*.glb", "bom_pdf" => "*.pdf", "svg" => "*.svg", _ => "*.csv" };
    public static readonly string[] Ids = ["ifc", "freecad", "dxf", "glb", "obj", "svg", "csv", "bom_pdf"];
    public static string Label(string id) => ImmeubleCad.Core.Translations.T("export.fmt." + id);
    public static string Purpose(string id) => ImmeubleCad.Core.Translations.T("export.pur." + id);
}

public partial class ExportDialog : Window
{
    private readonly List<(CheckBox Box, Guid Id)> _levels = new(), _networks = new();
    public ExportRequest? Result { get; private set; }

    public ExportDialog() => InitializeComponent();

    public ExportDialog(Building b, string? preselect = null) : this()
    {
        Formats.ItemsSource = ExportFormats.Ids.Select(ExportFormats.Label).ToList(); Formats.SelectedIndex = Math.Max(0, Array.IndexOf(ExportFormats.Ids, preselect));
        Formats.SelectionChanged += (_, _) => Purpose.Text = ExportFormats.Purpose(ExportFormats.Ids[Math.Max(0, Formats.SelectedIndex)]); Purpose.Text = ExportFormats.Purpose(ExportFormats.Ids[Math.Max(0, Formats.SelectedIndex)]);
        foreach (var l in b.Levels) { var cb = new CheckBox { Content = l.DisplayName, IsChecked = true }; _levels.Add((cb, l.Id)); LevelBoxes.Items.Add(cb); }
        foreach (var n in b.Networks) { var cb = new CheckBox { Content = $"{n.Name} ({n.Type})", IsChecked = true }; _networks.Add((cb, n.Id)); NetworkBoxes.Items.Add(cb); }
        OkBtn.Click += (_, _) =>
        {
            Result = new ExportRequest(ExportFormats.Ids[Math.Max(0, Formats.SelectedIndex)],
                new ExportOptions(_levels.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToHashSet(), _networks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToHashSet(), M.IsChecked == true));
            Close();
        };
        CancelBtn.Click += (_, _) => Close();
    }
}
