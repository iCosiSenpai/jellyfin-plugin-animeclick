using System.Net;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// La scheda di un'opera: dalla cache se è ancora valida, altrimenti dal sito; se il sito non
/// risponde, la copia in cache anche se scaduta.
/// </summary>
/// <remarks>
/// Era scritta dieci volte, nei provider, nei servizi di riparazione e nella diagnostica, con
/// piccole differenze: alcune copie mettevano in cache anche una pagina senza titolo (una
/// pagina intermedia del sito, che restava lì 48 ore al posto della scheda). Ora c'è una sola
/// regola: si mette in cache solo una scheda con il titolo.
///
/// La copia scaduta conta quando AnimeClick è bloccato da una verifica anti-bot o non risponde:
/// i dati di una settimana fa valgono più dei soli dati di un'altra fonte, e senza questo una
/// scansione durante il blocco sostituirebbe titoli e trame italiane già scritti. Un 404 invece
/// dice che la scheda non c'è più, e lì la copia vecchia non si usa.
/// </remarks>
internal static class AnimeClickAnimeCards
{
    internal static string CacheKey(string animeUrl) => "anime::" + animeUrl;

    /// <summary>Scheda valida dalla cache, altrimenti <see cref="FetchAsync"/>.</summary>
    internal static async Task<AnimeClickAnime> GetAsync(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        string animeUrl,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var cached = await cache
            .GetAsync<AnimeClickAnime>(CacheKey(animeUrl), configuration.CacheHours, cancellationToken)
            .ConfigureAwait(false);
        return cached ?? await FetchAsync(client, cache, parser, animeUrl, configuration, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Legge la scheda dal sito e la mette in cache; se il sito non risponde restituisce la
    /// copia in cache anche se scaduta, e solo in mancanza di quella rilancia l'errore.
    /// </summary>
    internal static async Task<AnimeClickAnime> FetchAsync(
        AnimeClickClient client,
        AnimeClickCacheService cache,
        AnimeClickHtmlParser parser,
        string animeUrl,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(animeUrl);
        string html;
        try
        {
            html = await client.GetStringAsync(animeUrl, configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
        {
            var stale = await cache.GetAsync<AnimeClickAnime>(key, cancellationToken).ConfigureAwait(false);
            if (stale is not null && !string.IsNullOrWhiteSpace(stale.Title))
            {
                return stale;
            }

            throw;
        }

        var anime = parser.ParseAnimePage(animeUrl, html);
        if (!string.IsNullOrWhiteSpace(anime.Title))
        {
            await cache.SetAsync(key, anime, cancellationToken).ConfigureAwait(false);
        }

        return anime;
    }
}
