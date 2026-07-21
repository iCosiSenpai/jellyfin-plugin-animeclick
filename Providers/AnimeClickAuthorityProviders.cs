using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Reapplies the AnimeClick series fields after Jellyfin has merged all remote providers.
/// </summary>
public sealed class AnimeClickSeriesAuthorityProvider : ICustomMetadataProvider<Series>, IHasOrder
{
    private readonly ILogger<AnimeClickSeriesAuthorityProvider> _logger;

    public AnimeClickSeriesAuthorityProvider(ILogger<AnimeClickSeriesAuthorityProvider> logger)
    {
        _logger = logger;
    }

    public string Name => "AnimeClick Authority";

    public int Order => 100;

    public Task<ItemUpdateType> FetchAsync(
        Series item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        var updateType = AnimeClickMetadataAuthorityStore.Apply(item);
        if (updateType != ItemUpdateType.None)
        {
            _logger.LogDebug("AnimeClick authority reapplied enabled series fields for {Item}", item.Path ?? item.Name);
        }

        return Task.FromResult(updateType);
    }
}

/// <summary>
/// Reapplies the AnimeClick movie fields after Jellyfin has merged all remote providers.
/// </summary>
public sealed class AnimeClickMovieAuthorityProvider : ICustomMetadataProvider<Movie>, IHasOrder
{
    private readonly ILogger<AnimeClickMovieAuthorityProvider> _logger;

    public AnimeClickMovieAuthorityProvider(ILogger<AnimeClickMovieAuthorityProvider> logger)
    {
        _logger = logger;
    }

    public string Name => "AnimeClick Authority";

    public int Order => 100;

    public Task<ItemUpdateType> FetchAsync(
        Movie item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        var updateType = AnimeClickMetadataAuthorityStore.Apply(item);
        if (updateType != ItemUpdateType.None)
        {
            _logger.LogDebug("AnimeClick authority reapplied enabled movie fields for {Item}", item.Path ?? item.Name);
        }

        return Task.FromResult(updateType);
    }
}

/// <summary>
/// Reapplies Italian episode titles, runtime and translated overview after the
/// remaining remote providers have filled their gaps.
/// </summary>
public sealed class AnimeClickEpisodeAuthorityProvider : ICustomMetadataProvider<Episode>, IHasOrder
{
    private readonly ILogger<AnimeClickEpisodeAuthorityProvider> _logger;

    public AnimeClickEpisodeAuthorityProvider(ILogger<AnimeClickEpisodeAuthorityProvider> logger)
    {
        _logger = logger;
    }

    public string Name => "AnimeClick Authority";

    public int Order => 100;

    public Task<ItemUpdateType> FetchAsync(
        Episode item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken)
    {
        var updateType = AnimeClickMetadataAuthorityStore.Apply(item);
        if (updateType != ItemUpdateType.None)
        {
            _logger.LogDebug("AnimeClick authority reapplied enabled episode fields for {Item}", item.Path ?? item.Name);
        }

        return Task.FromResult(updateType);
    }
}
