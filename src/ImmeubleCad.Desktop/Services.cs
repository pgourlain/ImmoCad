using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ImmeubleCad.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ImmeubleCad.Desktop;

public enum SaveChoice { Save, Discard, Cancel }

/// <summary>Everything the view-model needs from the UI: file pickers, prompts, errors, the import wizard.</summary>
public interface IDialogService
{
    Task<string?> PickOpenAsync(string title, string pattern);
    Task<string?> PickSaveAsync(string title, string pattern, string? suggestedName = null);
    Task<SaveChoice> ConfirmSaveAsync(string documentName);
    Task ShowErrorAsync(string title, string message);
    Task<ImportWizardViewModel?> RunImportWizardAsync(byte[] image);
    /// <summary>Single text input; null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string label, string initial);
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
    public Queue<string?> PromptAnswers { get; } = new();
    public Task<string?> PromptAsync(string title, string label, string initial) { Asked.Add("prompt:" + title); return Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null); }
    public Task<ImportWizardViewModel?> RunImportWizardAsync(byte[] image) => Task.FromResult(Wizard?.Invoke(image));
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
        var w = new Window { Title = "ImmeubleCad", Width = 420, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var panel = new StackPanel { Margin = new(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = ImmeubleCad.Core.Translations.T("dialog.save.changes", documentName), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        foreach (var (label, choice) in new[] { (ImmeubleCad.Core.Translations.T("dialog.save"), SaveChoice.Save), (ImmeubleCad.Core.Translations.T("dialog.discard"), SaveChoice.Discard), (ImmeubleCad.Core.Translations.T("dialog.cancel"), SaveChoice.Cancel) })
        { var b = new Button { Content = label }; b.Click += (_, _) => { result = choice; w.Close(); }; buttons.Children.Add(b); }
        panel.Children.Add(buttons); w.Content = panel; await w.ShowDialog(owner); return result;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var w = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }; ok.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new(20), Spacing = 16, Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok } };
        await w.ShowDialog(owner);
    }

    public async Task<string?> PromptAsync(string title, string label, string initial)
    {
        string? result = null;
        var w = new Window { Title = title, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var box = new TextBox { Text = initial }; var ok = new Button { Content = "OK", IsDefault = true }; var cancel = new Button { Content = ImmeubleCad.Core.Translations.T("dialog.cancel"), IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; w.Close(); }; cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new(20), Spacing = 10, Children = { new TextBlock { Text = label }, box, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, ok } } } };
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await w.ShowDialog(owner); return result;
    }

    public async Task<ImportWizardViewModel?> RunImportWizardAsync(byte[] image)
    {
        var wiz = new ImportWizard(image); await wiz.ShowDialog(owner); return wiz.Accepted ? wiz.Result : null;
    }
}

/// <summary>Composition root shared by <see cref="App"/> and the UI tests.</summary>
public static class AppServices
{
    public static ServiceProvider Build(string? appDataDir = null, IDialogService? dialogs = null)
    {
        var dir = appDataDir ?? AppLogProvider.DefaultDir();
        var s = new ServiceCollection();
        s.AddImmeubleCadServices();
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
