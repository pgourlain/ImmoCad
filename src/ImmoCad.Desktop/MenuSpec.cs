using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

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
            ? new List<MenuItemSpec> { new(T("menu.empty")) }
            : vm.RecentFiles.Select(f => new MenuItemSpec(Path.GetFileName(f), vm.OpenRecentCommand, f)).ToList();
        return new MenuSpec[]
        {
            new(T("menu.file"),
            [
                new(T("menu.new"), vm.NewDocumentCommand, Key: Key.N, UseCommandKey: true),
                new(T("menu.open"), vm.OpenCommand, Key: Key.O, UseCommandKey: true),
                new(T("menu.openrecent"), Children: recent),
                new(T("welcome.sample"), vm.OpenSampleCommand),
                MenuItemSpec.Separator,
                new(T("menu.save"), vm.SaveCommand, Key: Key.S, UseCommandKey: true),
                new(T("menu.saveas"), vm.SaveAsCommand, Key: Key.S, Extra: KeyModifiers.Shift, UseCommandKey: true),
                new(T("menu.close"), vm.CloseTabCommand, -1, Key: Key.W, UseCommandKey: true),
                MenuItemSpec.Separator,
                new(T("menu.importplan"), vm.ImportPlanCommand),
                new(T("menu.importifc"), vm.ImportIfcCommand),
                new(T("menu.importref"), vm.ImportReferenceCommand),
                new(T("export.go"), vm.ExportCommand, "ask", Key: Key.E, UseCommandKey: true),
                new(T("menu.quickexport"),
                    Children:
                    [
                        new("IFC", vm.ExportCommand, "ifc"), new(T("menu.fmt.freecad"), vm.ExportCommand, "freecad"), new("DXF", vm.ExportCommand, "dxf"),
                        new("glTF (.glb)", vm.ExportCommand, "glb"), new("OBJ", vm.ExportCommand, "obj"), new(T("menu.fmt.svg"), vm.ExportCommand, "svg"),
                        new(T("menu.fmt.csv"), vm.ExportCommand, "csv"), new(T("menu.fmt.pdf"), vm.ExportCommand, "bom_pdf"),
                    ]),
            ]),
            new(T("menu.edit"),
            [
                new(T("tip.undo"), vm.UndoCommand, Key: Key.Z, UseCommandKey: true),
                new(T("tip.redo"), vm.RedoCommand, Key: Key.Z, Extra: KeyModifiers.Shift, UseCommandKey: true),
                MenuItemSpec.Separator,
                new(T("menu.cut"), vm.CutCommand, Key: Key.X, UseCommandKey: true),
                new(T("menu.copy"), vm.CopyCommand, Key: Key.C, UseCommandKey: true),
                new(T("menu.paste"), vm.PasteCommand, Key: Key.V, UseCommandKey: true),
                new(T("menu.duplicate"), vm.DuplicateCommand, Key: Key.D, UseCommandKey: true),
                new(T("menu.selectall"), vm.SelectAllCommand, Key: Key.A, UseCommandKey: true),
                new(T("menu.deletesel"), vm.DeleteSelectionCommand),
                new(T("tip.copysel"), vm.CopySelectionToAllLevelsCommand),
            ]),
            new(T("menu.level"),
            [
                new(T("menu.addlevel"), vm.AddLevelCommand),
                new(T("menu.sel.up"), vm.MoveSelectionUpCommand, Key: Key.PageUp, UseCommandKey: true),
                new(T("menu.sel.down"), vm.MoveSelectionDownCommand, Key: Key.PageDown, UseCommandKey: true),
                MenuItemSpec.Separator,
                new(T("menu.align"), vm.SetToolCommand, "Align"),
                new(T("menu.straighten"), vm.StraightenSelectionCommand),
            ]),
            new(T("menu.view"),
            [
                new(T("menu.view.plan"), vm.SetModeCommand, "Plan", Key: Key.D2, UseCommandKey: true),
                new(T("menu.zoomin"), vm.ZoomPlanCommand, "in", Key: Key.OemPlus, UseCommandKey: true),
                new(T("menu.zoomout"), vm.ZoomPlanCommand, "out", Key: Key.OemMinus, UseCommandKey: true),
                new(T("menu.zoomfit"), vm.ZoomPlanCommand, "fit", Key: Key.D0, UseCommandKey: true),
                new(T("menu.view.elev"), vm.ShowElevationCommand),
                new(T("menu.view.solid"), vm.SetModeCommand, "Solid", Key: Key.D3, UseCommandKey: true),
                new(T("menu.view.wire"), vm.SetModeCommand, "Wireframe"),
                MenuItemSpec.Separator,
                new(T("menu.view.language"), Children: [new(T("lang.en"), vm.SetLanguageCommand, "en"), new(T("lang.fr"), vm.SetLanguageCommand, "fr")]),
                new(T("menu.theme"), Children: [new(T("menu.theme.auto"), vm.SetThemeCommand, "auto"), new(T("menu.theme.light"), vm.SetThemeCommand, "light"), new(T("menu.theme.dark"), vm.SetThemeCommand, "dark")]),
                MenuItemSpec.Separator,
                new(T("menu.snapgrid"), vm.ToggleSnapToGridCommand, Key: Key.F9),
                new(T("menu.northup"), vm.ToggleNorthUpCommand),
                new(T("menu.ghost.below"), vm.ToggleGhostBelowCommand),
                new(T("menu.ghost.above"), vm.ToggleGhostAboveCommand),
                new(T("commononly"), vm.ToggleCommonOnlyCommand),
            ]),
            new(T("menu.help"),
            [
                new(T("menu.openlogs"), new RelayAction(openLogs)),
                new(T("menu.copyinfo"), new RelayAction(copyInfo)),
                new(T("about.title"), new RelayAction(about)),
                new(T("menu.tour"), vm.StartTourCommand),
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
