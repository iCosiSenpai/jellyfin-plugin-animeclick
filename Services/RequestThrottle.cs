using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Process-wide pacing for one external API: a minimum interval between requests, plus a pause
/// the service itself asks for with <c>Retry-After</c>.
/// <para>
/// Only AnimeClick had any pacing. TheTVDB, TMDB and AniList were called as fast as a library
/// scan could issue requests, and their 429 answers were treated as ordinary failures: the miss
/// is deliberately not negative-cached, so the next scan retried everything and the situation got
/// worse under load rather than better. AniList allows roughly 90 requests a minute and a scan of
/// a few hundred series passes that comfortably.
/// </para>
/// <para>
/// The server-requested pause is honoured but clamped, for the same reason as in
/// <see cref="AnimeClickClient"/>: an absurd or hostile value must not be able to park every
/// later request until Jellyfin restarts.
/// </para>
/// </summary>
internal sealed class RequestThrottle
{
    private static readonly TimeSpan MaximumServerBackoff = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minimumInterval;
    private readonly string _service;
    private DateTime _nextRequestUtc = DateTime.MinValue;

    public RequestThrottle(string service, TimeSpan minimumInterval)
    {
        _service = service;
        _minimumInterval = minimumInterval;
    }

    /// <summary>Name of the paced service, for logging by the caller.</summary>
    public string Service => _service;

    /// <summary>
    /// Waits until the next request is allowed. The gate is released before returning, so a slow
    /// request does not hold back the others: pacing is about spacing request starts, and holding
    /// the gate for the whole exchange would serialise the whole scan onto one connection.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = _nextRequestUtc - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            _nextRequestUtc = DateTime.UtcNow.Add(_minimumInterval);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records a throttling answer. Returns the pause that will be applied, so the caller can say
    /// so in the log: without that, a rate-limited scan looks exactly like missing metadata.
    /// </summary>
    public TimeSpan NoticeRateLimit(HttpResponseMessage? response)
    {
        var delay = ReadRetryAfter(response) ?? TimeSpan.FromSeconds(30);
        if (delay > MaximumServerBackoff)
        {
            delay = MaximumServerBackoff;
        }

        var until = DateTime.UtcNow.Add(delay);
        _gate.Wait();
        try
        {
            if (until > _nextRequestUtc)
            {
                _nextRequestUtc = until;
            }
        }
        finally
        {
            _gate.Release();
        }

        return delay;
    }

    /// <summary>True when the status means "you are going too fast", not "this does not exist".</summary>
    public static bool IsRateLimited(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests
           || statusCode == HttpStatusCode.ServiceUnavailable;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var fromDate = date - DateTimeOffset.UtcNow;
            if (fromDate > TimeSpan.Zero)
            {
                return fromDate;
            }
        }

        return null;
    }
}
