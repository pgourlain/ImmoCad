using Avalonia.Controls;

namespace ImmoCad.Desktop;

public partial class AboutDialog : Window
{
    public AboutDialog() { InitializeComponent(); Info.Text = AppLogProvider.SystemInfo(); Ok.Click += (_, _) => Close(); }
}
