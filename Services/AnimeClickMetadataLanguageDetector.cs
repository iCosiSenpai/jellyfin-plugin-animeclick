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
