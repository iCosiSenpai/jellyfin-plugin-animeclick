using System;
using System.Threading;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Smette di chiamare un servizio esterno che continua a rispondere male, e ogni tanto
/// prova se è tornato.
/// </summary>
/// <remarks>
/// Nasce da AniList, che ha disattivato la propria API: risponde 403 a chiunque, con
/// «The AniList API has been temporarily disabled due to severe stability issues». Il plugin
/// ci riprovava a ogni elemento della libreria, due volte per elemento, pagando ogni volta il
/// ritardo fra le richieste e scrivendo un avviso identico. Su una libreria di qualche
/// centinaio di titoli sono centinaia di righe che non dicono niente di nuovo — e il rumore è
/// esattamente ciò che rende invisibile il guasto successivo.
///
/// Non sostituisce la gestione del rate limit, che è un'altra cosa: 429 significa «più piano»
/// e ha già la sua pausa in <see cref="RequestThrottle"/>. Qui si tratta di un servizio giù,
/// dove rallentare non serve perché la risposta non cambia.
///
/// L'attesa raddoppia a ogni ricaduta fino a un tetto: un servizio spento da settimane non
/// merita un tentativo ogni cinque minuti, ma nemmeno di essere abbandonato per sempre.
/// La classe è sicura fra più thread: i provider di Jellyfin girano in parallelo.
/// </remarks>
public sealed class AnimeClickCircuitBreaker
{
    private readonly string _name;
    private readonly int _failureThreshold;
    private readonly TimeSpan _initialCooldown;
    private readonly TimeSpan _maximumCooldown;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private int _consecutiveFailures;
    private int _openings;
    private DateTimeOffset? _retryAt;
    private bool _probeInFlight;

    public AnimeClickCircuitBreaker(
        string name,
        int failureThreshold,
        TimeSpan initialCooldown,
        TimeSpan maximumCooldown,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);

        _name = name;
        _failureThreshold = failureThreshold;
        _initialCooldown = initialCooldown;
        _maximumCooldown = maximumCooldown;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Il nome del servizio, per i messaggi di log.</summary>
    public string Name => _name;

    /// <summary>Se al momento le chiamate sono sospese.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _retryAt is not null;
            }
        }
    }

    /// <summary>Quando si tornerà a provare, se le chiamate sono sospese.</summary>
    public DateTimeOffset? RetryAt
    {
        get
        {
            lock (_gate)
            {
                return _retryAt;
            }
        }
    }

    /// <summary>
    /// Se la chiamata si può fare. Passata l'attesa lascia passare <b>un solo</b> tentativo,
    /// per capire se il servizio è tornato senza riversargli addosso l'intera libreria.
    /// </summary>
    public bool TryEnter()
    {
        lock (_gate)
        {
            if (_retryAt is null)
            {
                return true;
            }

            if (_probeInFlight || _timeProvider.GetUtcNow() < _retryAt.Value)
            {
                return false;
            }

            _probeInFlight = true;
            return true;
        }
    }

    /// <summary>Il servizio ha risposto: si riparte da zero.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openings = 0;
            _retryAt = null;
            _probeInFlight = false;
        }
    }

    /// <summary>
    /// La chiamata è finita senza dire niente sullo stato del servizio: un annullamento,
    /// oppure un rate limit, che significa «più piano» e non «sono giù».
    /// </summary>
    /// <remarks>
    /// Restituisce il tentativo di prova eventualmente in corso. Senza questo, una chiamata
    /// che esce per una via diversa da successo o fallimento lo lascerebbe appeso, e da quel
    /// momento nessuna richiesta passerebbe più.
    /// </remarks>
    public void RecordIndeterminate()
    {
        lock (_gate)
        {
            _probeInFlight = false;
        }
    }

    /// <summary>
    /// Il servizio non ha risposto o ha risposto male.
    /// </summary>
    /// <returns>
    /// True solo sulla chiamata che sospende le richieste, così il chiamante scrive un avviso
    /// all'apertura invece che a ogni elemento della libreria.
    /// </returns>
    public bool RecordFailure()
    {
        lock (_gate)
        {
            // Il tentativo di prova è fallito: si richiude subito, con l'attesa raddoppiata.
            if (_probeInFlight)
            {
                _probeInFlight = false;
                Open();
                return false;
            }

            if (_retryAt is not null)
            {
                return false;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures < _failureThreshold)
            {
                return false;
            }

            Open();
            return true;
        }
    }

    private void Open()
    {
        _openings++;
        // 5 min, 10, 20, 40… fino al tetto. Lo scalino si calcola sui raddoppi già fatti,
        // e si ferma prima che il moltiplicatore possa traboccare.
        var doublings = Math.Min(_openings - 1, 20);
        var ticks = Math.Min(
            _initialCooldown.Ticks * (1L << doublings),
            _maximumCooldown.Ticks);
        _retryAt = _timeProvider.GetUtcNow() + TimeSpan.FromTicks(ticks);
    }
}
