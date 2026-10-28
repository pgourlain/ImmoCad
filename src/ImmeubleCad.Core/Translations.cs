namespace ImmeubleCad.Core;

/// <summary>
/// User-visible strings, English and French, one language at a time. English is the default; the language can change at runtime
/// (<see cref="LanguageChanged"/> tells the views to refresh). A key missing in the current language falls back to English, then to the key itself.
/// </summary>
public static class Translations
{
    public static readonly string[] Languages = ["en", "fr"];
    private static string _language = "en";
    public static string Language
    {
        get => _language;
        set { var v = Languages.Contains(value) ? value : "en"; if (v == _language) return; _language = v; LanguageChanged?.Invoke(); }
    }
    public static event Action? LanguageChanged;

    private static readonly Lazy<(Dictionary<string, string> En, Dictionary<string, string> Fr)> Tables = new(() =>
    {
        var en = new Dictionary<string, string>(); var fr = new Dictionary<string, string>();
        foreach (var (k, e, f) in TranslationData.All) { en[k] = e; fr[k] = f; }
        return (en, fr);
    });

    public static string T(string key, params object[] args)
    {
        var (en, fr) = Tables.Value;
        var text = (_language == "fr" ? fr : en).TryGetValue(key, out var v) ? v : en.TryGetValue(key, out var e) ? e : key;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    /// <summary>Display name of an enum value (network type, space kind…), from the keys "enum.&lt;Type&gt;.&lt;Value&gt;".</summary>
    public static string Enum_(Enum value) => T($"enum.{value.GetType().Name}.{value}");

    /// <summary>A generated name that follows the language: "@key" or "@key:arg1:arg2". Anything else is a literal the user typed.</summary>
    public static string Token(string key, params object[] args) => args.Length == 0 ? "@" + key : "@" + key + ":" + string.Join(":", args);
    public static string Display(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw[0] != '@') return raw ?? "";
        var parts = raw[1..].Split(':'); return T(parts[0], parts.Skip(1).Cast<object>().ToArray());
    }

    public static bool Has(string key, string language) => (language == "fr" ? Tables.Value.Fr : Tables.Value.En).ContainsKey(key);
    public static IReadOnlyCollection<string> Keys => Tables.Value.En.Keys;
    public static IReadOnlyCollection<string> FrenchKeys => Tables.Value.Fr.Keys;
    public static IReadOnlyCollection<string> EnglishKeys => Tables.Value.En.Keys;
    public static string In(string language, string key) => (language == "fr" ? Tables.Value.Fr : Tables.Value.En).GetValueOrDefault(key, key);
}
