using System.Net;
using System.Net.Http;
using System.Text;
using AnimeClick.Plugin.Configuration;
using AnimeClick.Plugin.Models;
using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AnimeClick.Plugin.Tests;

/// <summary>
/// AnimeClick dietro la verifica anti-bot di Cloudflare (novembre 2026): ogni pagina HTML risponde
/// 403 con <c>cf-mitigated: challenge</c>. Il plugin la riconosce, smette di chiedere, usa ciò che
/// ha già in cache e riprova da solo.
/// </summary>
public class AnimeClickAvailabilityTests
{
    private const string CardUrl = "https://www.animeclick.it/anime/25493/seishun-buta-yarou";

    private const string ChallengeBody =
        "<!DOCTYPE html><html><head><title>Just a moment...</title></head><body>"
        + "<script>(function(){window._cf_chl_opt = {cType: 'managed'};}());</script></body></html>";

    [Fact]
    public void RiconosceLaVerificaDallIntestazione()
    {
        using var response = Challenge();
        Assert.True(AnimeClickAvailability.IsAntiBotChallenge(response, body: null));
    }

    [Fact]
    public void RiconosceLaVerificaDalCorpoServitoDaCloudflare()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.Server.ParseAdd("cloudflare");
        Assert.True(AnimeClickAvailability.MayBeChallenge(response));
        Assert.True(AnimeClickAvailability.IsAntiBotChallenge(response, ChallengeBody));
    }

    [Fact]
    public void UnaPaginaVietataDelSitoNonEUnaVerifica()
    {
        using var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
        forbidden.Headers.Server.ParseAdd("cloudflare");
        Assert.False(AnimeClickAvailability.IsAntiBotChallenge(forbidden, "<html><body>Accesso riservato agli utenti registrati</body></html>"));

        using var missing = new HttpResponseMessage(HttpStatusCode.NotFound);
        missing.Headers.Server.ParseAdd("cloudflare");
        Assert.False(AnimeClickAvailability.MayBeChallenge(missing));
        Assert.False(AnimeClickAvailability.IsAntiBotChallenge(missing, ChallengeBody));
    }

    [Fact]
    public async Task LaVerificaSospendeLeRichiesteELaProvaLeRiapre()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var availability = new AnimeClickAvailability(time);
        var blocked = true;
        var handler = new Handler(_ => blocked ? Challenge() : Page("<html><body>ok</body></html>"));
        var client = Client(handler, availability);
        var configuration = new PluginConfiguration();

        var first = await Assert.ThrowsAsync<AnimeClickUnavailableException>(
            () => client.GetStringAsync(CardUrl, configuration, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal(1, handler.Requests);

        var snapshot = availability.Snapshot();
        Assert.True(snapshot.Blocked);
        Assert.Equal(403, snapshot.LastBlockedStatus);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(15), snapshot.RetryAtUtc);

        // Sospese: nessuna richiesta, nessuna attesa nella coda.
        var suspended = await Assert.ThrowsAsync<AnimeClickUnavailableException>(
            () => client.GetStringAsync(CardUrl + "/episodi", configuration, CancellationToken.None));
        Assert.Null(suspended.StatusCode);
        Assert.Equal(1, handler.Requests);

        // Passata l'attesa, una sola prova: il sito è tornato e tutto riparte.
        blocked = false;
        time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        Assert.Equal("<html><body>ok</body></html>", await client.GetStringAsync(CardUrl, configuration, CancellationToken.None));
        Assert.Equal(2, handler.Requests);
        Assert.False(availability.Snapshot().Blocked);
        Assert.NotNull(availability.Snapshot().LastReachedUtc);
    }

    [Fact]
    public async Task UnaProvaFallitaRaddoppiaLAttesa()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var availability = new AnimeClickAvailability(time);
        var handler = new Handler(_ => Challenge());
        var client = Client(handler, availability);
        var configuration = new PluginConfiguration();

        await Assert.ThrowsAsync<AnimeClickUnavailableException>(() => client.GetStringAsync(CardUrl, configuration, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(16));
        await Assert.ThrowsAsync<AnimeClickUnavailableException>(() => client.GetStringAsync(CardUrl, configuration, CancellationToken.None));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(30), availability.RetryAt);
    }

    [Fact]
    public async Task LaVerificaChiestaAManoNonAspettaLaProvaAutomatica()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var availability = new AnimeClickAvailability(time);
        var blocked = true;
        var handler = new Handler(_ => blocked ? Challenge() : Page("<html></html>"));
        var client = Client(handler, availability);
        var configuration = new PluginConfiguration();

        await Assert.ThrowsAsync<AnimeClickUnavailableException>(() => client.GetStringAsync(CardUrl, configuration, CancellationToken.None));
        blocked = false;
        availability.ProbeNow();
        await client.GetStringAsync(CardUrl, configuration, CancellationToken.None);

        Assert.False(availability.Snapshot().Blocked);
    }

    [Fact]
    public async Task UnErroreDiRetePerdeLaProvaSenzaBloccareTutto()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var availability = new AnimeClickAvailability(time);
        var mode = "challenge";
        var handler = new Handler(_ => mode switch
        {
            "challenge" => Challenge(),
            "missing" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => throw new HttpRequestException("connessione rifiutata")
        });
        var client = Client(handler, availability);
        var configuration = new PluginConfiguration();

        await Assert.ThrowsAsync<AnimeClickUnavailableException>(() => client.GetStringAsync(CardUrl, configuration, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(16));

        // La prova finisce con un errore di rete: non dice nulla del blocco, ma la prova si chiude
        // e la successiva può partire. Lasciata aperta, nessuna richiesta passerebbe più.
        mode = "network";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(CardUrl, configuration, CancellationToken.None));
        Assert.True(availability.Snapshot().Blocked);

        // Anche un 404 è una risposta vera del sito: è raggiungibile.
        mode = "missing";
        var missing = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(CardUrl, configuration, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(availability.Snapshot().Blocked);
    }

    [Fact]
    public async Task LaSchedaScadutaSiUsaMentreAnimeClickEBloccato()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var card = new AnimeClickAnime { Id = "25493/seishun-buta-yarou", Title = "Seishun Buta Yarou", Overview = "Trama italiana." };
        await temporary.Cache.SetAsync(AnimeClickAnimeCards.CacheKey(CardUrl), card, CancellationToken.None);
        AgeEveryEntry(temporary, TimeSpan.FromDays(30));

        var availability = new AnimeClickAvailability(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var handler = new Handler(_ => Challenge());
        var configuration = new PluginConfiguration();

        // Scaduta: non è un riscontro, ma resta sul disco.
        Assert.Null(await temporary.Cache.GetAsync<AnimeClickAnime>(
            AnimeClickAnimeCards.CacheKey(CardUrl), configuration.CacheHours, CancellationToken.None));

        var anime = await AnimeClickAnimeCards.GetAsync(
            Client(handler, availability), temporary.Cache, new AnimeClickHtmlParser(), CardUrl, configuration, CancellationToken.None);

        Assert.Equal("Seishun Buta Yarou", anime.Title);
        Assert.Equal(1, handler.Requests);
        Assert.True(availability.Snapshot().Blocked);
    }

    [Fact]
    public async Task SenzaCopiaInCacheIlBloccoArrivaAlChiamante()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var availability = new AnimeClickAvailability(new FakeTimeProvider(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<AnimeClickUnavailableException>(() => AnimeClickAnimeCards.GetAsync(
            Client(new Handler(_ => Challenge()), availability), temporary.Cache, new AnimeClickHtmlParser(),
            CardUrl, new PluginConfiguration(), CancellationToken.None));
    }

    [Fact]
    public async Task UnaPaginaSenzaTitoloNonFinisceInCache()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var availability = new AnimeClickAvailability(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var parser = new AnimeClickHtmlParser();
        var handler = new Handler(_ => Page("<html><body><p>Pubblicità</p></body></html>"));

        await Assert.ThrowsAnyAsync<Exception>(() => AnimeClickAnimeCards.GetAsync(
            Client(handler, availability), temporary.Cache, parser, CardUrl, new PluginConfiguration(), CancellationToken.None));

        Assert.Null(await temporary.Cache.GetAsync<AnimeClickAnime>(AnimeClickAnimeCards.CacheKey(CardUrl), CancellationToken.None));
        Assert.False(availability.Snapshot().Blocked);
    }

    [Fact]
    public async Task LaRicercaSiFermaAlPrimoTentativoBloccato()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var availability = new AnimeClickAvailability(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var handler = new Handler(_ => Challenge());
        var search = new AnimeClickSeriesSearchProvider(
            Client(handler, availability), temporary.Cache, new AnimeClickHtmlParser(),
            NullLogger<AnimeClickSeriesSearchProvider>.Instance);
        var configuration = new PluginConfiguration();

        // Questo titolo, senza risultati, percorre tutta la scala: originale, semplificato,
        // suffisso, parole iniziali, prefissi e parole distintive.
        var results = await search.SearchAsync(
            "Fate/Grand Order: Shinsei Entaku Ryouiki Camelot (2020)", configuration, CancellationToken.None, 2020);

        Assert.Empty(results);
        Assert.Equal(1, handler.Requests);
        // E il blocco non diventa un "non esiste" in cache.
        Assert.Empty(Directory.EnumerateFiles(temporary.RootPath, "*search-empty*", SearchOption.AllDirectories));
    }

    private static AnimeClickClient Client(Handler handler, AnimeClickAvailability availability)
    {
        var factory = TestDoubles.Proxy<IHttpClientFactory>((method, _) =>
            method.Name == nameof(IHttpClientFactory.CreateClient)
                ? new HttpClient(handler, disposeHandler: false)
                : TestDoubles.DefaultReturn(method));
        return new AnimeClickClient(factory, NullLogger<AnimeClickClient>.Instance, availability);
    }

    private static HttpResponseMessage Challenge()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(ChallengeBody, Encoding.UTF8, "text/html")
        };
        response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
        response.Headers.Server.ParseAdd("cloudflare");
        return response;
    }

    private static HttpResponseMessage Page(string html)
        => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static void AgeEveryEntry(TemporaryAnimeClickCache temporary, TimeSpan age)
    {
        foreach (var file in Directory.EnumerateFiles(temporary.RootPath, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}

/// <summary>Correzioni del motore di ricerca e della lettura delle schede (1.8).</summary>
public class AnimeClickEngineRefinementTests
{
    [Theory]
    [InlineData("Kingdom 3rd Season", "Kingdom")]
    [InlineData("Mushoku Tensei Season 4", "Mushoku Tensei")]
    [InlineData("Re:Zero Third Season", "Re:Zero")]
    [InlineData("Mushoku Tensei IV", "Mushoku Tensei")]
    public void LaRicercaTogliePureIlNumeroDellaStagione(string name, string query)
        => Assert.Equal(query, AnimeClickSeriesSearchProvider.CleanSearchQuery(name));

    [Fact]
    public void UnaStagioneNonEUnSuffissoDistintivo()
    {
        Assert.Null(AnimeClickSeriesSearchProvider.GetSuffixQuery("Kingdom 3rd Season"));
        Assert.Equal("Railgun S", AnimeClickSeriesSearchProvider.GetSuffixQuery("Toaru Kagaku no Railgun S"));
    }

    [Theory]
    [InlineData("kingdom 2", "kingdom")]
    [InlineData("kingdom 3rd season", "kingdom")]
    [InlineData("mushoku tensei iv", "mushoku tensei")]
    [InlineData("86", "86")]
    [InlineData("mob psycho 100", "mob psycho 100")]
    [InlineData("season 2", "season 2")]
    public void IlTitoloSenzaParte(string normalized, string expected)
        => Assert.Equal(expected, AnimeClickAutomaticIdentification.StripPart(normalized));

    [Theory]
    [InlineData("Shōjo", "Shoujo")]
    [InlineData("Yarō", "Yarou")]
    [InlineData("Shūmatsu", "Shuumatsu")]
    public void LeVocaliLungheSiConfrontanoUguali(string accented, string romaji)
        => Assert.Equal(AnimeClickSearchScorer.NormalizeTitle(romaji), AnimeClickSearchScorer.NormalizeTitle(accented));

    [Theory]
    [InlineData("2019-01-01", null)]
    [InlineData("2019-06-15", "2019-06-15")]
    [InlineData("2019-06-15T00:00:00+02:00", "2019-06-14")]
    [InlineData("", null)]
    [InlineData("non una data", null)]
    public void LaDataDiUscitaDiUnaSchedaSoloSeVera(string value, string? expected)
    {
        var parsed = AnimeClickHtmlParser.ParsePremiereDate(value);
        Assert.Equal(expected, parsed?.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (parsed is not null) Assert.Equal(TimeSpan.Zero, parsed.Value.Offset);
    }

    [Fact]
    public async Task UnTitoloConLaBarraSiCercaComeTesto()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var requested = new List<string>();
        var factory = TestDoubles.Proxy<IHttpClientFactory>((method, _) =>
            method.Name == nameof(IHttpClientFactory.CreateClient)
                ? new HttpClient(new RecordingHandler(requested), disposeHandler: true)
                : TestDoubles.DefaultReturn(method));
        var search = new AnimeClickSeriesSearchProvider(
            new AnimeClickClient(factory, NullLogger<AnimeClickClient>.Instance, new AnimeClickAvailability()),
            temporary.Cache, new AnimeClickHtmlParser(), NullLogger<AnimeClickSeriesSearchProvider>.Instance);

        await search.SearchAsync("22/7", new PluginConfiguration(), CancellationToken.None);

        Assert.NotEmpty(requested);
        Assert.Contains("/cerca?name=", requested[0], StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(List<string> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("<html><body></body></html>", Encoding.UTF8, "text/html")
            });
        }
    }
}

public class AnimeClickCacheSweepTests
{
    [Fact]
    public async Task LaPuliziaToglieSoloCioCheNessunoRilegge()
    {
        using var temporary = new TemporaryAnimeClickCache();
        var cache = temporary.Cache;
        await cache.SetAsync("anime::https://www.animeclick.it/anime/1/vecchia", new AnimeClickAnime { Title = "Vecchia" }, CancellationToken.None);
        await cache.SetAsync("anime::https://www.animeclick.it/anime/2/recente", new AnimeClickAnime { Title = "Recente" }, CancellationToken.None);
        await cache.SetAsync("translation:v4::1::costosa", "Traduzione pagata", CancellationToken.None);
        var files = Directory.EnumerateFiles(temporary.RootPath, "*.json", SearchOption.AllDirectories).ToList();
        foreach (var file in files.Where(file => !file.Contains("recente", StringComparison.Ordinal)))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-400));
        }

        var orphan = files[0] + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(orphan, "{");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-3));

        var removed = cache.Sweep(DateTime.UtcNow, TimeSpan.FromDays(180));

        Assert.Equal(2, removed);
        Assert.False(File.Exists(orphan));
        Assert.Null(await cache.GetAsync<AnimeClickAnime>("anime::https://www.animeclick.it/anime/1/vecchia", CancellationToken.None));
        Assert.NotNull(await cache.GetAsync<AnimeClickAnime>("anime::https://www.animeclick.it/anime/2/recente", CancellationToken.None));
        Assert.Equal("Traduzione pagata", await cache.GetAsync<string>("translation:v4::1::costosa", CancellationToken.None));
    }
}
