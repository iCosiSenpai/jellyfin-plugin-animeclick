using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AnimeClick.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Runs cloud translations outside Jellyfin's metadata request path. Jobs are
/// bounded, process-wide, deduplicated by the same key used by the translation
/// cache, and handled by one worker because some services allow one active model at a time.
/// </summary>
public sealed class AnimeClickTranslationQueue : IDisposable
{
    private const int QueueCapacity = 256;
    private const int WorkerRestartLimit = 5;
    private static readonly TimeSpan WorkerRestartDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FastFailureBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TimeoutBackoff = TimeSpan.FromMinutes(15);

    private readonly AnimeClickAiTranslator _translator;
    private readonly AnimeClickCacheService _cache;
    private readonly AnimeClickMetadataRefreshScheduler _refreshScheduler;
    private readonly ILogger<AnimeClickTranslationQueue> _logger;
    private readonly Channel<TranslationWorkItem> _channel;
    private readonly ConcurrentDictionary<string, AnimeClickPendingTranslation> _pending =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly Task _worker;
    private long _generation;
    private int _disposed;

    public AnimeClickTranslationQueue(
        AnimeClickAiTranslator translator,
        AnimeClickCacheService cache,
        AnimeClickMetadataRefreshScheduler refreshScheduler,
        ILogger<AnimeClickTranslationQueue> logger)
    {
        _translator = translator;
        _cache = cache;
        _refreshScheduler = refreshScheduler;
        _logger = logger;
        _channel = Channel.CreateBounded<TranslationWorkItem>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public async Task<string?> GetCachedTranslationAsync(
        string sourceText,
        string cacheScope,
        string sourceIdentity,
        string fieldName,
        string sourceLanguage,
        string targetLanguage,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!TryBuildWorkKey(
                sourceText,
                cacheScope,
                sourceIdentity,
                fieldName,
                sourceLanguage,
                targetLanguage,
                configuration,
                out var workKey))
        {
            return null;
        }

        var cacheHours = configuration.TranslationCacheHours <= 0
            ? int.MaxValue
            : configuration.TranslationCacheHours;
        return await _cache
            .GetAsync<string?>(workKey, cacheHours, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AnimeClickTranslationQueueState> EnqueueAsync(
        string sourceText,
        string cacheScope,
        string sourceIdentity,
        string fieldName,
        string sourceLanguage,
        string targetLanguage,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        string? refreshPath = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return AnimeClickTranslationQueueState.Invalid;
        }

        if (!TryBuildWorkKey(
                sourceText,
                cacheScope,
                sourceIdentity,
                fieldName,
                sourceLanguage,
                targetLanguage,
                configuration,
                out var workKey))
        {
            return AnimeClickTranslationQueueState.Invalid;
        }

        // Cache inspection and pending registration are one atomic phase with respect to
        // administrative invalidation. A clear therefore either sees and invalidates this claim,
        // or completes before this enqueue starts; there is no gap after the clear's pending scan.
        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref _generation);
            if ((generation & 1) != 0)
            {
                return AnimeClickTranslationQueueState.Invalidating;
            }

        AnimeClickPendingRefreshTarget? refreshTarget = null;
        if (_refreshScheduler.TryCaptureOverviewByPath(refreshPath, out var expectedOverview))
        {
            refreshTarget = new AnimeClickPendingRefreshTarget(refreshPath!, expectedOverview);
        }

        var cached = await GetCachedTranslationAsync(
                sourceText,
                cacheScope,
                sourceIdentity,
                fieldName,
                sourceLanguage,
                targetLanguage,
                configuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return AnimeClickTranslationQueueState.Cached;
        }

        var backoffKey = GetBackoffKey(workKey);
        var backoff = await _cache
            .GetAsync<AnimeClickTranslationBackoff>(backoffKey, int.MaxValue, cancellationToken)
            .ConfigureAwait(false);
        if (backoff is not null && backoff.RetryAfterUtc > DateTime.UtcNow)
        {
            return AnimeClickTranslationQueueState.Backoff;
        }

        if (backoff is not null)
        {
            _cache.ClearKey(backoffKey);
        }

        while (true)
        {
            var currentGeneration = Volatile.Read(ref _generation);
            if (currentGeneration != generation || (currentGeneration & 1) != 0)
            {
                return AnimeClickTranslationQueueState.Invalidating;
            }

            var pending = new AnimeClickPendingTranslation(generation, refreshTarget);
            if (_pending.TryAdd(workKey, pending))
            {
                var item = new TranslationWorkItem(
                    workKey,
                    sourceText,
                    cacheScope,
                    sourceIdentity,
                    fieldName,
                    sourceLanguage,
                    targetLanguage,
                    generation,
                    pending);
                if (!_channel.Writer.TryWrite(item))
                {
                    pending.Seal();
                    RemovePending(workKey, pending);
                    return AnimeClickTranslationQueueState.QueueFull;
                }

                _logger.LogInformation(
                    "AnimeClick translation queued: source={Source} field={Field} pending={Pending}",
                    sourceIdentity,
                    fieldName,
                    _pending.Count);
                return AnimeClickTranslationQueueState.Queued;
            }

            if (_pending.TryGetValue(workKey, out var existing)
                && existing.TryJoin(generation, refreshTarget))
            {
                return AnimeClickTranslationQueueState.AlreadyQueued;
            }

            // The worker has sealed this claim or it belongs to a generation invalidated while
            // this enqueue was awaiting cache I/O. Remove only that exact instance: a new claim
            // may already have replaced it. Recheck publication/backoff before translating again.
            if (existing is not null)
            {
                RemovePending(workKey, existing);
            }

            cached = await GetCachedTranslationAsync(
                    sourceText,
                    cacheScope,
                    sourceIdentity,
                    fieldName,
                    sourceLanguage,
                    targetLanguage,
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return AnimeClickTranslationQueueState.Cached;
            }

            backoff = await _cache
                .GetAsync<AnimeClickTranslationBackoff>(backoffKey, int.MaxValue, cancellationToken)
                .ConfigureAwait(false);
            if (backoff is not null && backoff.RetryAfterUtc > DateTime.UtcNow)
            {
                return AnimeClickTranslationQueueState.Backoff;
            }

            if (backoff is not null)
            {
                _cache.ClearKey(backoffKey);
            }
        }
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    /// <summary>
    /// Starts an exclusive administrative cache invalidation. New enqueues are rejected while the
    /// lease is active. Existing work is invalidated only when its logical cache key matches the
    /// supplied predicate; a targeted clear for series B therefore cannot strand series A.
    /// </summary>
    public IDisposable BeginInvalidation(Func<string, bool>? shouldInvalidate = null)
    {
        _publicationGate.Wait();
        var generation = Interlocked.Increment(ref _generation);
        var invalidated = 0;
        foreach (var pair in _pending)
        {
            if (shouldInvalidate is null || shouldInvalidate(pair.Key))
            {
                pair.Value.Invalidate();
                RemovePending(pair.Key, pair.Value);
                invalidated++;
            }
        }

        _logger.LogInformation(
            "AnimeClick translation queue invalidation started: generation={Generation} invalidated={Invalidated} pending={Pending}",
            generation,
            invalidated,
            _pending.Count);
        return new TranslationInvalidationLease(this);
    }

    private void CompleteInvalidation()
    {
        var generation = Interlocked.Increment(ref _generation);
        _logger.LogInformation(
            "AnimeClick translation queue invalidation completed: generation={Generation} pending={Pending}",
            generation,
            _pending.Count);
        _publicationGate.Release();
    }

    internal static TimeSpan GetFailureBackoff(TimeSpan elapsed, int timeoutSeconds)
    {
        var effectiveTimeout = Math.Clamp(timeoutSeconds, 5, 120);
        return elapsed >= TimeSpan.FromSeconds(effectiveTimeout - 1)
            ? TimeoutBackoff
            : FastFailureBackoff;
    }

    internal static bool TryBuildWorkKey(
        string sourceText,
        string cacheScope,
        string sourceIdentity,
        string fieldName,
        string sourceLanguage,
        string targetLanguage,
        PluginConfiguration configuration,
        out string workKey)
    {
        workKey = string.Empty;
        if (!AnimeClickAiTranslator.IsConfigured(configuration, out var endpointUri)
            || string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        var plain = AnimeClickAiTranslator.StripHtml(sourceText);
        if (string.IsNullOrWhiteSpace(plain))
        {
            return false;
        }

        workKey = AnimeClickAiTranslator.BuildTranslationCacheKey(
            cacheScope,
            sourceIdentity,
            fieldName,
            sourceLanguage,
            targetLanguage,
            configuration.AiModel,
            endpointUri.AbsoluteUri,
            configuration.AiApiKey,
            plain);
        return true;
    }

    private async Task ProcessQueueAsync()
    {
        // Supervisor. The drain loop below used to be this whole method with only a cancellation
        // catch, so any other exception escaping it faulted this task with nobody observing it:
        // no log at any level, _pending never drained again, and from then on every EnqueueAsync
        // answered AlreadyQueued for those keys. The Italian synopsis feature stopped existing
        // silently until Jellyfin restarted. A fault is now reported and retried, bounded.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DrainQueueAsync().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nothing claimed can be trusted after an unexpected fault here, and releasing
                // the claims is what keeps EnqueueAsync from refusing those keys forever.
                foreach (var pending in _pending.Values)
                {
                    pending.Invalidate();
                }

                _pending.Clear();

                if (attempt >= WorkerRestartLimit)
                {
                    _logger.LogError(
                        ex,
                        "AnimeClick translation worker failed {Attempts} times and will not restart; episode synopses needing translation stay untranslated until Jellyfin is restarted",
                        attempt);
                    return;
                }

                _logger.LogError(
                    ex,
                    "AnimeClick translation worker faulted (attempt {Attempt} of {Limit}); restarting in {Delay}",
                    attempt,
                    WorkerRestartLimit,
                    WorkerRestartDelay);

                try
                {
                    await Task.Delay(WorkerRestartDelay, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task DrainQueueAsync()
    {
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    if (item.Pending.IsInvalidated
                        || !TryGetCurrentConfiguration(item, out var configuration))
                    {
                        _logger.LogInformation(
                            "AnimeClick background translation discarded before execution: source={Source} field={Field} reason=stale-profile-or-invalidated",
                            item.SourceIdentity,
                            item.FieldName);
                        continue;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    var translated = await _translator.TranslateMetadataFieldWithoutPublishingAsync(
                            item.SourceText,
                            item.CacheScope,
                            item.SourceIdentity,
                            item.FieldName,
                            item.SourceLanguage,
                            item.TargetLanguage,
                            configuration,
                            _shutdown.Token)
                        .ConfigureAwait(false);
                    stopwatch.Stop();

                    // Publication and administrative cache clearing share this gate.
                    // Recheck targeted invalidation/profile only after acquiring it, then publish
                    // the result (or backoff) while the clear endpoint is excluded.
                    await _publicationGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    try
                    {
                        if (item.Pending.IsInvalidated
                            || !TryGetCurrentConfiguration(item, out _))
                        {
                            _logger.LogInformation(
                                "AnimeClick background translation discarded after execution: source={Source} field={Field} reason=stale-profile-or-invalidated",
                                item.SourceIdentity,
                                item.FieldName);
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(translated))
                        {
                            await _cache.SetAsync(item.WorkKey, translated, _shutdown.Token)
                                .ConfigureAwait(false);
                            _cache.ClearKey(GetBackoffKey(item.WorkKey));
                            _logger.LogInformation(
                                "AnimeClick background translation completed: source={Source} field={Field} elapsedMs={ElapsedMs}",
                                item.SourceIdentity,
                                item.FieldName,
                                stopwatch.ElapsedMilliseconds);

                            // Seal only after publication so every path that joined this work key
                            // before the cache became visible receives a callback. Each target
                            // carries the Overview observed before translation started. The narrow
                            // provider re-runs the source chain instead of applying this payload
                            // directly, so newly available native Italian or a newer source wins.
                            var refreshTargets = item.Pending.Seal();
                            foreach (var target in refreshTargets)
                            {
                                _refreshScheduler.TryQueueByPathIfUnchanged(
                                    target.Path,
                                    MediaBrowser.Model.Entities.MetadataField.Overview,
                                    "background-translation-completed",
                                    target.ExpectedOverview,
                                    item.WorkKey);
                            }
                        }
                        else
                        {
                            var delay = GetFailureBackoff(
                                stopwatch.Elapsed,
                                configuration.EpisodeTranslationTimeoutSec);
                            await _cache.SetAsync(
                                    GetBackoffKey(item.WorkKey),
                                    new AnimeClickTranslationBackoff
                                    {
                                        RetryAfterUtc = DateTime.UtcNow.Add(delay),
                                        Reason = delay == TimeoutBackoff
                                            ? "timeout-or-slow-failure"
                                            : "translation-failure"
                                    },
                                    _shutdown.Token)
                                .ConfigureAwait(false);
                            _logger.LogWarning(
                                "AnimeClick background translation failed: source={Source} field={Field} elapsedMs={ElapsedMs}; retry suppressed for {BackoffMinutes} minutes",
                                item.SourceIdentity,
                                item.FieldName,
                                stopwatch.ElapsedMilliseconds,
                                delay.TotalMinutes);
                        }
                    }
                    finally
                    {
                        _publicationGate.Release();
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "AnimeClick background translation worker failed for source={Source} field={Field}",
                        item.SourceIdentity,
                        item.FieldName);
                }
                finally
                {
                    item.Pending.Seal();
                    RemovePending(item.WorkKey, item.Pending);
                }
            }
        }
    }

    private bool RemovePending(string workKey, AnimeClickPendingTranslation pending)
        => ((ICollection<KeyValuePair<string, AnimeClickPendingTranslation>>)_pending)
            .Remove(new KeyValuePair<string, AnimeClickPendingTranslation>(workKey, pending));

    private static bool TryGetCurrentConfiguration(
        TranslationWorkItem item,
        out PluginConfiguration configuration)
    {
        var current = global::AnimeClick.Plugin.Plugin.Instance?.Configuration;
        if (current is null)
        {
            configuration = null!;
            return false;
        }

        configuration = Snapshot(current);
        return configuration.EnableEpisodeSynopsisTranslation
            && TryBuildWorkKey(
                item.SourceText,
                item.CacheScope,
                item.SourceIdentity,
                item.FieldName,
                item.SourceLanguage,
                item.TargetLanguage,
                configuration,
                out var currentWorkKey)
            && string.Equals(currentWorkKey, item.WorkKey, StringComparison.Ordinal);
    }

    private static string GetBackoffKey(string workKey) => workKey + "::backoff";

    private static PluginConfiguration Snapshot(PluginConfiguration source)
        => new()
        {
            EnableEpisodeSynopsisTranslation = source.EnableEpisodeSynopsisTranslation,
            AiProvider = source.AiProvider,
            AiApiKey = source.AiApiKey,
            AiEndpoint = source.AiEndpoint,
            AiModel = source.AiModel,
            EpisodeTranslationTimeoutSec = source.EpisodeTranslationTimeoutSec,
            TranslationCacheHours = source.TranslationCacheHours,
            NegativeCacheHours = source.NegativeCacheHours
        };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        _shutdown.Cancel();

        bool workerCompleted;
        try
        {
            workerCompleted = _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation/fault during plugin shutdown is expected; the task is finished.
            workerCompleted = _worker.IsCompleted;
        }

        if (workerCompleted)
        {
            // The worker has stopped touching _shutdown/_publicationGate: dispose them now.
            _shutdown.Dispose();
            _publicationGate.Dispose();
        }
        else
        {
            // A translation is still in flight and may keep using the CTS token and the gate.
            // Defer disposal until it actually completes so we never trigger an
            // ObjectDisposedException on the worker thread during a slow shutdown.
            _worker.ContinueWith(
                _ =>
                {
                    _shutdown.Dispose();
                    _publicationGate.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class TranslationInvalidationLease : IDisposable
    {
        private AnimeClickTranslationQueue? _owner;

        public TranslationInvalidationLease(AnimeClickTranslationQueue owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.CompleteInvalidation();
        }
    }

    private sealed record TranslationWorkItem(
        string WorkKey,
        string SourceText,
        string CacheScope,
        string SourceIdentity,
        string FieldName,
        string SourceLanguage,
        string TargetLanguage,
        long Generation,
        AnimeClickPendingTranslation Pending);
}

/// <summary>
/// One shared translation claim. Equal content can be requested by several Jellyfin items; all
/// paths join until publication seals the claim, after which no refresh can be silently lost.
/// </summary>
internal sealed class AnimeClickPendingTranslation
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AnimeClickPendingRefreshTarget> _refreshTargets =
        new(StringComparer.Ordinal);
    private bool _sealed;
    private bool _invalidated;

    internal AnimeClickPendingTranslation(
        long generation,
        AnimeClickPendingRefreshTarget? refreshTarget)
    {
        Generation = generation;
        AddTarget(refreshTarget);
    }

    internal long Generation { get; private set; }

    internal bool IsInvalidated
    {
        get
        {
            lock (_gate)
            {
                return _invalidated;
            }
        }
    }

    internal bool TryJoin(long generation, AnimeClickPendingRefreshTarget? refreshTarget)
    {
        lock (_gate)
        {
            if (_sealed || _invalidated)
            {
                return false;
            }

            // A non-matching targeted clear advances the global enqueue generation but leaves this
            // work valid. EnqueueAsync has already rejected callers captured before that clear, so
            // a newer caller may safely rebase and join the unaffected claim.
            Generation = generation;
            AddTarget(refreshTarget);
            return true;
        }
    }

    internal IReadOnlyList<AnimeClickPendingRefreshTarget> Seal()
    {
        lock (_gate)
        {
            _sealed = true;
            return _refreshTargets.Values.ToArray();
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _invalidated = true;
            _sealed = true;
        }
    }

    private void AddTarget(AnimeClickPendingRefreshTarget? refreshTarget)
    {
        if (refreshTarget is not null)
        {
            // Keep the earliest observed state for a path. If it changes while the shared
            // translation is pending, the callback must fail closed rather than authorize anew.
            _refreshTargets.TryAdd(refreshTarget.Path, refreshTarget);
        }
    }
}

internal sealed record AnimeClickPendingRefreshTarget(string Path, string? ExpectedOverview);

public enum AnimeClickTranslationQueueState
{
    Invalid,
    Invalidating,
    Cached,
    Queued,
    AlreadyQueued,
    Backoff,
    QueueFull
}

internal sealed class AnimeClickTranslationBackoff
{
    public DateTime RetryAfterUtc { get; set; }

    public string Reason { get; set; } = string.Empty;
}
