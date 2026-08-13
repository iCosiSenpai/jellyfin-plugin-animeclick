using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// What happened the last time a repair was attempted on one item.
/// </summary>
public enum AnimeClickRepairOutcome
{
    /// <summary>A value was resolved; the provider decides whether it can be written.</summary>
    Available,

    /// <summary>The Overview was replaced.</summary>
    Applied,

    /// <summary>No value yet: an AI translation was queued and will publish later.</summary>
    WaitingTranslation,

    /// <summary>No source has this synopsis: AnimeClick, TheTVDB and TMDB were all empty.</summary>
    NoSource,

    /// <summary>The feature that would supply the value is switched off.</summary>
    Disabled,

    /// <summary>A lock, or a value that changed meanwhile, prevented the write.</summary>
    Blocked,

    /// <summary>Resolution threw.</summary>
    Error
}

/// <summary>
/// The recorded result of one repair attempt. Persisted, so a restart does not make the audit
/// forget what it already tried.
/// </summary>
public sealed class AnimeClickRepairAttempt
{
    public string Outcome { get; set; } = nameof(AnimeClickRepairOutcome.NoSource);

    public string Detail { get; set; } = string.Empty;

    public DateTimeOffset AttemptedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>
    /// Plugin version that produced this attempt. A newer version may know how to resolve what an
    /// older one could not, so its "no source" verdicts must not be inherited.
    /// </summary>
    public string PluginVersion { get; set; } = string.Empty;
}

/// <summary>
/// Remembers the outcome of every repair attempt, so the audit can distinguish "not fixed yet"
/// from "cannot be fixed".
///
/// Without this the report only classified the language of the stored text, and an item for which
/// no source has a synopsis stayed advertised as repairable forever: each bulk run queued it again,
/// changed nothing, and the totals never moved. Attempts are therefore recorded and, for a bounded
/// window, subtract the item from the actionable set — an explicit retry still overrides that, and
/// the window expires because sources do get filled in over time.
/// </summary>
public sealed class AnimeClickRepairLedger : IDisposable
{
    internal const string CacheKey = "repairLedger:v1";

    /// <summary>How long "no source anywhere" keeps an item out of the actionable set.</summary>
    internal static readonly TimeSpan NoSourceSuppression = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a queued translation keeps an item out of the actionable set. Long enough to cover
    /// a backlogged queue, short enough that a translation that never publishes is retried.
    /// </summary>
    internal static readonly TimeSpan WaitingSuppression = TimeSpan.FromHours(6);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(5);
    private const int MaximumEntries = 20_000;

    private readonly ConcurrentDictionary<Guid, AnimeClickRepairAttempt> _entries = new();
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickRepairLedger> _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Timer _flushTimer;
    private volatile bool _loaded;
    private int _disposed;

    public AnimeClickRepairLedger(
        AnimeClickCacheService cache,
        ILogger<AnimeClickRepairLedger> logger)
    {
        _cache = cache;
        _logger = logger;
        _flushTimer = new Timer(_ => TriggerFlush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Loads the persisted ledger once. Every read path calls this first; a failed load degrades to
    /// an empty ledger, which only costs one redundant repair attempt per item.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            Dictionary<string, AnimeClickRepairAttempt>? stored = null;
            try
            {
                stored = await _cache
                    .GetAsync<Dictionary<string, AnimeClickRepairAttempt>>(
                        CacheKey,
                        int.MaxValue,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeClick repair ledger could not be read; starting empty");
            }

            if (stored is not null)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var pair in stored)
                {
                    if (pair.Value is null
                        || !Guid.TryParse(pair.Key, out var itemId)
                        || IsExpired(pair.Value, now)
                        || IsStaleVerdict(pair.Value))
                    {
                        continue;
                    }

                    _entries[itemId] = pair.Value;
                }
            }

            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Records one attempt. Called from refresh threads, so it only touches memory and schedules a
    /// debounced write: a 100-item batch produces one file write, not a hundred.
    /// </summary>
    public void Record(Guid itemId, AnimeClickRepairOutcome outcome, string detail)
    {
        if (itemId == Guid.Empty || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            itemId,
            _ => new AnimeClickRepairAttempt
            {
                Outcome = outcome.ToString(),
                Detail = detail ?? string.Empty,
                AttemptedAt = now,
                Attempts = 1,
                PluginVersion = CurrentPluginVersion
            },
            (_, existing) => new AnimeClickRepairAttempt
            {
                Outcome = outcome.ToString(),
                Detail = detail ?? string.Empty,
                AttemptedAt = now,
                Attempts = existing.Attempts + 1,
                PluginVersion = CurrentPluginVersion
            });

        ScheduleFlush();
    }

    public bool TryGetAttempt(Guid itemId, out AnimeClickRepairAttempt attempt)
        => _entries.TryGetValue(itemId, out attempt!);

    /// <summary>
    /// True when the last attempt tells the audit not to offer this item again yet.
    /// </summary>
    public bool IsSuppressed(Guid itemId, DateTimeOffset now, out AnimeClickRepairAttempt attempt)
    {
        if (!TryGetAttempt(itemId, out attempt))
        {
            return false;
        }

        return SuppressionWindow(attempt.Outcome) is { } window && Age(attempt, now) < window;
    }

    /// <summary>
    /// Age of an attempt, clamped at zero so a clock moved backwards cannot resurrect an item that
    /// was just tried, nor pin one forever.
    /// </summary>
    internal static TimeSpan Age(AnimeClickRepairAttempt attempt, DateTimeOffset now)
    {
        var age = now - attempt.AttemptedAt;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    internal static TimeSpan? SuppressionWindow(string outcome)
    {
        if (string.Equals(outcome, nameof(AnimeClickRepairOutcome.NoSource), StringComparison.Ordinal))
        {
            return NoSourceSuppression;
        }

        return string.Equals(
            outcome,
            nameof(AnimeClickRepairOutcome.WaitingTranslation),
            StringComparison.Ordinal)
            ? WaitingSuppression
            : null;
    }

    /// <summary>
    /// The audit's short state name for one item: what the user needs to read on the row.
    /// </summary>
    internal static string DescribeState(string outcome)
        => outcome switch
        {
            nameof(AnimeClickRepairOutcome.Applied) => "applied",
            nameof(AnimeClickRepairOutcome.WaitingTranslation) => "waiting-translation",
            nameof(AnimeClickRepairOutcome.NoSource) => "no-source",
            nameof(AnimeClickRepairOutcome.Disabled) => "disabled",
            nameof(AnimeClickRepairOutcome.Blocked) => "blocked",
            nameof(AnimeClickRepairOutcome.Error) => "error",
            _ => "attempted"
        };

    /// <summary>
    /// Writes the ledger now. Exposed for shutdown and for tests that must observe persistence.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = _entries
                .Where(pair => !IsExpired(pair.Value, now))
                .OrderByDescending(pair => pair.Value.AttemptedAt)
                .Take(MaximumEntries)
                .ToDictionary(
                    pair => pair.Key.ToString("N", CultureInfo.InvariantCulture),
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);

            await _cache.SetAsync(CacheKey, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The ledger is an optimization over re-attempting: losing a write costs one wasted
            // repair, never a metadata failure.
            _logger.LogWarning(ex, "AnimeClick repair ledger could not be written");
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Only the timer is released: a flush started a moment ago may still be running, and
        // disposing its gate underneath it would throw on a background thread during shutdown.
        _flushTimer.Dispose();
    }

    private static bool IsExpired(AnimeClickRepairAttempt attempt, DateTimeOffset now)
        => Age(attempt, now) > Retention;

    /// <summary>
    /// The current plugin version, read from the assembly so it is also correct under test.
    /// </summary>
    internal static string CurrentPluginVersion { get; } =
        typeof(AnimeClickRepairLedger).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// True for a "no source" verdict issued by a different plugin version. Each release can add a
    /// source or a way of matching one — 0.5.4.0 taught the chain to read absolute episode numbers,
    /// which alone turned hundreds of "no source" answers into real synopses — so inheriting those
    /// verdicts would keep hiding episodes that are now resolvable. Everything else is kept: an
    /// applied repair or a lock does not become wrong across an upgrade.
    /// </summary>
    private static bool IsStaleVerdict(AnimeClickRepairAttempt attempt)
        => string.Equals(attempt.Outcome, nameof(AnimeClickRepairOutcome.NoSource), StringComparison.Ordinal)
            && !string.Equals(attempt.PluginVersion, CurrentPluginVersion, StringComparison.Ordinal);

    private void ScheduleFlush()
    {
        try
        {
            _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced this attempt; the in-memory record is still correct for this session.
        }
    }

    private void TriggerFlush()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = FlushAsync(CancellationToken.None);
    }
}
