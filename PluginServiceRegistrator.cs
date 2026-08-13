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
        // Resolve HttpClient through the shared IHttpClientFactory (like every other network
        // client here) instead of registering a typed client. AddHttpClient<AnimeClickClient>()
        // keys its options by the type name, which clashes and crashes registration if two
        // plugin versions are ever loaded at once; a plain singleton avoids that entirely.
        services.AddSingleton<AnimeClickClient>();
        services.AddSingleton<AnimeClickCacheService>();
        services.AddSingleton<AnimeClickHtmlParser>();
        services.AddSingleton<AnimeClickEpisodeListLoader>();
        services.AddSingleton<AnimeClickEpisodeLayoutResolver>();
        services.AddSingleton<AnimeClickSeasonResolver>();
        services.AddSingleton<AnimeClickSeriesSearchProvider>();
        services.AddSingleton<AnimeClickAniListResolver>();
        services.AddSingleton<AnimeClickTmdbClient>();
        services.AddSingleton<AnimeClickAiTranslator>();
        services.AddSingleton<AnimeClickMetadataRefreshIntentRegistry>();
        services.AddSingleton<AnimeClickMetadataRefreshScheduler>();
        services.AddSingleton<AnimeClickTranslationQueue>();
        services.AddSingleton<AnimeClickTvdbClient>();
        services.AddSingleton<AnimeClickMetadataFallbackService>();
        services.AddSingleton<AnimeClickRepairLedger>();
        services.AddSingleton<IAnimeClickOverviewResolver, AnimeClickOverviewResolver>();
        services.AddSingleton<AnimeClickLibraryQualityService>();
    }

    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        RegisterServices(services);
    }
}
