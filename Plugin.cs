using System;
using System.Collections.Generic;
using AnimeClick.Plugin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace AnimeClick.Plugin;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Migrate only the superseded default model, then force the persisted values into
        // usable ranges. SaveConfiguration persists the normalized result while preserving
        // every API key and any custom model choice.
        var migrated = Configuration.ApplyMigrations();
        var sanitized = Configuration.Sanitize();
        if (migrated || sanitized)
        {
            SaveConfiguration();
        }
    }

    /// <summary>
    /// Validates whatever the configuration endpoint received before it is persisted. The
    /// endpoint deserializes the request body straight onto the configuration object, so this
    /// is the only server-side gate: without it a negative delay or a BaseUrl that is not a
    /// URL reached every consumer, and the page's JavaScript checks were bypassable.
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration is PluginConfiguration animeClickConfiguration)
        {
            animeClickConfiguration.Sanitize();
        }

        base.UpdateConfiguration(configuration);
    }

    public override Guid Id => Guid.Parse("1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11");

    public override string Name => "AnimeClick Plugin";

    public override string Description => "Autorità metadati anime in italiano con fallback cloud controllato.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return
        [
            // Config page
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = ns + ".Configuration.configPage.html",

                // Show the plugin in the dashboard's left drawer. NOTE: Jellyfin 10.11's web
                // client renders every plugin menu entry with a fixed Material "folder" icon
                // and ignores PluginPageInfo.MenuIcon, so a custom AnimeClick logo in the
                // sidebar is not achievable on this server version.
                EnableInMainMenu = true
            },
            // Shared assets (served via /web/configurationpage?name=...)
            new PluginPageInfo { Name = "AnimeClickCss", EmbeddedResourcePath = ns + ".Web.assets.animeclick.css" },
            new PluginPageInfo { Name = "AnimeClickConfigJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-config.js" },
            new PluginPageInfo { Name = "AnimeClickBanner", EmbeddedResourcePath = ns + ".assets.banner.png" },
            new PluginPageInfo { Name = "AnimeClickLogo", EmbeddedResourcePath = ns + ".assets.logo.png" }
        ];
    }
}
