using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Jellyfin's metadata locks, as the plugin reads and removes them. A lock is the administrator's
/// protection of a manual edit: it is removed only on an explicit request, and only as far as that
/// request needs.
/// </summary>
internal static class AnimeClickLocks
{
    /// <summary>True when the whole item or this field of it is locked.</summary>
    public static bool IsLocked(BaseItem item, MetadataField field)
        => item.IsLocked || (item.LockedFields?.Contains(field) ?? false);

    /// <summary>
    /// Removes the lock of one field, or every lock when <paramref name="field"/> is null, and saves.
    /// Returns false, and saves nothing, when nothing was locked.
    /// </summary>
    public static async Task<bool> TryUnlockAsync(
        ILibraryManager libraryManager,
        BaseItem item,
        MetadataField? field,
        CancellationToken cancellationToken)
    {
        if (!Release(item, field))
        {
            return false;
        }

        await libraryManager
            .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The in-memory half of <see cref="TryUnlockAsync"/>, for a caller that saves the item itself
    /// together with the value the unlock was for.
    /// </summary>
    /// <remarks>
    /// A whole-item lock asked to release one field keeps every other field locked: the item
    /// becomes "all fields locked but this one" instead of losing every protection at once.
    /// </remarks>
    public static bool Release(BaseItem item, MetadataField? field)
    {
        var lockedFields = item.LockedFields ?? [];
        var fieldIsLocked = field is { } wanted ? lockedFields.Contains(wanted) : lockedFields.Length > 0;
        if (!item.IsLocked && !fieldIsLocked)
        {
            return false;
        }

        if (field is not { } released)
        {
            item.IsLocked = false;
            item.LockedFields = [];
            return true;
        }

        item.LockedFields = item.IsLocked
            ? [.. Enum.GetValues<MetadataField>().Where(locked => locked != released)]
            : [.. lockedFields.Where(locked => locked != released)];
        item.IsLocked = false;
        return true;
    }
}
