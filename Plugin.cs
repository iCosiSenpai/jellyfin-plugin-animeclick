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
    }

    public override Guid Id => Guid.Parse("1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11");

    public override string Name => "AnimeClick Metadata";

    public override string Description => "Provider metadati anime in italiano basato su AnimeClick con cache locale.";

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
            new PluginPageInfo { Name = "AnimeClickConfigJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-config.js" }
        ];
    }
}
