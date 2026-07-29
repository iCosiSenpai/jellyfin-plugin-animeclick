using System;

namespace AnimeClick.Plugin.Configuration;

/// <summary>
/// Bounds for the persisted settings, applied server-side whenever the configuration is
/// loaded or saved.
/// <para>
/// The configuration page validates some of these in JavaScript, but that check is trivially
/// bypassed by POSTing to <c>/Plugins/{guid}/Configuration</c> directly, and until now nothing
/// on the server side rejected the result: a negative <c>RequestDelayMilliseconds</c>, a
/// <c>MaxSearchResults</c> of 0 or a <c>BaseUrl</c> that is not a URL at all were persisted as
/// given. Downstream consumers clamped a few of these individually, so every new consumer
/// started from scratch; the bounds now live at the boundary instead.
/// </para>
/// <para>
/// Deliberately kept free of Jellyfin types: <c>PluginConfiguration</c> derives from
/// <c>BasePluginConfiguration</c>, which cannot be loaded outside the Jellyfin runtime, so
/// putting the arithmetic here is what makes it directly testable.
/// </para>
/// </summary>
internal static class ConfigurationLimits
{
    internal const string DefaultBaseUrl = "https://www.animeclick.it";

    internal const int MinPosterWidthMinimum = 0;
    internal const int MinPosterWidthMaximum = 4000;

    // 0 keeps the documented "never expires" behaviour of TranslationCacheHours.
    internal const int TranslationCacheHoursMinimum = 0;
    internal const int TranslationCacheHoursMaximum = 100 * 8760;

    // Matches the clamp the Ollama translator and the diagnostics endpoint already apply.
    internal const int TranslationTimeoutMinimum = 5;
    internal const int TranslationTimeoutMaximum = 120;

    internal const int MaxSearchResultsMinimum = 1;
    internal const int MaxSearchResultsMaximum = 50;

    internal const int CacheHoursMinimum = 1;
    internal const int CacheHoursMaximum = 8760;

    // 0 disables negative caching, which is a meaningful choice rather than an error.
    internal const int NegativeCacheHoursMinimum = 0;
    internal const int NegativeCacheHoursMaximum = 8760;

    // Matches AnimeClickClient's own bounds on the configured inter-request delay.
    internal const int RequestDelayMinimum = 500;
    internal const int RequestDelayMaximum = 60_000;

    /// <summary>
    /// Clamps <paramref name="value"/> into the inclusive range. Unlike <see cref="Math.Clamp"/>
    /// this tolerates an inverted range instead of throwing, because the arguments come from
    /// constants that a later edit could get wrong.
    /// </summary>
    internal static int Clamp(int value, int minimum, int maximum)
    {
        if (minimum > maximum)
        {
            (minimum, maximum) = (maximum, minimum);
        }

        return value < minimum ? minimum : value > maximum ? maximum : value;
    }

    /// <summary>
    /// Returns an absolute HTTP(S) base URL without a trailing slash, falling back to
    /// <see cref="DefaultBaseUrl"/> when the stored value cannot be used. Every scraping
    /// request is built on this value, so an unusable one is worse than the default.
    /// </summary>
    internal static string NormalizeBaseUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return DefaultBaseUrl;
        }

        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }
}
