using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmoCad.Core;
using ImmoCad.Core.Model;
using ImmoCad.Mcp;
using Microsoft.Extensions.Logging;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

public partial class MainViewModel
{
    // ---- AI agents: the MCP server of the running app ----
    private LiveMcpServer? _mcp;

    /// <summary>File › Allow AI agents (MCP): an agent can read and edit the building shown in the window, through a local server (loopback only).</summary>
    [ObservableProperty] private bool _allowAgents;
    partial void OnAllowAgentsChanged(bool value)
    {
        Settings.AllowAgents = value; if (SettingsPath != null) try { SettingsStore.Save(Settings, SettingsPath); } catch (IOException) { }
        OnPropertyChanged(nameof(McpUrl)); OnPropertyChanged(nameof(McpCommand)); OnPropertyChanged(nameof(McpJson));
        _ = value ? StartMcpAsync() : StopMcpAsync();
    }

    public int McpPort => Settings.McpPort is > 1023 and < 65536 ? Settings.McpPort : LiveMcpServer.DefaultPort;
    public string McpUrl => $"http://127.0.0.1:{McpPort}/mcp";
    /// <summary>The command that registers the server in Claude Code.</summary>
    public string McpCommand => $"claude mcp add --transport http immocad {McpUrl}";
    /// <summary>The same server for the clients configured by a JSON file.</summary>
    public string McpJson => "{\n  \"mcpServers\": {\n    \"immocad\": { \"type\": \"http\", \"url\": \"" + McpUrl + "\" }\n  }\n}";
    public bool McpRunning => _mcp is { Running: true };

    /// <summary>Raised by File › Connect an AI agent…: the window shows how to connect (and the server is on).</summary>
    public event Action? ConnectAgentRequested;
    [RelayCommand] private void ToggleAllowAgents() => AllowAgents = !AllowAgents;
    [RelayCommand] private void ConnectAgent() { if (!AllowAgents) AllowAgents = true; ConnectAgentRequested?.Invoke(); }

    /// <summary>Starts the server when the settings say so (called once the window exists).</summary>
    public void RestoreAgents() { if (Settings.AllowAgents) AllowAgents = true; }

    private async Task StartMcpAsync()
    {
        try
        {
            if (_mcp is { Running: true }) return;
            _mcp = new LiveMcpServer(McpPort);
            await _mcp.StartAsync(new LiveSession(this), new UiMarshaller(this));
            OnPropertyChanged(nameof(McpRunning)); Status = T("agents.on", McpUrl);
        }
        catch (Exception ex)
        {
            App.Log.CreateLogger("Mcp").LogError(ex, "MCP server could not start on {Url}", McpUrl);
            _mcp = null; OnPropertyChanged(nameof(McpRunning)); Status = T("agents.failed", ex.Message);
            Settings.AllowAgents = false; _allowAgents = false; OnPropertyChanged(nameof(AllowAgents));
        }
    }

    private async Task StopMcpAsync()
    {
        try { if (_mcp is { } m) { _mcp = null; await m.StopAsync(); } }
        catch (Exception ex) { App.Log.CreateLogger("Mcp").LogError(ex, "MCP server did not stop cleanly"); }
        OnPropertyChanged(nameof(McpRunning)); Status = T("agents.off");
    }

    /// <summary>The building an agent sees is the one open in the window; its edits go through the same actions and history as the user's.</summary>
    private sealed class LiveSession(MainViewModel vm) : McpSession
    {
        public override Building Building => vm.Building;
        public override ImmoCad.Core.History.BuildingHistory History => vm.History;
        public override ImmoCad.Core.History.BuildingActions Actions => vm.Actions;
        public override string? Path { get => vm.Active.FilePath; set => vm.Active.FilePath = value; }
        public override void Replace(Building b) => vm.AddDoc(new Doc { Building = b });
    }

    private sealed class UiMarshaller(MainViewModel vm) : IUiMarshaller
    {
        public async ValueTask<T> Run<T>(Func<ValueTask<T>> work) =>
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => { try { return await work(); } finally { vm.Refresh(); vm.Changed?.Invoke(); } });
    }
}
