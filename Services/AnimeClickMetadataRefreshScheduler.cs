using System;
using System.Collections.Concurrent;
using AnimeClick.Plugin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Queues narrow, non-destructive metadata refreshes for work completed outside
/// Jellyfin's original provider request. It is shared by background translation
/// publication and the administrative library-repair endpoint.
/// </summary>
public sealed class AnimeClickMetadataRefreshScheduler
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly AnimeClickMetadataRefreshIntentRegistry _intentRegistry;
    private readonly ILogger<AnimeClickMetadataRefreshScheduler> _logger;
    private readonly ConcurrentDictionary<RefreshClaim, long> _recentlyQueued = new();

    public AnimeClickMetadataRefreshScheduler(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        AnimeClickMetadataRefreshIntentRegistry intentRegistry,
        ILogger<AnimeClickMetadataRefreshScheduler> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _intentRegistry = intentRegistry;
        _logger = logger;
    }

    public bool TryQueueByPath(
        string? path,
        MetadataField field,
        string reason,
        string? overview = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var item = _libraryManager.FindByPath(path, isFolder: false);
        return item is not null && TryQueue(item, field, reason, overview);
    }

    /// <summary>
    /// Captures the database value before asynchronous translation starts. The resulting callback
    /// is authorized only while that exact value remains current.
    /// </summary>
    internal bool TryCaptureOverviewByPath(string? path, out string? overview)
    {
        overview = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var item = _libraryManager.FindByPath(path, isFolder: false);
        if (item is null)
        {
            return false;
        }

        overview = item.Overview;
        return true;
    }

    internal bool TryQueueByPathIfUnchanged(
        string path,
        MetadataField field,
        string reason,
        string? expectedOverview,
        string deduplicationKey)
    {
        var item = _libraryManager.FindByPath(path, isFolder: false);
        return item is not null
            && TryQueueCore(
                item,
                field,
                reason,
                overview: null,
                expectedOverview,
                deduplicationKey);
    }

    public bool TryQueue(
        BaseItem item,
        MetadataField field,
        string reason,
        string? overview = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return TryQueueCore(item, field, reason, overview, item.Overview, reason);
    }

    private bool TryQueueCore(
        BaseItem item,
        MetadataField field,
        string reason,
        string? overview,
        string? expectedOverview,
        string deduplicationKey)
    {
        // This scheduler exists for field-scoped repairs. Other metadata fields still use the
        // normal Jellyfin refresh flow; accepting one here would create an intent no custom
        // provider knows how to consume.
        if (field != MetadataField.Overview)
        {
            _logger.LogWarning(
                "AnimeClick narrow refresh rejected unsupported field={Field} item={ItemId}",
                field,
                item.Id);
            return false;
        }

        if (item.IsLocked || (item.LockedFields?.Contains(field) ?? false))
        {
            _logger.LogInformation(
                "AnimeClick refresh skipped: item={ItemId} field={Field} reason=locked",
                item.Id,
                field);
            return false;
        }

        // The authorization is state-scoped as well as field-scoped. Capturing before cloud work
        // starts closes the whole queue interval, not only the later QueueRefresh interval.
        if (!string.Equals(item.Overview, expectedOverview, StringComparison.Ordinal)
            || !AnimeClickOverviewRepairPolicy.CanReplace(item.Overview))
        {
            _logger.LogInformation(
                "AnimeClick Overview refresh skipped: item={ItemId} reason=current-value-changed-or-not-repairable",
                item.Id);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(overview)
            && string.Equals(item.Overview, overview, StringComparison.Ordinal))
        {
            return false;
        }

        // Different translation sources use different work keys, so completion B cannot be
        // suppressed for thirty seconds merely because completion A targeted the same item.
        var claim = new RefreshClaim(item.Id, field, deduplicationKey);
        if (!TryClaim(claim))
        {
            _logger.LogDebug(
                "AnimeClick refresh deduplicated: item={ItemId} field={Field} reason={Reason}",
                item.Id,
                field,
                reason);
            return false;
        }

        var directoryService = new DirectoryService(_fileSystem);
        try
        {
            var options = new MetadataRefreshOptions(directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ImageRefreshMode = MetadataRefreshMode.None,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
                IsAutomated = true
            };
            _intentRegistry.Register(
                directoryService,
                item,
                field,
                reason,
                overview,
                expectedOverview);
            _providerManager.QueueRefresh(item.Id, options, RefreshPriority.Low);
            _logger.LogInformation(
                "AnimeClick metadata refresh queued: item={ItemId} type={ItemType} field={Field} reason={Reason}",
                item.Id,
                item.GetType().Name,
                field,
                reason);
            return true;
        }
        catch (Exception ex)
        {
            _intentRegistry.Cancel(directoryService);
            _recentlyQueued.TryRemove(claim, out _);
            _logger.LogWarning(
                ex,
                "AnimeClick could not queue metadata refresh for item={ItemId} field={Field}",
                item.Id,
                field);
            return false;
        }
    }

    private bool TryClaim(RefreshClaim claim)
    {
        var now = DateTime.UtcNow.Ticks;
        var cutoff = now - DuplicateWindow.Ticks;
        while (true)
        {
            if (_recentlyQueued.TryGetValue(claim, out var previous))
            {
                if (previous >= cutoff)
                {
                    return false;
                }

                if (_recentlyQueued.TryUpdate(claim, now, previous))
                {
                    return true;
                }

                continue;
            }

            if (_recentlyQueued.TryAdd(claim, now))
            {
                return true;
            }
        }
    }

    private readonly record struct RefreshClaim(Guid ItemId, MetadataField Field, string Reason);
}
