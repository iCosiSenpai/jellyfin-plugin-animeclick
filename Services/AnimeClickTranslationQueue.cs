using System;
using System.Collections.Concurrent;
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
/// cache, and handled by one worker because Ollama Cloud allows one active model.
/// </summary>
public sealed class AnimeClickTranslationQueue : IDisposable
{
    private const int QueueCapacity = 256;
    private static readonly TimeSpan FastFailureBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TimeoutBackoff = TimeSpan.FromMinutes(15);

    private readonly AnimeClickOllamaTranslator _translator;
    private readonly AnimeClickCacheService _cache;
    private readonly ILogger<AnimeClickTranslationQueue> _logger;
    private readonly Channel<TranslationWorkItem> _channel;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly Task _worker;
    private long _generation;
    private int _disposed;

    public AnimeClickTranslationQueue(
        AnimeClickOllamaTranslator translator,
        AnimeClickCacheService cache,
        ILogger<AnimeClickTranslationQueue> logger)
    {
        _translator = translator;
        _cache = cache;
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
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return AnimeClickTranslationQueueState.Invalid;
        }

        // Capture the generation before any asynchronous cache work. A clear that
        // starts after this point makes the eventual item stale; work entering while
        // the exclusive clear lease is active is rejected immediately.
        var generation = Volatile.Read(ref _generation);
        if ((generation & 1) != 0)
        {
            return AnimeClickTranslationQueueState.Invalidating;
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

        if (!_pending.TryAdd(workKey, 0))
        {
            return AnimeClickTranslationQueueState.AlreadyQueued;
        }

        var item = new TranslationWorkItem(
            workKey,
            sourceText,
            cacheScope,
            sourceIdentity,
            fieldName,
            sourceLanguage,
            targetLanguage,
            generation);
        if (!_channel.Writer.TryWrite(item))
        {
            _pending.TryRemove(workKey, out _);
            return AnimeClickTranslationQueueState.QueueFull;
        }

        _logger.LogInformation(
            "AnimeClick translation queued: source={Source} field={Field} pending={Pending}",
            sourceIdentity,
            fieldName,
            _pending.Count);
        return AnimeClickTranslationQueueState.Queued;
    }

    /// <summary>
    /// Starts an exclusive administrative cache invalidation. The lease spans the
    /// complete clear operation, so background publication cannot race its result.
    /// Generations are odd while the lease is active and even when work is accepted.
    /// </summary>
    public IDisposable BeginInvalidation()
    {
        _publicationGate.Wait();
        var generation = Interlocked.Increment(ref _generation);
        _logger.LogInformation(
            "AnimeClick translation queue invalidation started: generation={Generation} pending={Pending}",
            generation,
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
        if (string.IsNullOrWhiteSpace(configuration.OllamaCloudApiKey)
            || string.IsNullOrWhiteSpace(configuration.OllamaCloudModel)
            || string.IsNullOrWhiteSpace(sourceText)
            || !AnimeClickOllamaTranslator.TryNormalizeCloudEndpoint(
                configuration.OllamaCloudEndpoint,
                out var endpointUri))
        {
            return false;
        }

        var plain = AnimeClickOllamaTranslator.StripHtml(sourceText);
        if (string.IsNullOrWhiteSpace(plain))
        {
            return false;
        }

        workKey = AnimeClickOllamaTranslator.BuildTranslationCacheKey(
            cacheScope,
            sourceIdentity,
            fieldName,
            sourceLanguage,
            targetLanguage,
            configuration.OllamaCloudModel,
            endpointUri.AbsoluteUri,
            configuration.OllamaCloudApiKey,
            plain);
        return true;
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    if (item.Generation != Volatile.Read(ref _generation)
                        || !TryGetCurrentConfiguration(item, out var configuration))
                    {
                        _logger.LogInformation(
                            "AnimeClick background translation discarded before execution: source={Source} field={Field} reason=stale-profile-or-generation",
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
                    // Recheck generation/profile only after acquiring it, then publish
                    // the result (or backoff) while the clear endpoint is excluded.
                    await _publicationGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    try
                    {
                        if (item.Generation != Volatile.Read(ref _generation)
                            || !TryGetCurrentConfiguration(item, out _))
                        {
                            _logger.LogInformation(
                                "AnimeClick background translation discarded after execution: source={Source} field={Field} reason=stale-profile-or-generation",
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
                    _pending.TryRemove(item.WorkKey, out _);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal plugin shutdown.
        }
    }

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
            OllamaCloudApiKey = source.OllamaCloudApiKey,
            OllamaCloudEndpoint = source.OllamaCloudEndpoint,
            OllamaCloudModel = source.OllamaCloudModel,
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
        long Generation);
}

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
