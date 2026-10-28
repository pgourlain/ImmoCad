using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ImmoCad.Desktop;

/// <summary>Reads the design tokens of Themes/Tokens.axaml for the control's current theme variant.</summary>
public static class Tokens
{
    public static Color Get(Control c, string key, Color fallback) =>
        c.TryFindResource(key, c.ActualThemeVariant, out var v) && v is Color col ? col : fallback;
    public static IBrush Brush(Control c, string key, Color fallback) => new SolidColorBrush(Get(c, key, fallback));
}
