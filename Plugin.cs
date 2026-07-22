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

        // Migrate only the superseded default model. SaveConfiguration persists the
        // normalized value while preserving every API key and any custom model choice.
        if (Configuration.ApplyMigrations())
        {
            SaveConfiguration();
        }
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
                EmbeddedResourcePath = ns + ".Configuration.configPage.html"
            },
            // Shared assets (served via /web/configurationpage?name=...)
            new PluginPageInfo { Name = "AnimeClickCss", EmbeddedResourcePath = ns + ".Web.assets.animeclick.css" },
            new PluginPageInfo { Name = "AnimeClickConfigJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-config.js" },
            new PluginPageInfo { Name = "AnimeClickBanner", EmbeddedResourcePath = ns + ".assets.banner.png" },
            new PluginPageInfo { Name = "AnimeClickLogo", EmbeddedResourcePath = ns + ".assets.logo.png" }
        ];
    }
}
