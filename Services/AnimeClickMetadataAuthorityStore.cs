using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Keeps the authoritative values emitted by AnimeClick only for the short gap
/// between its remote provider and the matching post-merge custom provider.
/// Every remote invocation starts a lease that invalidates an older snapshot for
/// the same item; failed, cancelled and early-returning invocations publish none.
/// </summary>
public static class AnimeClickMetadataAuthorityStore
{
    private static readonly ConcurrentDictionary<string, AuthoritySnapshot> Snapshots =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, AuthorityAttempt> ActiveAttempts =
        new(StringComparer.Ordinal);

    private static readonly object[] LifecycleGates = CreateLifecycleGates();

    // This is deliberately much shorter than metadata/cache TTLs. It only covers
    // the remaining providers in one Jellyfin refresh, not a later refresh.
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromSeconds(5);

    // The full expiry scan runs at most once per interval. Begin() is invoked for every item
    // in a library refresh, so running an unconditional O(n) scan there would make a refresh
    // O(n^2). Per-item snapshots are still removed eagerly by Begin/Apply regardless.
    private static readonly long PruneIntervalTicks = TimeSpan.FromSeconds(5).Ticks;
    private static long _lastPruneUtcTicks;

    /// <summary>
    /// Starts one authority attempt and removes a snapshot left by an earlier
    /// refresh of the same item. Dispose without Capture represents an early
    /// return, cancellation or failure and leaves no applicable snapshot.
    /// </summary>
    public static AuthorityLease<TItem> Begin<TItem>(string? path, string? animeClickId)
        where TItem : BaseItem
    {
        var now = DateTimeOffset.UtcNow;
        var key = BuildKey(typeof(TItem), path, animeClickId);
        var token = Guid.NewGuid();
        var identity = NormalizeAnimeClickIdentity(animeClickId);

        PruneExpiredState(now);
        if (key is not null)
        {
            lock (GetLifecycleGate(key))
            {
                Snapshots.TryRemove(key, out _);
                ActiveAttempts[key] = new AuthorityAttempt(token, now);
            }
        }

        return new AuthorityLease<TItem>(key, identity, token, now);
    }

    public static ItemUpdateType Apply<TItem>(TItem item)
        where TItem : BaseItem
    {
        var key = BuildKey(typeof(TItem), item.Path, item.GetProviderId("AnimeClick"));
        if (key is null)
        {
            return ItemUpdateType.None;
        }

        AuthoritySnapshot? snapshot;
        lock (GetLifecycleGate(key))
        {
            // Consume regardless of locks or validation so this refresh can never
            // leave a snapshot available to a later invocation.
            if (!Snapshots.TryRemove(key, out snapshot))
            {
                return ItemUpdateType.None;
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (snapshot.CreatedAt > now + MaximumFutureClockSkew
            || now - snapshot.CreatedAt > SnapshotLifetime
            || item.IsLocked
            || (IsIdentityBoundKey(key)
                && !string.Equals(
                    snapshot.AnimeClickIdentity,
                    NormalizeAnimeClickIdentity(item.GetProviderId("AnimeClick")),
                    StringComparison.Ordinal)))
        {
            return ItemUpdateType.None;
        }

        var changed = false;
        var lockedFields = item.LockedFields ?? [];

        if (!lockedFields.Contains(MetadataField.Name)
            && !string.IsNullOrWhiteSpace(snapshot.Name)
            && !string.Equals(item.Name, snapshot.Name, StringComparison.Ordinal))
        {
            item.Name = snapshot.Name;
            changed = true;
        }

        if (!lockedFields.Contains(MetadataField.Overview)
            && !string.IsNullOrWhiteSpace(snapshot.Overview)
            && !string.Equals(item.Overview, snapshot.Overview, StringComparison.Ordinal))
        {
            item.Overview = snapshot.Overview;
            changed = true;
        }

        if (!lockedFields.Contains(MetadataField.Genres)
            && snapshot.Genres.Length > 0
            && !item.Genres.SequenceEqual(snapshot.Genres, StringComparer.Ordinal))
        {
            item.Genres = snapshot.Genres;
            changed = true;
        }

        if (!lockedFields.Contains(MetadataField.Tags) && snapshot.Tags.Length > 0)
        {
            var mergedTags = snapshot.Tags
                .Concat(item.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!item.Tags.SequenceEqual(mergedTags, StringComparer.Ordinal))
            {
                item.Tags = mergedTags;
                changed = true;
            }
        }

        if (!lockedFields.Contains(MetadataField.ProductionLocations)
            && snapshot.ProductionLocations.Length > 0)
        {
            var mergedLocations = snapshot.ProductionLocations
                .Concat(item.ProductionLocations)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!item.ProductionLocations.SequenceEqual(mergedLocations, StringComparer.Ordinal))
            {
                item.ProductionLocations = mergedLocations;
                changed = true;
            }
        }

        if (!lockedFields.Contains(MetadataField.Studios)
            && snapshot.Studios.Length > 0
            && !item.Studios.SequenceEqual(snapshot.Studios, StringComparer.Ordinal))
        {
            item.Studios = snapshot.Studios;
            changed = true;
        }

        if (!lockedFields.Contains(MetadataField.OfficialRating)
            && !string.IsNullOrWhiteSpace(snapshot.OfficialRating)
            && !string.Equals(item.OfficialRating, snapshot.OfficialRating, StringComparison.Ordinal))
        {
            item.OfficialRating = snapshot.OfficialRating;
            changed = true;
        }

        if (!lockedFields.Contains(MetadataField.Runtime)
            && snapshot.RunTimeTicks.HasValue
            && item.RunTimeTicks != snapshot.RunTimeTicks)
        {
            item.RunTimeTicks = snapshot.RunTimeTicks;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.OriginalTitle)
            && !string.Equals(item.OriginalTitle, snapshot.OriginalTitle, StringComparison.Ordinal))
        {
            item.OriginalTitle = snapshot.OriginalTitle;
            changed = true;
        }

        if (snapshot.ProductionYear.HasValue && item.ProductionYear != snapshot.ProductionYear)
        {
            item.ProductionYear = snapshot.ProductionYear;
            changed = true;
        }

        if (snapshot.PremiereDate.HasValue && item.PremiereDate != snapshot.PremiereDate)
        {
            item.PremiereDate = snapshot.PremiereDate;
            changed = true;
        }

        if (snapshot.CommunityRating.HasValue && item.CommunityRating != snapshot.CommunityRating)
        {
            item.CommunityRating = snapshot.CommunityRating;
            changed = true;
        }

        if (item is Series series
            && snapshot.SeriesStatus.HasValue
            && series.Status != snapshot.SeriesStatus)
        {
            series.Status = snapshot.SeriesStatus;
            changed = true;
        }

        if (snapshot.RemoteTrailers.Count > 0)
        {
            var trailers = snapshot.RemoteTrailers
                .Concat(item.RemoteTrailers)
                .Where(trailer => !string.IsNullOrWhiteSpace(trailer.Url))
                .DistinctBy(trailer => trailer.Url, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!item.RemoteTrailers.Select(trailer => trailer.Url)
                .SequenceEqual(trailers.Select(trailer => trailer.Url), StringComparer.OrdinalIgnoreCase))
            {
                item.RemoteTrailers = trailers;
                changed = true;
            }
        }

        foreach (var providerId in snapshot.ProviderIds)
        {
            if (string.Equals(providerId.Key, "AnimeClick", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(item.GetProviderId(providerId.Key), providerId.Value, StringComparison.Ordinal))
                {
                    item.SetProviderId(providerId.Key, providerId.Value);
                    changed = true;
                }
            }
            else if (item.ProviderIds.TryAdd(providerId.Key, providerId.Value))
            {
                changed = true;
            }
        }

        return changed ? ItemUpdateType.MetadataEdit : ItemUpdateType.None;
    }

    private static bool Publish<TItem>(AuthorityLease<TItem> lease, TItem item)
        where TItem : BaseItem
    {
        var now = DateTimeOffset.UtcNow;
        if (lease.IsDisposed
            || now < lease.StartedAt - MaximumFutureClockSkew
            || now - lease.StartedAt > SnapshotLifetime)
        {
            return false;
        }

        var itemIdentity = NormalizeAnimeClickIdentity(item.GetProviderId("AnimeClick"));
        var key = lease.InitialKey ?? BuildKey(typeof(TItem), null, item.GetProviderId("AnimeClick"));
        if (key is null)
        {
            return false;
        }

        // A path-scoped lease is already correlated to the same library item and may
        // legitimately canonicalize its provider ID during this refresh. ID-scoped
        // leases still require the exact normalized identity they started with.
        if (IsIdentityBoundKey(key)
            && lease.InitialIdentity is not null
            && (itemIdentity is null
                || !string.Equals(lease.InitialIdentity, itemIdentity, StringComparison.Ordinal)))
        {
            return false;
        }

        lock (GetLifecycleGate(key))
        {
            if (lease.InitialKey is not null)
            {
                if (!ActiveAttempts.TryGetValue(key, out var attempt)
                    || attempt.Token != lease.Token
                    || attempt.StartedAt != lease.StartedAt)
                {
                    return false;
                }
            }
            else if (ActiveAttempts.ContainsKey(key))
            {
                // A path/ID-correlated refresh owns this item. An uncorrelated
                // title lookup must not overwrite its lifecycle.
                return false;
            }

            Snapshots[key] = AuthoritySnapshot.From(item, itemIdentity, now, lease.Token);
            RemoveAttemptIfOwned(key, lease.Token);
            lease.MarkPublished();
            return true;
        }
    }

    private static void Release<TItem>(AuthorityLease<TItem> lease)
        where TItem : BaseItem
    {
        if (lease.InitialKey is null)
        {
            return;
        }

        lock (GetLifecycleGate(lease.InitialKey))
        {
            RemoveAttemptIfOwned(lease.InitialKey, lease.Token);
            if (!lease.WasPublished
                && Snapshots.TryGetValue(lease.InitialKey, out var snapshot)
                && snapshot.OwnerToken == lease.Token)
            {
                Snapshots.TryRemove(lease.InitialKey, out _);
            }
        }
    }

    private static string? BuildKey(Type itemType, string? path, string? animeClickId)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return $"{itemType.FullName}|path|{path.Trim()}";
        }

        var identity = NormalizeAnimeClickIdentity(animeClickId);
        return identity is null ? null : $"{itemType.FullName}|id|{identity}";
    }

    private static string? NormalizeAnimeClickIdentity(string? animeClickId)
    {
        if (!AnimeClickClient.TryNormalizeAnimeClickId(animeClickId, out var normalized))
        {
            return null;
        }

        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? normalized[..slash] : normalized;
    }

    private static bool IsIdentityBoundKey(string key)
    {
        var markerIndex = key.IndexOf('|');
        return markerIndex >= 0
            && key.AsSpan(markerIndex).StartsWith("|id|", StringComparison.Ordinal);
    }

    private static void RemoveAttemptIfOwned(string key, Guid token)
    {
        if (ActiveAttempts.TryGetValue(key, out var attempt) && attempt.Token == token)
        {
            ((ICollection<KeyValuePair<string, AuthorityAttempt>>)ActiveAttempts)
                .Remove(new KeyValuePair<string, AuthorityAttempt>(key, attempt));
        }
    }

    private static void PruneExpiredState(DateTimeOffset now)
    {
        // Throttle: only one thread runs the O(n) scan per interval; others return immediately.
        // This reclaims entries for items that are never refreshed again; entries for items
        // that are refreshed are already removed eagerly by Begin/Apply.
        var nowTicks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastPruneUtcTicks);
        if (nowTicks - last < PruneIntervalTicks
            || Interlocked.CompareExchange(ref _lastPruneUtcTicks, nowTicks, last) != last)
        {
            return;
        }

        var threshold = now - SnapshotLifetime;
        foreach (var pair in Snapshots)
        {
            if (pair.Value.CreatedAt < threshold || pair.Value.CreatedAt > now + MaximumFutureClockSkew)
            {
                lock (GetLifecycleGate(pair.Key))
                {
                    if (Snapshots.TryGetValue(pair.Key, out var current)
                        && current == pair.Value)
                    {
                        Snapshots.TryRemove(pair.Key, out _);
                    }
                }
            }
        }

        foreach (var pair in ActiveAttempts)
        {
            if (pair.Value.StartedAt < threshold || pair.Value.StartedAt > now + MaximumFutureClockSkew)
            {
                lock (GetLifecycleGate(pair.Key))
                {
                    if (ActiveAttempts.TryGetValue(pair.Key, out var current)
                        && current == pair.Value)
                    {
                        RemoveAttemptIfOwned(pair.Key, pair.Value.Token);
                    }
                }
            }
        }
    }

    private static object[] CreateLifecycleGates()
    {
        var gates = new object[64];
        for (var index = 0; index < gates.Length; index++)
        {
            gates[index] = new object();
        }

        return gates;
    }

    private static object GetLifecycleGate(string key)
        => LifecycleGates[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % LifecycleGates.Length)];

    private sealed record AuthorityAttempt(Guid Token, DateTimeOffset StartedAt);

    public sealed class AuthorityLease<TItem> : IDisposable
        where TItem : BaseItem
    {
        private int _disposed;
        private int _published;

        internal AuthorityLease(
            string? initialKey,
            string? initialIdentity,
            Guid token,
            DateTimeOffset startedAt)
        {
            InitialKey = initialKey;
            InitialIdentity = initialIdentity;
            Token = token;
            StartedAt = startedAt;
        }

        internal string? InitialKey { get; }
        internal string? InitialIdentity { get; }
        internal Guid Token { get; }
        internal DateTimeOffset StartedAt { get; }
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        internal bool WasPublished => Volatile.Read(ref _published) != 0;

        public bool Capture(TItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return Publish(this, item);
        }

        internal void MarkPublished() => Interlocked.Exchange(ref _published, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release(this);
            }
        }
    }

    private sealed record AuthoritySnapshot(
        Guid OwnerToken,
        DateTimeOffset CreatedAt,
        string? AnimeClickIdentity,
        string? Name,
        string? OriginalTitle,
        string? Overview,
        int? ProductionYear,
        DateTime? PremiereDate,
        float? CommunityRating,
        string? OfficialRating,
        long? RunTimeTicks,
        string[] Genres,
        string[] Tags,
        string[] Studios,
        string[] ProductionLocations,
        IReadOnlyList<MediaUrl> RemoteTrailers,
        IReadOnlyDictionary<string, string> ProviderIds,
        SeriesStatus? SeriesStatus)
    {
        public static AuthoritySnapshot From(
            BaseItem item,
            string? animeClickIdentity,
            DateTimeOffset createdAt,
            Guid ownerToken)
        {
            var trailers = item.RemoteTrailers
                .Select(trailer => new MediaUrl { Name = trailer.Name, Url = trailer.Url })
                .ToArray();
            var providerIds = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase);

            return new AuthoritySnapshot(
                ownerToken,
                createdAt,
                animeClickIdentity,
                item.Name,
                item.OriginalTitle,
                item.Overview,
                item.ProductionYear,
                item.PremiereDate,
                item.CommunityRating,
                item.OfficialRating,
                item.RunTimeTicks,
                item.Genres.ToArray(),
                item.Tags.ToArray(),
                item.Studios.ToArray(),
                item.ProductionLocations.ToArray(),
                trailers,
                providerIds,
                (item as Series)?.Status);
        }
    }
}
