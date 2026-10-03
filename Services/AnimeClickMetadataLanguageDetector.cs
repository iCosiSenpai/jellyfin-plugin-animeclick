using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimeClick.Plugin.Services;

public enum AnimeClickTextLanguage
{
    Unknown,
    Italian,
    English
}

public sealed record AnimeClickLanguageDetection(
    AnimeClickTextLanguage Language,
    double Confidence,
    int TokenCount,
    int ItalianEvidence,
    int EnglishEvidence);

/// <summary>
/// Small deterministic EN/IT classifier for metadata auditing. It deliberately
/// returns Unknown for short or mixed text: classification is used to select a
/// repair candidate, never as a reason to overwrite uncertain content.
/// </summary>
public static partial class AnimeClickMetadataLanguageDetector
{
    private static readonly HashSet<string> ItalianWords = new(StringComparer.Ordinal)
    {
        "anche", "avere", "aveva", "che", "chi", "come", "con", "contro", "cui", "dalla",
        "dalle", "degli", "della", "delle", "dello", "dopo", "dove", "durante", "era", "erano",
        "essere", "fino", "gli", "hanno", "mentre", "nella", "nelle", "nello", "non", "ogni",
        "per", "perché", "pero", "però", "più", "prima", "quando", "quella", "quelle", "quello",
        "questa", "queste", "questi", "questo", "senza", "sono", "sua", "sue", "sul", "sulla",
        "tra", "tutto", "una", "viene"
    };

    private static readonly HashSet<string> EnglishWords = new(StringComparer.Ordinal)
    {
        "about", "after", "again", "against", "also", "although", "and", "another", "are", "because",
        "before", "being", "between", "but", "can", "could", "does", "during", "each", "even", "from",
        "had", "has", "have", "her", "him", "his", "how", "into", "its", "more", "most", "not", "only",
        "other", "our", "over", "she", "should", "some", "than", "that", "their", "them", "then", "there",
        "these", "they", "this", "those", "through", "until", "very", "was", "were", "what", "when", "where",
        "which", "while", "who", "why", "will", "with", "would", "you", "your"
    };

    [GeneratedRegex(@"[\p{L}]+(?:['’][\p{L}]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    // Episode names are usually too short for the synopsis classifier. Require
    // multiple English clues and no Italian clue; single names stay uncertain.
    private static readonly HashSet<string> ItalianTitleWords = new(ItalianWords, StringComparer.Ordinal)
    {
        "il", "lo", "la", "le", "un", "di", "del", "dei", "e", "è", "al", "nel",
        "io", "tu", "mio", "mia", "noi", "voi", "sei"
    };

    private static readonly HashSet<string> EnglishTitleWords = new(EnglishWords, StringComparer.Ordinal)
    {
        "the", "my", "we", "is", "am", "to", "beginning", "end", "new", "day", "girl",
        "boy", "friend", "friends", "love", "world", "life", "heart", "school", "first",
        "last", "return", "want", "like", "meet", "see", "time", "dream", "dreams",
        "battle", "brother", "sister", "night", "welcome", "me", "it", "an", "of",
        "for", "on", "off", "out", "up", "down", "never", "always", "nothing", "everything",
        "dead", "alive", "happy", "birthday", "date", "secret", "truth", "promise", "wish",
        "summer", "winter", "spring", "autumn", "family", "home", "attack", "betrayal",
        "reunion", "good", "bad", "best", "worst", "small", "big", "little", "lost", "found",
        "gone", "alone", "together", "today", "tomorrow", "yesterday", "morning", "evening",
        "i'm", "i'll", "i've", "you're", "we're", "they're", "don't", "doesn't", "can't", "won't"
    };

    public static bool IsEnglishEpisodeTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.IndexOfAny(['à', 'è', 'ì', 'ò', 'ù', 'À', 'È', 'Ì', 'Ò', 'Ù']) >= 0) return false;
        var tokens = WordRegex().Matches(text.ToLowerInvariant())
            .Select(match => match.Value.Replace('’', '\''))
            .Distinct(StringComparer.Ordinal).ToList();
        if (tokens.Any(ItalianTitleWords.Contains)) return false;
        // "I" alone is also an Italian article before a proper name ("I Little Busters").
        return tokens.Count(EnglishTitleWords.Contains) >= 2 || tokens.Contains("i") && tokens.Contains("am");
    }

    /// <summary>
    /// Se il testo e' riconoscibilmente italiano.
    /// </summary>
    /// <remarks>
    /// Un testo troppo corto o ambiguo non e' italiano ai fini di questa domanda: serve a
    /// decidere se una sinossi presa da una fonte esterna puo' essere usata cosi' com'e'
    /// o va tradotta, e nel dubbio conviene tradurre.
    /// </remarks>
    public static bool IsItalian(string? text)
        => Detect(text).Language == AnimeClickTextLanguage.Italian;

    public static AnimeClickLanguageDetection Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AnimeClickLanguageDetection(AnimeClickTextLanguage.Unknown, 0, 0, 0, 0);
        }

        var tokens = WordRegex()
            .Matches(text.ToLowerInvariant())
            .Select(match => match.Value.Replace('’', '\''))
            .ToList();
        if (tokens.Count < 8)
        {
            return new AnimeClickLanguageDetection(AnimeClickTextLanguage.Unknown, 0, tokens.Count, 0, 0);
        }

        var italian = tokens.Count(ItalianWords.Contains);
        var english = tokens.Count(EnglishWords.Contains);
        var evidence = italian + english;
        if (evidence < 3)
        {
            return new AnimeClickLanguageDetection(
                AnimeClickTextLanguage.Unknown,
                evidence / (double)Math.Max(3, tokens.Count),
                tokens.Count,
                italian,
                english);
        }

        var winner = Math.Max(italian, english);
        var loser = Math.Min(italian, english);
        var confidence = winner / (double)evidence;
        if (winner < 3 || winner < (loser * 1.5) + 1 || confidence < 0.7)
        {
            return new AnimeClickLanguageDetection(
                AnimeClickTextLanguage.Unknown,
                confidence,
                tokens.Count,
                italian,
                english);
        }

        return new AnimeClickLanguageDetection(
            english > italian ? AnimeClickTextLanguage.English : AnimeClickTextLanguage.Italian,
            confidence,
            tokens.Count,
            italian,
            english);
    }
}
