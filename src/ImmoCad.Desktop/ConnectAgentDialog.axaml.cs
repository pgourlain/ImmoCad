using Avalonia.Controls;
using Avalonia.Input.Platform;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>How to connect an AI agent (Claude Code or any MCP client) to the running app.</summary>
public partial class ConnectAgentDialog : Window
{
    public ConnectAgentDialog() => InitializeComponent();

    public ConnectAgentDialog(MainViewModel vm) : this()
    {
        void Fill()
        {
            State.Text = T(vm.AllowAgents ? "agents.state.on" : "agents.state.off");
            Url.Text = vm.McpUrl; Command.Text = vm.McpCommand; Json.Text = vm.McpJson; Stop.IsVisible = vm.AllowAgents;
        }
        Fill();
        async void Copy(string text) { if (Clipboard != null) await Clipboard.SetTextAsync(text); }
        CopyUrl.Click += (_, _) => Copy(vm.McpUrl); CopyCommand.Click += (_, _) => Copy(vm.McpCommand); CopyJson.Click += (_, _) => Copy(vm.McpJson);
        Stop.Click += (_, _) => { vm.AllowAgents = false; Fill(); };
        Ok.Click += (_, _) => Close();
    }
}
