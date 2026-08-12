using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Reapplies AnimeClick values after Jellyfin's normal merge. ValidationOnly repairs are handled
/// through a correlated intent and may update Overview only; ordinary refreshes retain the full
/// authority-store behavior.
/// </summary>
public abstract class AnimeClickAuthorityProvider<TItem> :
    ICustomMetadataProvider<TItem>,
    IHasOrder,
    IHasItemChangeMonitor
    where TItem : BaseItem
{
    private readonly AnimeClickMetadataRefreshIntentRegistry _intentRegistry;
    private readonly IAnimeClickOverviewResolver _overviewResolver;
    private readonly ILogger _logger;

    protected AnimeClickAuthorityProvider(
        AnimeClickMetadataRefreshIntentRegistry intentRegistry,
        IAnimeClickOverviewResolver overviewResolver,
        ILogger logger)
    {
        _intentRegistry = intentRegistry;
        _overviewResolver = overviewResolver;
        _logger = logger;
    }

    public string Name => "AnimeClick Authority";

    public int Order => 100;

    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
        => item is TItem
            && _intentRegistry.HasIntent(directoryService, item, MetadataField.Overview);

    public async Task<ItemUpdateType> FetchAsync(
        TItem item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        if (_intentRegistry.TryTake(
                options.DirectoryService,
                item,
                MetadataField.Overview,
                out var intent))
        {
            // This branch is the complete ValidationOnly repair. Never call Apply here: its
            // snapshot intentionally contains names, genres, studios, IDs and other fields that
            // an Overview-only operation is not authorized to change.
            if (item.IsLocked
                || (item.LockedFields?.Contains(MetadataField.Overview) ?? false)
                || !string.Equals(item.Overview, intent.ExpectedOverview, System.StringComparison.Ordinal)
                || !AnimeClickOverviewRepairPolicy.CanReplace(item.Overview))
            {
                return ItemUpdateType.None;
            }

            var overview = intent.Overview;
            if (string.IsNullOrWhiteSpace(overview))
            {
                overview = await _overviewResolver.ResolveAsync(item, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Resolution can perform network/cache work. Revalidate the exact source state and
            // locks afterward so a manual/native Italian correction made while it was in flight
            // always wins over this delayed repair.
            if (item.IsLocked
                || (item.LockedFields?.Contains(MetadataField.Overview) ?? false)
                || !string.Equals(item.Overview, intent.ExpectedOverview, System.StringComparison.Ordinal)
                || !AnimeClickOverviewRepairPolicy.CanReplace(item.Overview)
                || string.IsNullOrWhiteSpace(overview)
                || string.Equals(item.Overview, overview, System.StringComparison.Ordinal))
            {
                return ItemUpdateType.None;
            }

            item.Overview = overview;
            _logger.LogInformation(
                "AnimeClick authority applied Overview-only repair for item={ItemId} type={ItemType} reason={Reason}",
                item.Id,
                item.GetType().Name,
                intent.Reason);
            return ItemUpdateType.MetadataEdit;
        }

        var updateType = AnimeClickMetadataAuthorityStore.Apply(item);
        if (updateType != ItemUpdateType.None)
        {
            _logger.LogDebug(
                "AnimeClick authority reapplied enabled {ItemType} fields for {Item}",
                item.GetType().Name,
                item.Path ?? item.Name);
        }

        return updateType;
    }
}

public sealed class AnimeClickSeriesAuthorityProvider : AnimeClickAuthorityProvider<Series>
{
    public AnimeClickSeriesAuthorityProvider(
        AnimeClickMetadataRefreshIntentRegistry intentRegistry,
        IAnimeClickOverviewResolver overviewResolver,
        ILogger<AnimeClickSeriesAuthorityProvider> logger)
        : base(intentRegistry, overviewResolver, logger)
    {
    }
}

public sealed class AnimeClickMovieAuthorityProvider : AnimeClickAuthorityProvider<Movie>
{
    public AnimeClickMovieAuthorityProvider(
        AnimeClickMetadataRefreshIntentRegistry intentRegistry,
        IAnimeClickOverviewResolver overviewResolver,
        ILogger<AnimeClickMovieAuthorityProvider> logger)
        : base(intentRegistry, overviewResolver, logger)
    {
    }
}

public sealed class AnimeClickEpisodeAuthorityProvider : AnimeClickAuthorityProvider<Episode>
{
    public AnimeClickEpisodeAuthorityProvider(
        AnimeClickMetadataRefreshIntentRegistry intentRegistry,
        IAnimeClickOverviewResolver overviewResolver,
        ILogger<AnimeClickEpisodeAuthorityProvider> logger)
        : base(intentRegistry, overviewResolver, logger)
    {
    }
}
