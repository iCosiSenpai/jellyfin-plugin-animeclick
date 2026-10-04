using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AnimeClick.Plugin.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// What the configuration page needs about the libraries themselves: where AnimeClick is enabled,
/// enabling it on request, and the anime it refreshed most recently. Everything here reads local
/// state only; enabling a library changes its options and nothing else.
/// </summary>
public sealed class AnimeClickLibraryWorkspace
{
    public const string ProviderName = "AnimeClick";
    public const int MaximumShowcaseItems = 60;
    private const int OverviewLength = 320;

    private static readonly string[] SeriesTypes = ["Series", "Season", "Episode"];
    private static readonly string[] MovieTypes = ["Movie"];

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AnimeClickLibraryWorkspace> _logger;

    public AnimeClickLibraryWorkspace(ILibraryManager libraryManager, ILogger<AnimeClickLibraryWorkspace> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>The item types AnimeClick can provide for a library of the given collection type.</summary>
    internal static IReadOnlyList<string> RelevantTypes(string? collectionType)
        => collectionType?.Trim().ToLowerInvariant() switch
        {
            "tvshows" => SeriesTypes,
            "movies" => MovieTypes,
            null or "" or "mixed" or "unknown" => [.. SeriesTypes, .. MovieTypes],
            _ => []
        };

    public IReadOnlyList<AnimeClickLibraryStatus> GetLibraries()
        => _libraryManager.GetVirtualFolders()
            .Select(folder => Describe(
                folder.ItemId,
                folder.Name,
                folder.CollectionType?.ToString(),
                folder.LibraryOptions))
            .OfType<AnimeClickLibraryStatus>()
            .OrderBy(status => status.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Adds AnimeClick to the metadata and image providers of one library. Metadata always goes first,
    /// since AnimeClick is the Italian authority. Images go first only when the integrated TMDB or
    /// Fanart artwork is configured; otherwise AnimeClick has just its own poster to offer and stays a
    /// fallback behind whatever image providers the library already uses.
    /// </summary>
    public AnimeClickLibraryEnableResult Enable(string? libraryId, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Guid.TryParse(libraryId, out var id)
            || _libraryManager.GetItemById(id) is not CollectionFolder folder)
        {
            return new AnimeClickLibraryEnableResult { Error = "Libreria non trovata. Aggiorna l’elenco e riprova." };
        }

        var collectionType = folder.CollectionType?.ToString();
        var types = RelevantTypes(collectionType);
        if (types.Count == 0)
        {
            return new AnimeClickLibraryEnableResult
            {
                Error = "AnimeClick fornisce dati per serie, stagioni, episodi e film: questa libreria contiene altro."
            };
        }

        var options = folder.GetLibraryOptions();
        if (options?.TypeOptions is null || !options.TypeOptions.Any(entry => Contains(types, entry.Type)))
        {
            return new AnimeClickLibraryEnableResult
            {
                Error = "Jellyfin non ha ancora salvato i provider di questa libreria. Apri una volta le sue impostazioni in "
                    + "Dashboard → Librerie, salva, poi riprova."
            };
        }

        var changed = ApplyAnimeClick(options, types, IntegratedArtworkConfigured(configuration));
        if (changed)
        {
            folder.UpdateLibraryOptions(options);
            _logger.LogInformation("AnimeClick enabled as metadata provider in library {Library}", folder.Name);
        }

        return new AnimeClickLibraryEnableResult
        {
            Success = true,
            Changed = changed,
            Library = Describe(id.ToString("N", CultureInfo.InvariantCulture), folder.Name, collectionType, options)
        };
    }

    internal static bool IntegratedArtworkConfigured(PluginConfiguration configuration)
        => (configuration.EnableIntegratedImages && !string.IsNullOrWhiteSpace(configuration.TmdbApiKey))
            || (configuration.EnableFanartImages
                && (!string.IsNullOrWhiteSpace(configuration.FanartPersonalApiKey)
                    || !string.IsNullOrWhiteSpace(configuration.FanartProjectApiKey)));

    /// <summary>
    /// Puts AnimeClick in the provider lists of the given types. Other providers are kept and keep their
    /// relative order; type entries Jellyfin never saved are left alone, because creating one would
    /// silently disable every provider that is not listed in it.
    /// </summary>
    internal static bool ApplyAnimeClick(LibraryOptions options, IReadOnlyCollection<string> types, bool imagesFirst)
    {
        ArgumentNullException.ThrowIfNull(options);
        var changed = false;
        foreach (var entry in options.TypeOptions ?? [])
        {
            if (!Contains(types, entry.Type))
            {
                continue;
            }

            changed |= Update(entry.MetadataFetchers, WithProvider(entry.MetadataFetchers), value => entry.MetadataFetchers = value);
            changed |= Update(entry.MetadataFetcherOrder, First(entry.MetadataFetcherOrder), value => entry.MetadataFetcherOrder = value);
            changed |= Update(entry.ImageFetchers, WithProvider(entry.ImageFetchers), value => entry.ImageFetchers = value);
            changed |= Update(
                entry.ImageFetcherOrder,
                imagesFirst ? First(entry.ImageFetcherOrder) : WithProvider(entry.ImageFetcherOrder),
                value => entry.ImageFetcherOrder = value);
        }

        return changed;
    }

    public AnimeClickShowcase GetShowcase(int limit, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var identified = GetIdentifiedItems();
        return new AnimeClickShowcase
        {
            SeriesCount = identified.Count(item => item is Series),
            MovieCount = identified.Count(item => item is Movie),
            Items = identified
                .OrderByDescending(item => item.DateLastRefreshed)
                .ThenByDescending(item => item.DateCreated)
                .Take(Math.Clamp(limit, 1, MaximumShowcaseItems))
                .Select(item => ToShowcaseItem(item, configuration))
                .ToList()
        };
    }

    /// <summary>
    /// Every identified series and film, compact. The audits only list what needs attention (and the
    /// title audit only series), so without this a film already complete in Italian would be missing
    /// from the library page altogether.
    /// </summary>
    public IReadOnlyList<AnimeClickCatalogItem> GetCatalog()
        => GetIdentifiedItems()
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new AnimeClickCatalogItem
            {
                Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = item.Name ?? string.Empty,
                Year = item.ProductionYear,
                Type = item is Movie ? "Movie" : "Series",
                AnimeClickId = item.GetProviderId(ProviderName)!,
                HasPrimaryImage = item.HasImage(ImageType.Primary, 0),
                UpdatedAt = item.DateLastRefreshed == default
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(item.DateLastRefreshed, DateTimeKind.Utc))
            })
            .ToList();

    private List<BaseItem> GetIdentifiedItems()
        => _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
                Recursive = true,
                IsVirtualItem = false
            })
            .Where(item => item is Series or Movie
                && !string.IsNullOrWhiteSpace(item.GetProviderId(ProviderName)))
            .ToList();

    private static AnimeClickShowcaseItem ToShowcaseItem(BaseItem item, PluginConfiguration configuration)
    {
        var animeClickId = item.GetProviderId(ProviderName)!;
        return new AnimeClickShowcaseItem
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Name = item.Name ?? string.Empty,
            OriginalTitle = string.Equals(item.OriginalTitle, item.Name, StringComparison.Ordinal) ? null : item.OriginalTitle,
            Year = item.ProductionYear,
            Type = item is Movie ? "Movie" : "Series",
            AnimeClickId = animeClickId,
            AnimeClickUrl = AnimeClickClient.TryBuildAnimeUrl(configuration.BaseUrl, animeClickId, out var url) ? url : null,
            Overview = Shorten(item.Overview),
            Genres = (item.Genres ?? []).Take(3).ToList(),
            CommunityRating = item.CommunityRating,
            UpdatedAt = item.DateLastRefreshed == default
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(item.DateLastRefreshed, DateTimeKind.Utc)),
            HasPrimaryImage = item.HasImage(ImageType.Primary, 0),
            HasBackdropImage = item.HasImage(ImageType.Backdrop, 0),
            HasLogoImage = item.HasImage(ImageType.Logo, 0)
        };
    }

    private static string? Shorten(string? overview)
    {
        var text = overview?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length <= OverviewLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', OverviewLength - 1);
        return text[..(cut > OverviewLength / 2 ? cut : OverviewLength - 1)].TrimEnd(',', ';', ':', ' ') + "…";
    }

    private static AnimeClickLibraryStatus? Describe(
        string? id,
        string? name,
        string? collectionType,
        LibraryOptions? options)
    {
        var types = RelevantTypes(collectionType);
        if (string.IsNullOrWhiteSpace(id) || types.Count == 0)
        {
            return null;
        }

        var status = new AnimeClickLibraryStatus
        {
            Id = Guid.TryParse(id, out var parsed) ? parsed.ToString("N", CultureInfo.InvariantCulture) : id,
            Name = string.IsNullOrWhiteSpace(name) ? "Libreria" : name,
            CollectionType = collectionType?.ToLowerInvariant()
        };

        foreach (var type in types)
        {
            var entry = options?.TypeOptions?.FirstOrDefault(option => string.Equals(option.Type, type, StringComparison.OrdinalIgnoreCase));
            status.Types.Add(entry is null
                ? new AnimeClickLibraryTypeStatus { Type = type }
                : new AnimeClickLibraryTypeStatus
                {
                    Type = type,
                    Configured = true,
                    MetadataEnabled = Contains(entry.MetadataFetchers, ProviderName),
                    MetadataFirst = Contains(entry.MetadataFetchers, ProviderName)
                        && string.Equals(EffectiveOrder(entry.MetadataFetchers, entry.MetadataFetcherOrder).FirstOrDefault(), ProviderName, StringComparison.OrdinalIgnoreCase),
                    ImagesEnabled = Contains(entry.ImageFetchers, ProviderName)
                });
        }

        var configured = status.Types.Where(type => type.Configured).ToList();
        status.State = configured.Count == 0
            ? AnimeClickLibraryState.Unmanaged
            : configured.All(type => type.MetadataEnabled && type.MetadataFirst)
                ? AnimeClickLibraryState.Active
                : configured.Any(type => type.MetadataEnabled)
                    ? AnimeClickLibraryState.Partial
                    : AnimeClickLibraryState.Inactive;
        status.CanEnable = status.State is AnimeClickLibraryState.Partial or AnimeClickLibraryState.Inactive
            || (status.State == AnimeClickLibraryState.Active && configured.Any(type => !type.ImagesEnabled));
        return status;
    }

    /// <summary>The enabled providers in the order Jellyfin runs them: listed ones first, the rest after.</summary>
    private static IEnumerable<string> EffectiveOrder(string[]? enabled, string[]? order)
    {
        var active = enabled ?? [];
        return (order ?? [])
            .Where(name => Contains(active, name))
            .Concat(active.Where(name => !Contains(order, name)));
    }

    private static string[] WithProvider(string[]? values)
        => Contains(values, ProviderName) ? values! : [.. values ?? [], ProviderName];

    private static string[] First(string[]? values)
        => [ProviderName, .. (values ?? []).Where(name => !string.Equals(name, ProviderName, StringComparison.OrdinalIgnoreCase))];

    private static bool Update(string[]? current, string[] next, Action<string[]> assign)
    {
        if (current is not null && current.SequenceEqual(next, StringComparer.Ordinal))
        {
            return false;
        }

        assign(next);
        return true;
    }

    private static bool Contains(IEnumerable<string>? values, string? name)
        => name is not null && values is not null && values.Contains(name, StringComparer.OrdinalIgnoreCase);
}

public static class AnimeClickLibraryState
{
    public const string Active = "active";
    public const string Partial = "partial";
    public const string Inactive = "inactive";

    /// <summary>Jellyfin has no saved provider list for this library, so its server-wide defaults apply.</summary>
    public const string Unmanaged = "unmanaged";
}

public sealed class AnimeClickLibraryStatus
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CollectionType { get; set; }
    public string State { get; set; } = AnimeClickLibraryState.Inactive;
    public bool CanEnable { get; set; }
    public List<AnimeClickLibraryTypeStatus> Types { get; set; } = [];
}

public sealed class AnimeClickLibraryTypeStatus
{
    public string Type { get; set; } = string.Empty;
    public bool Configured { get; set; }
    public bool MetadataEnabled { get; set; }
    public bool MetadataFirst { get; set; }
    public bool ImagesEnabled { get; set; }
}

public sealed class AnimeClickLibraryEnableResult
{
    public bool Success { get; set; }
    public bool Changed { get; set; }
    public AnimeClickLibraryStatus? Library { get; set; }
    public string? Error { get; set; }
}

public sealed class AnimeClickShowcase
{
    public int SeriesCount { get; set; }
    public int MovieCount { get; set; }
    public List<AnimeClickShowcaseItem> Items { get; set; } = [];
}

public sealed class AnimeClickCatalogItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string Type { get; set; } = "Series";
    public string AnimeClickId { get; set; } = string.Empty;
    public bool HasPrimaryImage { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class AnimeClickShowcaseItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public string Type { get; set; } = "Series";
    public string AnimeClickId { get; set; } = string.Empty;
    public string? AnimeClickUrl { get; set; }
    public string? Overview { get; set; }
    public List<string> Genres { get; set; } = [];
    public float? CommunityRating { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public bool HasPrimaryImage { get; set; }
    public bool HasBackdropImage { get; set; }
    public bool HasLogoImage { get; set; }
}
