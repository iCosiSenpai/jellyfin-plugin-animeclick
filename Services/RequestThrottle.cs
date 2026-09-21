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

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _minimumInterval;
    private readonly string _service;
    private DateTimeOffset _nextRequestUtc = DateTimeOffset.MinValue;

    public RequestThrottle(string service, TimeSpan minimumInterval, TimeProvider? clock = null)
    {
        _service = service;
        _minimumInterval = minimumInterval;
        _clock = clock ?? TimeProvider.System;
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining;
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                remaining = _nextRequestUtc - now;
                if (remaining <= TimeSpan.Zero)
                {
                    _nextRequestUtc = now.Add(_minimumInterval);
                    return;
                }
            }
            // No lock is held during a server backoff. A 429 can extend it immediately.
            await Task.Delay(remaining, _clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a throttling answer. Returns the pause that will be applied, so the caller can say
    /// so in the log: without that, a rate-limited scan looks exactly like missing metadata.
    /// </summary>
    public TimeSpan NoticeRateLimit(HttpResponseMessage? response)
    {
        var delay = ReadRetryAfter(response, _clock.GetUtcNow()) ?? TimeSpan.FromSeconds(30);
        if (delay > MaximumServerBackoff)
        {
            delay = MaximumServerBackoff;
        }

        var until = _clock.GetUtcNow().Add(delay);
        lock (_gate)
        {
            if (until > _nextRequestUtc)
            {
                _nextRequestUtc = until;
            }
        }

        return delay;
    }

    /// <summary>True when the status means "you are going too fast", not "this does not exist".</summary>
    public static bool IsRateLimited(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests
           || statusCode == HttpStatusCode.ServiceUnavailable;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage? response, DateTimeOffset now)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var fromDate = date - now;
            if (fromDate > TimeSpan.Zero)
            {
                return fromDate;
            }
        }

        return null;
    }
}
