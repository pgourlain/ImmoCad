using Avalonia.Controls;
using ImmeubleCad.Core.Export;
using ImmeubleCad.Core.Model;

namespace ImmeubleCad.Desktop;

public sealed record ExportRequest(string Format, ExportOptions Options);

/// <summary>Which formats exist and what each is for (beginner friendly).</summary>
public static class ExportFormats
{
    public static string Extension(string id) => id switch { "ifc" => "*.ifc", "freecad" => "*.FCMacro", "dxf" => "*.dxf", "obj" => "*.obj", "glb" => "*.glb", "bom_pdf" => "*.pdf", "svg" => "*.svg", _ => "*.csv" };
    public static readonly (string Id, string Label, string Purpose)[] All =
    [
        ("ifc", "IFC 4 (.ifc)", "Échange BIM : Revit, ArchiCAD, FreeCAD (atelier BIM), BlenderBIM."),
        ("freecad", "Macro FreeCAD (.FCMacro)", "Reconstruit des murs et tuyaux modifiables dans FreeCAD."),
        ("dxf", "DXF (.dxf)", "Dessin 2D pour AutoCAD, LibreCAD, QCAD : un calque par niveau et par réseau."),
        ("glb", "glTF (.glb)", "Maquette 3D pour Blender et les visionneuses web (un nœud par niveau et par réseau)."),
        ("obj", "OBJ (.obj)", "Maillage 3D pour Blender, SketchUp, impression 3D."),
        ("svg", "Plan du niveau courant (.svg)", "Plan imprimable d'un niveau."),
        ("csv", "Nomenclature (.csv)", "Longueurs de tuyaux, raccords et équipements pour un tableur."),
        ("bom_pdf", "Nomenclature (.pdf)", "La même nomenclature, prête à imprimer."),
    ];
}

public partial class ExportDialog : Window
{
    private readonly List<(CheckBox Box, Guid Id)> _levels = new(), _networks = new();
    public ExportRequest? Result { get; private set; }

    public ExportDialog() => InitializeComponent();

    public ExportDialog(Building b, string? preselect = null) : this()
    {
        Formats.ItemsSource = ExportFormats.All.Select(f => f.Label).ToList(); Formats.SelectedIndex = Math.Max(0, Array.FindIndex(ExportFormats.All, f => f.Id == preselect));
        Formats.SelectionChanged += (_, _) => Purpose.Text = ExportFormats.All[Math.Max(0, Formats.SelectedIndex)].Purpose; Purpose.Text = ExportFormats.All[Math.Max(0, Formats.SelectedIndex)].Purpose;
        foreach (var l in b.Levels) { var cb = new CheckBox { Content = l.Name, IsChecked = true }; _levels.Add((cb, l.Id)); LevelBoxes.Items.Add(cb); }
        foreach (var n in b.Networks) { var cb = new CheckBox { Content = $"{n.Name} ({n.Type})", IsChecked = true }; _networks.Add((cb, n.Id)); NetworkBoxes.Items.Add(cb); }
        OkBtn.Click += (_, _) =>
        {
            Result = new ExportRequest(ExportFormats.All[Math.Max(0, Formats.SelectedIndex)].Id,
                new ExportOptions(_levels.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToHashSet(), _networks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToHashSet(), M.IsChecked == true));
            Close();
        };
        CancelBtn.Click += (_, _) => Close();
    }
}
