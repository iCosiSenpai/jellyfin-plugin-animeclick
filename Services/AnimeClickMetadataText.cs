using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace AnimeClick.Plugin.Services;

/// <summary>A missing editorial value must never block a downstream provider.</summary>
internal static partial class AnimeClickMetadataText
{
    private static readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase)
    {
        "n/a", "n/d", "nd", "na", "tba", "tbd", "unknown", "untitled", "sconosciuto",
        "non disponibile", "non presente", "nessuna trama", "nessuna sinossi", "trama non disponibile",
        "sinossi non disponibile", "nessuna trama disponibile", "nessuna sinossi disponibile",
        "trama non ancora disponibile", "sinossi non ancora disponibile", "titolo non disponibile",
        "no overview available", "overview not available", "no synopsis available", "not available",
        "to be announced", "coming soon", "animeclick", "animeclick.it"
    };

    [GeneratedRegex(@"^(?:season|stagione|episodio|episode|puntata|ep\.?)\s*#?\s*\d+(?:\s*[-–/]\s*\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericName();

    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = AnimeClickAiTranslator.StripHtml(value).Trim();
        var label = text.Trim(' ', '.', '!', ':', '-', '–', '—');
        return label.Length == 0 || Missing.Contains(label) || GenericName().IsMatch(label)
            || AnimeClickHtmlParser.IsPlaceholderEpisodeText(label) ? null : text;
    }

    public static bool HasValue([NotNullWhen(true)] string? value) => Clean(value) is not null;

    public static string? ItalianOverview(string? value)
    {
        var text = Clean(value);
        return text is not null && AnimeClickMetadataLanguageDetector.Detect(text).Language != AnimeClickTextLanguage.English ? text : null;
    }

    public static string? Title(string? value)
    {
        if (value?.Length > 300 || value?.Contains('\n') == true || value?.Contains('\r') == true) return null;
        var title = Clean(value);
        return title is null || title.StartsWith("```", StringComparison.Ordinal)
            || title.StartsWith('{') || title.StartsWith('[') ? null : title;
    }

    public static string[] Labels(IEnumerable<string> values)
        => values.Select(Title).Where(v => v is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
