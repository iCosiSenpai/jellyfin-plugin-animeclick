// AnimeClick Metadata Plugin for Jellyfin
// Copyright (C) 2026 Alessio Cosi (iCosiSenpai)
//
// Software libero sotto GNU Affero General Public License v3 (dalla versione 1.6.0.0; le
// versioni precedenti restano sotto GPLv3): vedi LICENSE. Termini aggiuntivi ai sensi della
// sezione 7 — attribuzione, versioni modificate riconoscibili, nome, logo e mascotte non
// concessi — e autorizzazione allo scraping di AnimeClick.it non trasferibile: vedi NOTICE.

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

        // Issue #2: an update can leave the previous copy next to this one, and Jellyfin would load both.
        var assembly = GetType().Assembly;
        AnimeClick.Plugin.Services.AnimeClickPluginVersions.SupersedeOlderCopies(
            applicationPaths.PluginsPath,
            Id,
            assembly.GetName().Version ?? new Version(0, 0),
            System.IO.Path.GetDirectoryName(assembly.Location));

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

    public override string Description => "Titoli, trame ed episodi degli anime in italiano da AnimeClick.";

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

                // Keep the plugin directly reachable from the administration menu.
                EnableInMainMenu = true,
                MenuIcon = "movie"
            },
            // Shared assets (served via /web/configurationpage?name=...). The page loads the scripts
            // in this order: each one builds on the namespace the previous ones declared.
            new PluginPageInfo { Name = "AnimeClickCss", EmbeddedResourcePath = ns + ".Web.assets.animeclick.css" },
            new PluginPageInfo { Name = "AnimeClickCoreJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-core.js" },
            new PluginPageInfo { Name = "AnimeClickSettingsJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-settings.js" },
            new PluginPageInfo { Name = "AnimeClickLibraryJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-library.js" },
            new PluginPageInfo { Name = "AnimeClickHomeJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-home.js" },
            new PluginPageInfo { Name = "AnimeClickSetupJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-setup.js" },
            new PluginPageInfo { Name = "AnimeClickAppJs", EmbeddedResourcePath = ns + ".Web.assets.animeclick-app.js" },
            new PluginPageInfo { Name = "AnimeClickMascot", EmbeddedResourcePath = ns + ".Web.assets.mascot-360.png" },
            new PluginPageInfo { Name = "AnimeClickMascotSmall", EmbeddedResourcePath = ns + ".Web.assets.mascot-96.png" },
            new PluginPageInfo { Name = "AnimeClickLogoTmdb", EmbeddedResourcePath = ns + ".ProviderLogos.tmdb.svg" },
            new PluginPageInfo { Name = "AnimeClickLogoFanart", EmbeddedResourcePath = ns + ".ProviderLogos.fanart.png" },
            new PluginPageInfo { Name = "AnimeClickLogoTvdb", EmbeddedResourcePath = ns + ".ProviderLogos.thetvdb-dark.png" }
        ];
    }
}
