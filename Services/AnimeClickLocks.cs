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
    /// Removes the whole-item lock and the lock of one field, or of every field when
    /// <paramref name="field"/> is null. Returns false, and saves nothing, when nothing was locked.
    /// </summary>
    public static async Task<bool> TryUnlockAsync(
        ILibraryManager libraryManager,
        BaseItem item,
        MetadataField? field,
        CancellationToken cancellationToken)
    {
        var lockedFields = item.LockedFields ?? [];
        var fieldIsLocked = field is { } wanted ? lockedFields.Contains(wanted) : lockedFields.Length > 0;
        if (!item.IsLocked && !fieldIsLocked)
        {
            return false;
        }

        item.IsLocked = false;
        item.LockedFields = field is { } removed ? [.. lockedFields.Where(locked => locked != removed)] : [];
        await libraryManager
            .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}
