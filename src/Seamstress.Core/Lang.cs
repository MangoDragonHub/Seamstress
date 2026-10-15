using System.Globalization;
using System.Text.Json;

namespace Seamstress.Core;

/// <summary>A language the interface can be shown in: its code (as in the translation file names) and its own name for itself.</summary>
public sealed record Language(string Code, string NativeName);

/// <summary>
/// Translates the interface. Texts are written in English in the code and looked up in a catalogue for the chosen
/// language (Localization/&lt;code&gt;.json, embedded in this assembly), which maps each English text to its translation.
/// A text missing from the catalogue is shown in English.
/// </summary>
public static class Lang
{
    public const string English = "en";

    public static readonly IReadOnlyList<Language> Languages = new Language[]
    {
        new(English, "English"),
        new("es", "Español"),
        new("pt", "Português (Brasil)"),
        new("fr", "Français"),
        new("zh", "中文（简体）"),
        new("ja", "日本語"),
    };

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Loaded = new();
    private static IReadOnlyDictionary<string, string> _current = new Dictionary<string, string>();
    private static string _code = English;

    /// <summary>The code of the language being shown. Setting an unknown code switches to English.</summary>
    public static string Current
    {
        get => _code;
        set
        {
            string code = Languages.Any(l => l.Code == value) ? value : English;
            _code = code;
            _current = Catalog(code);
        }
    }

    /// <summary>
    /// The translation of an English text in the current language. A text can start with a context and "|"
    /// ("view|Front") where one English word needs different translations; without a translation the context is dropped.
    /// </summary>
    public static string T(string text) => Lookup(_current, text);

    /// <summary>Translates a text with {0}-style placeholders, then fills them in.</summary>
    public static string F(string text, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(text), args);

    /// <summary>
    /// Marks a text that is stored in English and translated later, where it is shown, so the translation check finds
    /// it. Returns the text unchanged.
    /// </summary>
    public static string N(string text) => text;

    /// <summary>The translation of an English text in a given language, without changing the current one.</summary>
    public static string In(string code, string text) => Lookup(Catalog(code), text);

    private static string Lookup(IReadOnlyDictionary<string, string> catalog, string text)
    {
        if (catalog.TryGetValue(text, out var translated) && translated.Length > 0)
            return translated;
        int bar = text.IndexOf('|');
        return bar >= 0 ? text[(bar + 1)..] : text;
    }

    /// <summary>Every English text the catalogue of a language translates, with its translation.</summary>
    public static IReadOnlyDictionary<string, string> Catalog(string code)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(code, out var catalog))
                return catalog;
            catalog = Read(code);
            Loaded[code] = catalog;
            return catalog;
        }
    }

    private static IReadOnlyDictionary<string, string> Read(string code)
    {
        if (code == English)
            return new Dictionary<string, string>();
        var assembly = typeof(Lang).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Localization.{code}.json", StringComparison.Ordinal));
        if (resource == null)
            return new Dictionary<string, string>();
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
