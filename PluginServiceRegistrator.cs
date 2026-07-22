using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeClick.Plugin;

/// <summary>
/// Registers plugin services into Jellyfin's DI container.
/// Both signatures are needed: the interface-based one for assembly loading,
/// and the reflection-based one for runtime invocation.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddHttpClient<AnimeClickClient>();
        services.AddSingleton<AnimeClickCacheService>();
        services.AddSingleton<AnimeClickHtmlParser>();
        services.AddSingleton<AnimeClickEpisodeListLoader>();
        services.AddSingleton<AnimeClickEpisodeLayoutResolver>();
        services.AddSingleton<AnimeClickSeasonResolver>();
        services.AddSingleton<AnimeClickSeriesSearchProvider>();
        services.AddSingleton<AnimeClickAniListResolver>();
        services.AddSingleton<AnimeClickTmdbClient>();
        services.AddSingleton<AnimeClickOllamaTranslator>();
        services.AddSingleton<AnimeClickTranslationQueue>();
        services.AddSingleton<AnimeClickTvdbClient>();
        services.AddSingleton<AnimeClickMetadataFallbackService>();
    }

    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        RegisterServices(services);
    }
}
