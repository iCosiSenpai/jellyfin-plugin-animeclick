using AnimeClick.Plugin.Providers;
using AnimeClick.Plugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeClick.Plugin;

/// <summary>
/// Registers plugin services into Jellyfin's DI container.
/// Automatically discovered by Jellyfin at startup.
/// Uses explicit interface implementation for backward compatibility
/// with older Jellyfin.Controller NuGet package.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddHttpClient<AnimeClickClient>();
        services.AddSingleton<AnimeClickCacheService>();
        services.AddSingleton<AnimeClickHtmlParser>();
        services.AddSingleton<AnimeClickSeriesSearchProvider>();
    }

    void IPluginServiceRegistrator.RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        RegisterServices(services);
    }
}
