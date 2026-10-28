using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using static ImmoCad.Core.Translations;

namespace ImmoCad.Desktop;

/// <summary>
/// Localization from XAML without reflection bindings (Native AOT friendly): <c>vm:L.Text="key"</c>, <c>vm:L.Content</c>, <c>vm:L.Header</c>,
/// <c>vm:L.Tip</c>, <c>vm:L.Watermark</c>, <c>vm:L.Title</c>, <c>vm:L.Format</c>. The text is applied when the property is set and again,
/// for every live control, whenever the language changes.
/// </summary>
public sealed class L : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TextProperty = Register("Text");
    public static readonly AttachedProperty<string?> ContentProperty = Register("Content");
    public static readonly AttachedProperty<string?> HeaderProperty = Register("Header");
    public static readonly AttachedProperty<string?> TipProperty = Register("Tip");
    public static readonly AttachedProperty<string?> WatermarkProperty = Register("Watermark");
    public static readonly AttachedProperty<string?> TitleProperty = Register("Title");
    public static readonly AttachedProperty<string?> FormatProperty = Register("Format");

    public static string? GetText(AvaloniaObject o) => o.GetValue(TextProperty); public static void SetText(AvaloniaObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetContent(AvaloniaObject o) => o.GetValue(ContentProperty); public static void SetContent(AvaloniaObject o, string? v) => o.SetValue(ContentProperty, v);
    public static string? GetHeader(AvaloniaObject o) => o.GetValue(HeaderProperty); public static void SetHeader(AvaloniaObject o, string? v) => o.SetValue(HeaderProperty, v);
    public static string? GetTip(AvaloniaObject o) => o.GetValue(TipProperty); public static void SetTip(AvaloniaObject o, string? v) => o.SetValue(TipProperty, v);
    public static string? GetWatermark(AvaloniaObject o) => o.GetValue(WatermarkProperty); public static void SetWatermark(AvaloniaObject o, string? v) => o.SetValue(WatermarkProperty, v);
    public static string? GetTitle(AvaloniaObject o) => o.GetValue(TitleProperty); public static void SetTitle(AvaloniaObject o, string? v) => o.SetValue(TitleProperty, v);
    public static string? GetFormat(AvaloniaObject o) => o.GetValue(FormatProperty); public static void SetFormat(AvaloniaObject o, string? v) => o.SetValue(FormatProperty, v);

    private static readonly List<WeakReference<AvaloniaObject>> Live = new();

    static L() => LanguageChanged += Refresh;

    private static AttachedProperty<string?> Register(string name)
    {
        var p = AvaloniaProperty.RegisterAttached<L, AvaloniaObject, string?>(name);
        p.Changed.AddClassHandler<AvaloniaObject>((o, _) => { Track(o); Apply(o); });
        return p;
    }

    private static void Track(AvaloniaObject o) { if (!Live.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, o))) Live.Add(new WeakReference<AvaloniaObject>(o)); }

    private static void Refresh()
    {
        Live.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Live.ToList()) if (w.TryGetTarget(out var o)) Apply(o);
    }

    private static void Apply(AvaloniaObject o)
    {
        if (GetText(o) is { } t && o is TextBlock tb) tb.Text = T(t);
        if (GetContent(o) is { } c && o is ContentControl cc) cc.Content = T(c);
        if (GetHeader(o) is { } h) { if (o is MenuItem mi) mi.Header = T(h); else if (o is HeaderedContentControl hc) hc.Header = T(h); }
        if (GetTip(o) is { } tip && o is Control ctl) ToolTip.SetTip(ctl, T(tip));
        if (GetWatermark(o) is { } wm && o is TextBox tx) tx.Watermark = T(wm);
        if (GetTitle(o) is { } ti && o is Window w) w.Title = T(ti);
        if (GetFormat(o) is { } f && o is NumericUpDown n) n.FormatString = T(f);
    }
}
