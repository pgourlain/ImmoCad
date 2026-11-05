using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ImmoCad.Desktop;

public static class Converters
{
    public static readonly IValueConverter BoldIf = new FuncConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);
    /// <summary>Translation key → text in the current language (for lists of keys).</summary>
    public static readonly IValueConverter Loc = new FuncConverter<string, string>(k => ImmoCad.Core.Translations.T(k));
    /// <summary>Enum value → its display name in the current language.</summary>
    public static readonly IValueConverter EnumName = new FuncConverter<Enum, string>(e => ImmoCad.Core.Translations.Enum_(e));
    /// <summary>Network type → the colour its pipes have on the plan.</summary>
    public static readonly IValueConverter NetworkBrush = new FuncConverter<ImmoCad.Core.Model.NetworkType, IBrush>(t => { var c = ImmoCad.Core.Networks.NetworkQueries.ColorOf(t); return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)); });
    public static readonly IValueConverter DimIf = new FuncConverter<bool, double>(b => b ? 0.45 : 1.0);
    public static readonly IValueConverter NotEmpty = new FuncConverter<string, bool>(s => s.Length > 0);
    /// <summary>Orange when true; otherwise <see cref="AvaloniaProperty.UnsetValue"/> so Foreground falls back to its normal (theme) brush — a plain <c>null</c> here would set Foreground to no brush at all, making the text invisible.</summary>
    public static readonly IValueConverter WarnIf = new FuncConverter<bool, object?>(b => b ? Brushes.OrangeRed : Avalonia.AvaloniaProperty.UnsetValue);
}

internal sealed class FuncConverter<TIn, TOut>(Func<TIn, TOut> f) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is TIn v ? f(v) : default(TOut);
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
