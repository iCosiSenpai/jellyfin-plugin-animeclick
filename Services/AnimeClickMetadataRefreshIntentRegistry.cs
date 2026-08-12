using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Correlates one queued ValidationOnly refresh with the custom provider invocation that is
/// allowed to handle it. The DirectoryService instance is unique to MetadataRefreshOptions and
/// therefore keeps a narrow refresh from affecting another item or a later normal refresh.
/// </summary>
public sealed class AnimeClickMetadataRefreshIntentRegistry
{
    // Low-priority Jellyfin queues can remain backlogged during scans. Keep the correlation long
    // enough for a bounded 100-item repair batch while still reclaiming abandoned queue entries.
    private static readonly TimeSpan IntentLifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<IDirectoryService, AnimeClickMetadataRefreshIntent> _intents =
        new(DirectoryServiceReferenceComparer.Instance);

    internal void Register(
        IDirectoryService directoryService,
        BaseItem item,
        MetadataField field,
        string reason,
        string? overview)
        => Register(directoryService, item, field, reason, overview, item.Overview);

    internal void Register(
        IDirectoryService directoryService,
        BaseItem item,
        MetadataField field,
        string reason,
        string? overview,
        string? expectedOverview)
    {
        ArgumentNullException.ThrowIfNull(directoryService);
        ArgumentNullException.ThrowIfNull(item);

        var now = DateTimeOffset.UtcNow;
        PruneExpired(now);
        _intents[directoryService] = new AnimeClickMetadataRefreshIntent(
            item.Id,
            field,
            reason,
            overview,
            expectedOverview,
            now);
    }

    internal bool HasIntent(
        IDirectoryService directoryService,
        BaseItem item,
        MetadataField field)
    {
        ArgumentNullException.ThrowIfNull(directoryService);
        ArgumentNullException.ThrowIfNull(item);

        if (!_intents.TryGetValue(directoryService, out var intent))
        {
            return false;
        }

        if (IsExpired(intent, DateTimeOffset.UtcNow))
        {
            RemoveIfCurrent(directoryService, intent);
            return false;
        }

        return intent.ItemId == item.Id && intent.Field == field;
    }

    internal bool TryTake(
        IDirectoryService directoryService,
        BaseItem item,
        MetadataField field,
        out AnimeClickMetadataRefreshIntent intent)
    {
        intent = null!;
        if (!HasIntent(directoryService, item, field)
            || !_intents.TryGetValue(directoryService, out var candidate)
            || !RemoveIfCurrent(directoryService, candidate))
        {
            return false;
        }

        intent = candidate;
        return true;
    }

    internal void Cancel(IDirectoryService directoryService)
        => _intents.TryRemove(directoryService, out _);

    private static bool IsExpired(AnimeClickMetadataRefreshIntent intent, DateTimeOffset now)
        => intent.CreatedAt > now + TimeSpan.FromSeconds(5)
            || now - intent.CreatedAt > IntentLifetime;

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _intents)
        {
            if (IsExpired(pair.Value, now))
            {
                RemoveIfCurrent(pair.Key, pair.Value);
            }
        }
    }

    private bool RemoveIfCurrent(
        IDirectoryService directoryService,
        AnimeClickMetadataRefreshIntent intent)
        => ((ICollection<KeyValuePair<IDirectoryService, AnimeClickMetadataRefreshIntent>>)_intents)
            .Remove(new KeyValuePair<IDirectoryService, AnimeClickMetadataRefreshIntent>(directoryService, intent));

    private sealed class DirectoryServiceReferenceComparer : IEqualityComparer<IDirectoryService>
    {
        public static DirectoryServiceReferenceComparer Instance { get; } = new();

        public bool Equals(IDirectoryService? left, IDirectoryService? right)
            => ReferenceEquals(left, right);

        public int GetHashCode(IDirectoryService value)
            => RuntimeHelpers.GetHashCode(value);
    }
}

internal sealed record AnimeClickMetadataRefreshIntent(
    Guid ItemId,
    MetadataField Field,
    string Reason,
    string? Overview,
    string? ExpectedOverview,
    DateTimeOffset CreatedAt);

internal static class AnimeClickOverviewRepairPolicy
{
    internal static bool CanReplace(string? overview)
        => string.IsNullOrWhiteSpace(overview)
            || AnimeClickMetadataLanguageDetector.Detect(overview).Language == AnimeClickTextLanguage.English;
}
