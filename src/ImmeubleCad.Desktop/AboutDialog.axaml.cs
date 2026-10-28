using Avalonia.Controls;

namespace ImmeubleCad.Desktop;

public partial class AboutDialog : Window
{
    public AboutDialog() { InitializeComponent(); Info.Text = AppLogProvider.SystemInfo(); Ok.Click += (_, _) => Close(); }
}
