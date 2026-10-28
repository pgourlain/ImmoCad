using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using static ImmeubleCad.Core.Translations;

namespace ImmeubleCad.Desktop;

/// <summary>One menu entry, defined once and rendered as a NativeMenu (macOS) or an in-window Menu (Windows/Linux).</summary>
public sealed record MenuItemSpec(string Header, ICommand? Command = null, object? Parameter = null, Key Key = Key.None, KeyModifiers Extra = KeyModifiers.None, bool UseCommandKey = false,
    IReadOnlyList<MenuItemSpec>? Children = null, bool IsSeparator = false)
{
    public static MenuItemSpec Separator { get; } = new("-", IsSeparator: true);
}
public sealed record MenuSpec(string Header, IReadOnlyList<MenuItemSpec> Items);

public static class MenuFactory
{
    private static ICommand NotYet { get; } = new NotYetImplementedCommand();

    /// <summary>⌘ on macOS, Ctrl elsewhere (PlatformHotkeyConfiguration.CommandModifiers).</summary>
    public static KeyModifiers CommandModifiers => Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    public static IReadOnlyList<MenuSpec> Build(MainViewModel vm, Action openLogs, Action copyInfo, Action about)
    {
        var recent = vm.RecentFiles.Count == 0
            ? new List<MenuItemSpec> { new("(vide)") }
            : vm.RecentFiles.Select(f => new MenuItemSpec(Path.GetFileName(f), vm.OpenRecentCommand, f)).ToList();
        return new MenuSpec[]
        {
            new(T("menu.file"),
            [
                new("Nouveau", vm.NewDocumentCommand, Key: Key.N, UseCommandKey: true),
                new("Ouvrir…", vm.OpenCommand, Key: Key.O, UseCommandKey: true),
                new("Ouvrir récent", Children: recent),
                new("Ouvrir l'exemple", vm.OpenSampleCommand),
                MenuItemSpec.Separator,
                new("Enregistrer", vm.SaveCommand, Key: Key.S, UseCommandKey: true),
                new("Enregistrer sous…", vm.SaveAsCommand, Key: Key.S, Extra: KeyModifiers.Shift, UseCommandKey: true),
                new("Fermer", vm.CloseTabCommand, -1, Key: Key.W, UseCommandKey: true),
                MenuItemSpec.Separator,
                new("Importer un plan (JPG/PNG)…", vm.ImportPlanCommand),
                new("Exporter…", vm.ExportCommand, "ask", Key: Key.E, UseCommandKey: true),
                new("Export rapide",
                    Children:
                    [
                        new("IFC", vm.ExportCommand, "ifc"), new("Macro FreeCAD", vm.ExportCommand, "freecad"), new("DXF", vm.ExportCommand, "dxf"),
                        new("glTF (.glb)", vm.ExportCommand, "glb"), new("OBJ", vm.ExportCommand, "obj"), new("Plan du niveau (SVG)", vm.ExportCommand, "svg"),
                        new("Nomenclature (CSV)", vm.ExportCommand, "csv"), new("Nomenclature (PDF)", vm.ExportCommand, "bom_pdf"),
                    ]),
            ]),
            new(T("menu.edit"),
            [
                new("Annuler", vm.UndoCommand, Key: Key.Z, UseCommandKey: true),
                new("Rétablir", vm.RedoCommand, Key: Key.Z, Extra: KeyModifiers.Shift, UseCommandKey: true),
                MenuItemSpec.Separator,
                new("Tout sélectionner", vm.SelectAllCommand, Key: Key.A, UseCommandKey: true),
                new("Supprimer la sélection", vm.DeleteSelectionCommand),
                new("Copier la sélection sur tous les niveaux", vm.CopySelectionToAllLevelsCommand),
            ]),
            new(T("menu.level"),
            [
                new("Ajouter un niveau", vm.AddLevelCommand),
                new("Sélection → étage au-dessus", vm.MoveSelectionUpCommand, Key: Key.PageUp, UseCommandKey: true),
                new("Sélection → étage en dessous", vm.MoveSelectionDownCommand, Key: Key.PageDown, UseCommandKey: true),
                MenuItemSpec.Separator,
                new("Aligner sur le niveau du dessous…", vm.SetToolCommand, "Align"),
                new("Redresser les murs sélectionnés", vm.StraightenSelectionCommand),
            ]),
            new(T("menu.view"),
            [
                new("Plan 2D", vm.SetModeCommand, "Plan", Key: Key.D2, UseCommandKey: true),
                new("Élévation du mur sélectionné", vm.ShowElevationCommand),
                new("3D plein", vm.SetModeCommand, "Solid", Key: Key.D3, UseCommandKey: true),
                new("3D filaire", vm.SetModeCommand, "Wireframe"),
                MenuItemSpec.Separator,
                new("Niveau du dessous en fantôme", vm.ToggleGhostBelowCommand),
                new("Niveau du dessus en fantôme", vm.ToggleGhostAboveCommand),
                new("Parties communes seulement", vm.ToggleCommonOnlyCommand),
            ]),
            new(T("menu.help"),
            [
                new("Ouvrir le dossier des journaux", new RelayAction(openLogs)),
                new("Copier les informations système", new RelayAction(copyInfo)),
                new("À propos", new RelayAction(about)),
                new("Visite guidée (bientôt)", NotYet),
            ]),
        };
    }

    private static KeyGesture? Gesture(MenuItemSpec i) => i.Key == Key.None ? null : new KeyGesture(i.Key, (i.UseCommandKey ? CommandModifiers : KeyModifiers.None) | i.Extra);

    public static Menu ToMenu(IReadOnlyList<MenuSpec> specs)
    {
        var menu = new Menu();
        foreach (var s in specs) menu.Items.Add(new MenuItem { Header = s.Header, ItemsSource = s.Items.Select(ToItem).ToList() });
        return menu;
    }
    private static Control ToItem(MenuItemSpec i)
    {
        if (i.IsSeparator) return new Separator();
        var m = new MenuItem { Header = i.Header, Command = i.Command, CommandParameter = i.Parameter, HotKey = Gesture(i) };
        if (i.Children != null) m.ItemsSource = i.Children.Select(ToItem).ToList();
        if (i.Command == null && i.Children == null) m.IsEnabled = false;
        return m;
    }

    public static NativeMenu ToNativeMenu(IReadOnlyList<MenuSpec> specs)
    {
        var menu = new NativeMenu();
        foreach (var s in specs) { var top = new NativeMenuItem(s.Header.Replace("_", "")) { Menu = new NativeMenu() }; foreach (var i in s.Items) top.Menu!.Items.Add(ToNative(i)); menu.Items.Add(top); }
        return menu;
    }
    private static NativeMenuItemBase ToNative(MenuItemSpec i)
    {
        if (i.IsSeparator) return new NativeMenuItemSeparator();
        var m = new NativeMenuItem(i.Header) { Command = i.Command, CommandParameter = i.Parameter, Gesture = Gesture(i) };
        if (i.Children != null) { m.Menu = new NativeMenu(); foreach (var c in i.Children) m.Menu.Items.Add(ToNative(c)); }
        return m;
    }
}

public sealed class NotYetImplementedCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => false;
    public void Execute(object? parameter) { }
}
public sealed class RelayAction(Action a) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => a();
}
