using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmoCad.Core;
using ImmoCad.Core.Catalog;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>The guided path through a project: each step shows only the tools and the panel it needs.</summary>
public enum WorkStep { Plan, Rooms, Equip, Connect, Result }

public sealed record StepItem(WorkStep Step, int Number, string Label, bool IsCurrent, bool IsDone)
{
    public string Key => Step.ToString();
}

/// <summary>One entry of the command palette (⌘K): a menu command, a tool, a step or an object of the catalog.</summary>
public sealed record CommandEntry(string Title, string Group, Action Run);

public partial class MainViewModel
{
    // ---- Guided steps ----
    [ObservableProperty] private WorkStep _step = WorkStep.Plan;
    /// <summary>Index of the side panel tab (Palette, Options, Networks, History, Site, Spaces, Properties); each step opens its own.</summary>
    [ObservableProperty] private int _panelTab = 6;
    /// <summary>Expert mode: every tool and every action stays visible whatever the step (remembered in the settings).</summary>
    [ObservableProperty] private bool _expertMode;
    public ObservableCollection<StepItem> Steps { get; } = new();

    [RelayCommand] private void SetStep(string step) { var s = Enum.Parse<WorkStep>(step); if (s == Step) OnStepChanged(s); else Step = s; }   // clicking the current step opens its panel again
    [RelayCommand] private void NextStep() { if (Step < WorkStep.Result) Step++; }
    [RelayCommand] private void ToggleExpert() => ExpertMode = !ExpertMode;

    partial void OnStepChanged(WorkStep value)
    {
        Tool = ToolKind.Select;
        PanelTab = value switch { WorkStep.Plan => 6, WorkStep.Rooms => 5, WorkStep.Equip => 0, WorkStep.Connect => 2, _ => 3 };
        if (value == WorkStep.Result) RequestMode(ViewMode.Solid); else if (Mode != ViewMode.Plan) RequestMode(ViewMode.Plan);
        Hint = T("step.hint." + value.ToString().ToLowerInvariant());
        RebuildSteps(); NotifyToolGroups(); RebuildPalette();
    }

    partial void OnExpertModeChanged(bool value)
    {
        Settings.ExpertMode = value; if (SettingsPath != null) try { SettingsStore.Save(Settings, SettingsPath); } catch (IOException) { }
        NotifyToolGroups();
    }

    private void RebuildSteps()
    {
        Steps.Clear();
        foreach (var s in Enum.GetValues<WorkStep>())
            Steps.Add(new StepItem(s, (int)s + 1, T("step." + s.ToString().ToLowerInvariant()), s == Step, s < Step));
        OnPropertyChanged(nameof(NextStepLabel)); OnPropertyChanged(nameof(HasNextStep));
    }

    public bool HasNextStep => Step < WorkStep.Result;
    public string NextStepLabel => HasNextStep ? T("step.next", T("step." + (Step + 1).ToString().ToLowerInvariant())) : "";

    // which tool groups the strip shows: by step, or all of them in expert mode
    public bool ShowPlanTools => ExpertMode || Step == WorkStep.Plan;
    public bool ShowWallTools => ExpertMode || Step is WorkStep.Plan or WorkStep.Rooms;
    public bool ShowRoomTools => ExpertMode || Step == WorkStep.Rooms;
    public bool ShowRiserTool => ExpertMode || Step is WorkStep.Equip or WorkStep.Connect;
    public bool ShowPipeTool => ExpertMode || Step == WorkStep.Connect;
    public bool ShowExpertActions => ExpertMode;
    // sections of the Properties panel: only the ones the selected object has (the background picture when nothing is selected)
    public bool ShowWallProps => WallsSelected;
    public bool ShowPipeProps => SelectedSegment != null;
    public bool ShowEquipmentProps => EquipmentSelected;
    public bool ShowBackgroundProps => !HasSelection;
    private void NotifyToolGroups()
    {
        foreach (var n in new[] { nameof(ShowPlanTools), nameof(ShowWallTools), nameof(ShowRoomTools), nameof(ShowRiserTool), nameof(ShowPipeTool), nameof(ShowExpertActions),
                                  nameof(ShowWallProps), nameof(ShowPipeProps), nameof(ShowEquipmentProps), nameof(ShowBackgroundProps) }) OnPropertyChanged(n);
    }

    // ---- Action bar on the selection ----
    /// <summary>The actions on the selection edit the building: not offered while walking, which is read-only.</summary>
    public bool ShowSelectionActions => HasSelection && NotWalk;
    public bool HasSelection => SelectedWalls.Count > 0 || SelectedSegment != null || SelectedEquipmentId != null || SelectedSpaceId != null || SelectedOpening != null;
    /// <summary>Walls are selected (the selection set holds walls and floor markings together: same click, same box, same Delete).</summary>
    public bool WallsSelected => SelectedWalls.Count > 0 && SelectedOpening == null && CurrentLevel is { } wl && wl.Walls.Any(w => SelectedWalls.Contains(w.Id));
    public bool MarkingsSelected => SelectedWalls.Count > 0 && SelectedOpening == null && CurrentLevel is { } ml && ml.Markings.Any(m => SelectedWalls.Contains(m.Id));
    private string WallsAndMarkingsText()
    {
        var l = CurrentLevel; var n = l?.Walls.Count(w => SelectedWalls.Contains(w.Id)) ?? 0; var m = l?.Markings.Count(x => SelectedWalls.Contains(x.Id)) ?? 0;
        return m == 0 ? T("walls.selected", Math.Max(n, SelectedWalls.Count)) : n == 0 ? T("markings.selected", m) : T("walls.markings.selected", n, m);
    }
    public bool EquipmentSelected => SelectedEquipmentId != null;
    [RelayCommand] private void ShowProperties() => PanelTab = 6;
    [RelayCommand] private void RotateSelectedEquipment() { if (SelectedEquipmentId == null) return; EqRotation = (EqRotation + 90) % 360; ApplyEquipmentCommand.Execute(null); }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SelectionText))
            foreach (var n in new[] { nameof(HasSelection), nameof(ShowSelectionActions), nameof(WallsSelected), nameof(MarkingsSelected), nameof(EquipmentSelected), nameof(ShowWallProps), nameof(ShowPipeProps), nameof(ShowEquipmentProps), nameof(ShowBackgroundProps) })
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    // ---- Command palette (⌘K) ----
    [ObservableProperty] private bool _commandPaletteOpen;
    [ObservableProperty] private string _commandQuery = "";
    [ObservableProperty] private int _commandIndex;
    public ObservableCollection<CommandEntry> CommandResults { get; } = new();
    /// <summary>Set by the window: the menu commands (the palette also lists tools, steps and the catalog itself).</summary>
    public Func<IEnumerable<CommandEntry>>? MenuCommands { get; set; }

    [RelayCommand] private void OpenCommandPalette() { CommandQuery = ""; CommandPaletteOpen = true; SearchCommands(); }
    [RelayCommand] private void CloseCommandPalette() => CommandPaletteOpen = false;
    partial void OnCommandQueryChanged(string value) => SearchCommands();

    public IEnumerable<CommandEntry> AllCommands()
    {
        foreach (var s in Enum.GetValues<WorkStep>()) { var k = s; yield return new(T("step." + s.ToString().ToLowerInvariant()), T("cmd.group.step"), () => Step = k); }
        foreach (var t in Enum.GetValues<ToolKind>().Where(t => t != ToolKind.Place)) { var k = t; yield return new(T("toolname." + t), T("cmd.group.tool"), () => Tool = k); }
        foreach (var e in EquipmentCatalog.All) { var id = e.Id; yield return new(e.Name, T("cmd.group.object"), () => SelectPaletteCommand.Execute(id)); }
        yield return new(T("tip.expert"), T("cmd.group.view"), () => ExpertMode = !ExpertMode);
        foreach (var c in MenuCommands?.Invoke() ?? []) yield return c;
    }

    /// <summary>Accent- and case-insensitive search; titles starting with the query come first.</summary>
    public void SearchCommands()
    {
        CommandResults.Clear(); var q = Fold(CommandQuery.Trim());
        var hits = AllCommands().Select(c => (c, f: Fold(c.Title)))
            .Where(x => q.Length == 0 || q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => x.f.Contains(w)))
            .OrderBy(x => q.Length > 0 && x.f.StartsWith(q) ? 0 : 1).ThenBy(x => x.c.Title, StringComparer.CurrentCulture)
            .DistinctBy(x => x.c.Title + "|" + x.c.Group).Take(12);
        foreach (var (c, _) in hits) CommandResults.Add(c);
        CommandIndex = 0;
    }

    [RelayCommand] private void RunCommand(CommandEntry? entry)
    {
        entry ??= CommandIndex >= 0 && CommandIndex < CommandResults.Count ? CommandResults[CommandIndex] : null;
        if (entry == null) return;
        CommandPaletteOpen = false; entry.Run();
    }
    public void MoveCommandIndex(int delta) { if (CommandResults.Count > 0) CommandIndex = Math.Clamp(CommandIndex + delta, 0, CommandResults.Count - 1); }

    private static string Fold(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD)) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Flattens menu entries (sub-menus included) into palette entries, the menu name as group.</summary>
    public static IEnumerable<CommandEntry> FromMenus(IEnumerable<MenuSpec> menus)
    {
        IEnumerable<CommandEntry> Walk(string group, IEnumerable<MenuItemSpec> items)
        {
            foreach (var i in items)
            {
                if (i.IsSeparator) continue;
                if (i.Children != null) { foreach (var c in Walk(group, i.Children)) yield return c; continue; }
                if (i.Command is ICommand cmd) { var p = i.Parameter; yield return new(i.Header, group, () => { if (cmd.CanExecute(p)) cmd.Execute(p); }); }
            }
        }
        return menus.SelectMany(m => Walk(m.Header, m.Items));
    }
}
