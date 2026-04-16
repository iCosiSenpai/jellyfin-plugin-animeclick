using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeClick.Plugin;

/// <summary>
/// Registers plugin services into Jellyfin's DI container.
/// Automatically discovered by Jellyfin at startup.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpClient<AnimeClickClient>();
        services.AddSingleton<AnimeClickCacheService>();
        services.AddSingleton<AnimeClickHtmlParser>();
        services.AddSingleton<AnimeClickSeriesSearchProvider>();
    }
}
