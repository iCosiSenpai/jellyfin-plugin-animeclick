using System;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Se AnimeClick, in questo momento, risponde alle richieste automatiche.
/// </summary>
/// <remarks>
/// Nasce dalla verifica anti-bot di Cloudflare che AnimeClick ha attivato su tutte le pagine
/// HTML nel novembre 2026: a qualunque client che non esegue JavaScript risponde 403 con
/// <c>cf-mitigated: challenge</c> e una pagina «Just a moment...», anche con lo User-Agent di
/// un browser. Il plugin non la supera e non prova a farlo: la verifica è una scelta del sito.
///
/// Prima di questa classe ogni elemento della libreria provava fino a una dozzina di varianti
/// della ricerca, ognuna con la sua attesa fra le richieste e il suo errore nel log, e la
/// scheda in cache scaduta veniva cancellata proprio quando sarebbe servita. Ora la prima
/// verifica sospende tutte le richieste ad AnimeClick; un solo tentativo, dopo 15 minuti, 30,
/// un'ora… fino a sei ore, controlla se il sito è tornato raggiungibile. Una verifica chiesta
/// dalla pagina del plugin anticipa quel tentativo.
///
/// Conta solo la verifica anti-bot: un 404, un 500 o un timeout dicono qualcosa della singola
/// pagina o della rete, e hanno già i loro tentativi nel client.
/// </remarks>
public sealed class AnimeClickAvailability
{
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumCooldown = TimeSpan.FromHours(6);

    // Tanto basta a riconoscere la pagina di verifica fra le prime righe senza leggere oltre.
    private const int ChallengeSniffLength = 16 * 1024;

    private static readonly string[] ChallengeMarkers =
    [
        "_cf_chl_opt",
        "/cdn-cgi/challenge-platform/",
        "cf-browser-verification",
        "cf-error-details",
        "Attention Required! | Cloudflare"
    ];

    private readonly AnimeClickCircuitBreaker _breaker;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private DateTimeOffset? _blockedSince;
    private DateTimeOffset? _lastBlockedAt;
    private DateTimeOffset? _lastReachedAt;
    private int? _lastBlockedStatus;

    public AnimeClickAvailability(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _breaker = new AnimeClickCircuitBreaker(
            "AnimeClick",
            failureThreshold: 1,
            InitialCooldown,
            MaximumCooldown,
            _timeProvider);
    }

    /// <summary>
    /// Lo stato del processo. Statico come la coda delle richieste del client: la verifica
    /// riguarda il sito, non la singola richiesta.
    /// </summary>
    internal static AnimeClickAvailability Shared { get; } = new();

    /// <summary>Quando si tornerà a provare, se le richieste sono sospese.</summary>
    public DateTimeOffset? RetryAt => _breaker.RetryAt;

    /// <summary>Se le richieste sono sospese in questo momento.</summary>
    internal bool IsSuspended => _breaker.IsOpen;

    /// <summary>Se la richiesta può partire; dopo l'attesa lascia passare un solo tentativo.</summary>
    internal bool TryEnter(out bool isProbe) => _breaker.TryEnter(out isProbe);

    /// <summary>AnimeClick ha risposto con la verifica anti-bot.</summary>
    /// <returns>True solo quando le richieste vengono sospese adesso, per un solo avviso nel log.</returns>
    internal bool RecordBlocked(HttpStatusCode statusCode)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            _blockedSince ??= now;
            _lastBlockedAt = now;
            _lastBlockedStatus = (int)statusCode;
        }

        return _breaker.RecordFailure();
    }

    /// <summary>AnimeClick ha risposto con una pagina vera, anche un 404.</summary>
    internal void RecordReachable()
    {
        lock (_gate)
        {
            _blockedSince = null;
            _lastReachedAt = _timeProvider.GetUtcNow();
        }

        _breaker.RecordSuccess();
    }

    /// <summary>
    /// Il tentativo di prova è finito senza dire nulla del blocco: rete, timeout, 5xx,
    /// annullamento. Lo chiama solo chi tiene la prova.
    /// </summary>
    internal void RecordInconclusive() => _breaker.RecordIndeterminate();

    /// <summary>Anticipa a adesso il tentativo successivo, per una verifica chiesta dalla pagina.</summary>
    public void ProbeNow() => _breaker.ProbeNow();

    public AnimeClickAvailabilitySnapshot Snapshot()
    {
        lock (_gate)
        {
            var retryAt = _breaker.RetryAt;
            return new AnimeClickAvailabilitySnapshot
            {
                Blocked = retryAt is not null,
                BlockedSinceUtc = retryAt is null ? null : _blockedSince,
                LastBlockedUtc = _lastBlockedAt,
                LastBlockedStatus = _lastBlockedStatus,
                LastReachedUtc = _lastReachedAt,
                RetryAtUtc = retryAt
            };
        }
    }

    /// <summary>
    /// Se la risposta è la verifica anti-bot di Cloudflare invece della pagina richiesta.
    /// </summary>
    /// <remarks>
    /// L'intestazione <c>cf-mitigated: challenge</c> basta da sola. Senza, si guarda il corpo
    /// solo di un 403, 429 o 503 servito da Cloudflare: è da lì che passano anche gli errori
    /// del sito, quindi lo stato da solo non distingue una pagina vietata da una verifica.
    /// </remarks>
    internal static bool IsAntiBotChallenge(HttpResponseMessage response, string? body)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated))
        {
            foreach (var value in mitigated)
            {
                if (value.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable)
            || !IsServedByCloudflare(response)
            || string.IsNullOrEmpty(body))
        {
            return false;
        }

        var head = body.Length > ChallengeSniffLength ? body[..ChallengeSniffLength] : body;
        foreach (var marker in ChallengeMarkers)
        {
            if (head.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Se vale la pena leggere il corpo per cercare la verifica.</summary>
    internal static bool MayBeChallenge(HttpResponseMessage response)
        => response.Headers.Contains("cf-mitigated")
           || ((response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                   or HttpStatusCode.ServiceUnavailable)
               && IsServedByCloudflare(response));

    private static bool IsServedByCloudflare(HttpResponseMessage response)
    {
        if (response.Headers.Contains("cf-ray"))
        {
            return true;
        }

        foreach (var product in response.Headers.Server)
        {
            if (product.Product?.Name.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Lo stato di AnimeClick come lo vede la pagina del plugin.</summary>
public sealed class AnimeClickAvailabilitySnapshot
{
    /// <summary>Le richieste ad AnimeClick sono sospese per la verifica anti-bot.</summary>
    public bool Blocked { get; init; }

    public DateTimeOffset? BlockedSinceUtc { get; init; }

    public DateTimeOffset? LastBlockedUtc { get; init; }

    public int? LastBlockedStatus { get; init; }

    public DateTimeOffset? LastReachedUtc { get; init; }

    public DateTimeOffset? RetryAtUtc { get; init; }
}

/// <summary>
/// AnimeClick non ha restituito la pagina: ha risposto con la verifica anti-bot, oppure le
/// richieste sono sospese da una verifica precedente.
/// </summary>
/// <remarks>
/// Deriva da <see cref="HttpRequestException"/> perché i chiamanti che già gestiscono un errore
/// di rete continuino a farlo; chi vuole distinguere — per non scrivere un errore per ogni
/// elemento, o per usare la scheda in cache anche se scaduta — intercetta questo tipo.
/// </remarks>
public sealed class AnimeClickUnavailableException : HttpRequestException
{
    public AnimeClickUnavailableException(string message, HttpStatusCode? statusCode, DateTimeOffset? retryAt)
        : base(message, inner: null, statusCode)
    {
        RetryAt = retryAt;
    }

    /// <summary>Quando il plugin tornerà a provare.</summary>
    public DateTimeOffset? RetryAt { get; }

    internal static AnimeClickUnavailableException Blocked(HttpStatusCode statusCode, DateTimeOffset? retryAt)
        => new(
            $"AnimeClick ha risposto con la verifica anti-bot di Cloudflare (HTTP {(int)statusCode}) invece della pagina.",
            statusCode,
            retryAt);

    internal static AnimeClickUnavailableException Suspended(DateTimeOffset? retryAt)
        => new(
            "Richieste ad AnimeClick sospese: il sito risponde con la verifica anti-bot di Cloudflare.",
            statusCode: null,
            retryAt);
}
