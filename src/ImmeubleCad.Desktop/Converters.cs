using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ImmeubleCad.Desktop;

public static class Converters
{
    public static readonly IValueConverter BoldIf = new FuncConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);
    public static readonly IValueConverter DimIf = new FuncConverter<bool, double>(b => b ? 0.45 : 1.0);
}

internal sealed class FuncConverter<TIn, TOut>(Func<TIn, TOut> f) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is TIn v ? f(v) : default(TOut);
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
