using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AnimeClick.Plugin.Providers;

/// <summary>
/// Registers the AnimeClick external ID for Series.
/// This makes the "AnimeClick" field appear in the "Identify" dialog
/// and in the item's external IDs section.
/// The ID format is "72/naruto" (numeric ID + slug from the URL).
/// </summary>
public class AnimeClickSeriesExternalId : IExternalId
{
    public string ProviderName => "AnimeClick";
    public string Key => "AnimeClick";
    public ExternalIdMediaType? Type => ExternalIdMediaType.Series;
    public string UrlFormatString => "https://www.animeclick.it/anime/{0}";
    public bool Supports(IHasProviderIds item) => item is Series;
}

/// <summary>
/// Registers the AnimeClick external ID for Movies.
/// </summary>
public class AnimeClickMovieExternalId : IExternalId
{
    public string ProviderName => "AnimeClick";
    public string Key => "AnimeClick";
    public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
    public string UrlFormatString => "https://www.animeclick.it/anime/{0}";
    public bool Supports(IHasProviderIds item) => item is Movie;
}

/// <summary>
/// Registers the AnimeClick external ID for Seasons, so the field is editable on a season.
/// <para>
/// AnimeClick publishes most franchises as one card per cour, and the sequel traversal can only
/// follow a chain it can prove: for "Clannad After Story" or "Kaguya-sama 2" the relation exists
/// but the safety filters or the ambiguity rule stop it. Writing the right card on the season is
/// the manual way out, and the episode provider now prefers a season identity over the series
/// one — but without this registration the field never appears in the interface.
/// </para>
/// </summary>
public class AnimeClickSeasonExternalId : IExternalId
{
    public string ProviderName => "AnimeClick";
    public string Key => "AnimeClick";
    public ExternalIdMediaType? Type => ExternalIdMediaType.Season;
    public string UrlFormatString => "https://www.animeclick.it/anime/{0}";
    public bool Supports(IHasProviderIds item) => item is Season;
}
