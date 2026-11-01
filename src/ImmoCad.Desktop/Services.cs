using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ImmoCad.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ImmoCad.Desktop;

public enum SaveChoice { Save, Discard, Cancel }

/// <summary>Everything the view-model needs from the UI: file pickers, prompts, errors, the import wizard.</summary>
public interface IDialogService
{
    Task<string?> PickOpenAsync(string title, string pattern);
    Task<string?> PickSaveAsync(string title, string pattern, string? suggestedName = null);
    Task<SaveChoice> ConfirmSaveAsync(string documentName);
    Task ShowErrorAsync(string title, string message);
    Task<ImportWizardViewModel?> RunImportWizardAsync(byte[] image, IReadOnlyList<WizardReference>? references = null);
    /// <summary>Single text input; null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string label, string initial);
    /// <summary>Export dialog: format, levels, networks, units. Null when cancelled.</summary>
    Task<ExportRequest?> ShowExportDialogAsync(ImmoCad.Core.Model.Building building, string? preselect = null);
}

/// <summary>Scripted answers for tests; records what was asked.</summary>
public sealed class FakeDialogService : IDialogService
{
    public Queue<string?> OpenAnswers { get; } = new(); public Queue<string?> SaveAnswers { get; } = new(); public Queue<SaveChoice> SaveChoices { get; } = new();
    public List<string> Errors { get; } = new(); public List<string> Asked { get; } = new();
    public Func<byte[], ImportWizardViewModel?>? Wizard { get; set; }
    public Task<string?> PickOpenAsync(string title, string pattern) { Asked.Add("open:" + title); return Task.FromResult(OpenAnswers.Count > 0 ? OpenAnswers.Dequeue() : null); }
    public Task<string?> PickSaveAsync(string title, string pattern, string? suggestedName = null) { Asked.Add("save:" + title); return Task.FromResult(SaveAnswers.Count > 0 ? SaveAnswers.Dequeue() : null); }
    public Task<SaveChoice> ConfirmSaveAsync(string documentName) { Asked.Add("confirm:" + documentName); return Task.FromResult(SaveChoices.Count > 0 ? SaveChoices.Dequeue() : SaveChoice.Cancel); }
    public Task ShowErrorAsync(string title, string message) { Errors.Add($"{title}: {message}"); return Task.CompletedTask; }
    public Queue<ExportRequest?> ExportAnswers { get; } = new();
    public Task<ExportRequest?> ShowExportDialogAsync(ImmoCad.Core.Model.Building building, string? preselect = null) { Asked.Add("export-dialog"); return Task.FromResult(ExportAnswers.Count > 0 ? ExportAnswers.Dequeue() : null); }
    public Queue<string?> PromptAnswers { get; } = new();
    public Task<string?> PromptAsync(string title, string label, string initial) { Asked.Add("prompt:" + title); return Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null); }
    public Task<ImportWizardViewModel?> RunImportWizardAsync(byte[] image, IReadOnlyList<WizardReference>? references = null) => Task.FromResult(Wizard?.Invoke(image));
}

public sealed class AvaloniaDialogService(Window owner) : IDialogService
{
    public async Task<string?> PickOpenAsync(string title, string pattern)
    {
        var types = pattern == "image" ? new[] { FilePickerFileTypes.ImageAll } : new[] { new FilePickerFileType(title) { Patterns = new[] { pattern } } };
        return (await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, FileTypeFilter = types })).FirstOrDefault()?.Path.LocalPath;
    }
    public async Task<string?> PickSaveAsync(string title, string pattern, string? suggestedName = null) =>
        (await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, DefaultExtension = pattern.TrimStart('*'), FileTypeChoices = new[] { new FilePickerFileType(title) { Patterns = new[] { pattern } } } }))?.Path.LocalPath;

    public async Task<SaveChoice> ConfirmSaveAsync(string documentName)
    {
        var result = SaveChoice.Cancel;
        var w = new Window { Title = "ImmoCad", Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        Button Make(string key, SaveChoice choice, bool primary = false, bool cancel = false)
        {
            var b = new Button { Content = ImmoCad.Core.Translations.T(key), MinWidth = 96, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsDefault = primary, IsCancel = cancel };
            if (primary) b.Classes.Add("accent");
            b.Click += (_, _) => { result = choice; w.Close(); }; return b;
        }
        var text = new StackPanel { Spacing = 6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = ImmoCad.Core.Translations.T("dialog.save.changes", documentName), FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 15, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = ImmoCad.Core.Translations.T("dialog.save.lost"), Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        } };
        var head = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 16 };
        head.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ImmoCad/Assets/icon.png"))), Width = 48, Height = 48, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top });
        Grid.SetColumn(text, 1); head.Children.Add(text);
        var buttons = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        var discard = Make("dialog.discard", SaveChoice.Discard); var cancel = Make("dialog.cancel", SaveChoice.Cancel, cancel: true); var save = Make("dialog.save", SaveChoice.Save, primary: true);
        Grid.SetColumn(cancel, 2); Grid.SetColumn(save, 3); buttons.Children.Add(discard); buttons.Children.Add(cancel); buttons.Children.Add(save);
        w.Content = new StackPanel { Margin = new(24, 22, 24, 20), Spacing = 22, Children = { head, buttons } };
        await w.ShowDialog(owner); return result;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var w = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }; ok.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new(20), Spacing = 16, Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok } };
        await w.ShowDialog(owner);
    }

    public async Task<ExportRequest?> ShowExportDialogAsync(ImmoCad.Core.Model.Building building, string? preselect = null)
    {
        var d = new ExportDialog(building, preselect); await d.ShowDialog(owner); return d.Result;
    }

    public async Task<string?> PromptAsync(string title, string label, string initial)
    {
        string? result = null;
        var w = new Window { Title = title, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var box = new TextBox { Text = initial }; var ok = new Button { Content = "OK", IsDefault = true }; var cancel = new Button { Content = ImmoCad.Core.Translations.T("dialog.cancel"), IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; w.Close(); }; cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new(20), Spacing = 10, Children = { new TextBlock { Text = label }, box, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, ok } } } };
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await w.ShowDialog(owner); return result;
    }

    public async Task<ImportWizardViewModel?> RunImportWizardAsync(byte[] image, IReadOnlyList<WizardReference>? references = null)
    {
        var wiz = new ImportWizard(image, references); await wiz.ShowDialog(owner); return wiz.Accepted ? wiz.Result : null;
    }
}

/// <summary>Composition root shared by <see cref="App"/> and the UI tests.</summary>
public static class AppServices
{
    public static ServiceProvider Build(string? appDataDir = null, IDialogService? dialogs = null)
    {
        var dir = appDataDir ?? AppLogProvider.DefaultDir();
        var s = new ServiceCollection();
        s.AddImmoCadServices();
        s.AddSingleton(App.Log);
        s.AddSingleton<ILoggerFactory>(sp => LoggerFactory.Create(b => b.AddProvider(sp.GetRequiredService<AppLogProvider>()).SetMinimumLevel(AppLogProvider.LevelFromEnvironment())));
        s.AddSingleton(_ => SettingsStore.Load(Path.Combine(dir, "settings.json")));
        s.AddSingleton(_ => new AutosaveService(dir));
        s.AddSingleton(new SettingsLocation(Path.Combine(dir, "settings.json")));
        if (dialogs != null) s.AddSingleton(dialogs);
        return s.BuildServiceProvider();
    }
}
public sealed record SettingsLocation(string Path);
