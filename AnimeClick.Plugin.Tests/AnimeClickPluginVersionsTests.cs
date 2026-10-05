using System.Text.Json;
using AnimeClick.Plugin.Services;
using Xunit;

public sealed class AnimeClickPluginVersionsTests : IDisposable
{
    private static readonly Guid AnimeClick = Guid.Parse("1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11");
    private readonly string _plugins = Path.Combine(Path.GetTempPath(), "animeclick-plugins-" + Guid.NewGuid().ToString("N"));

    public AnimeClickPluginVersionsTests() => Directory.CreateDirectory(_plugins);

    public void Dispose() => Directory.Delete(_plugins, recursive: true);

    private string Folder(string name, string? meta)
    {
        var folder = Path.Combine(_plugins, name);
        Directory.CreateDirectory(folder);
        if (meta is not null) File.WriteAllText(Path.Combine(folder, "meta.json"), meta);
        return folder;
    }

    private static string Meta(Guid guid, string version, string? status = "Active", string name = "AnimeClick Plugin")
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["guid"] = guid.ToString(), ["name"] = name, ["version"] = version, ["status"] = status, ["autoUpdate"] = true
        });

    private string Status(string folder) => JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "meta.json")))
        .RootElement.GetProperty("status").GetString()!;

    [Fact]
    public void AnOlderCopyLeftByAnUpdateIsRetiredAndEverythingElseIsLeftAlone()
    {
        // The issue: the catalog wrote one name, the loaded plugin another, and both copies were loaded.
        var current = Folder("AnimeClick Metadata_1.6.0.0", Meta(AnimeClick, "1.6.0.0", name: "AnimeClick Metadata"));
        var previous = Folder("AnimeClick Metadata_1.5.0.0", Meta(AnimeClick, "1.5.0.0"));
        var disabled = Folder("AnimeClick Metadata_1.4.0.0", Meta(AnimeClick, "1.4.0.0", "Disabled"));
        var newer = Folder("AnimeClick Metadata_1.7.0.0", Meta(AnimeClick, "1.7.0.0"));
        var other = Folder("Editor's Choice_1.5.2.0", Meta(Guid.NewGuid(), "1.5.2.0"));
        var broken = Folder("AnimeClick Metadata_0.1.0.0", "{ not json");
        var empty = Folder("Without manifest", null);

        var changed = AnimeClickPluginVersions.SupersedeOlderCopies(_plugins, AnimeClick, new Version(1, 6, 0, 0), current);

        Assert.Equal([previous], changed);
        Assert.Equal("Superseded", Status(previous));
        Assert.Equal("Active", Status(current));
        Assert.Equal("Disabled", Status(disabled));
        Assert.Equal("Active", Status(newer));
        Assert.Equal("Active", Status(other));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(broken, "meta.json")));
        Assert.False(File.Exists(Path.Combine(empty, "meta.json")));
        Assert.Empty(Directory.GetFiles(_plugins, "*.tmp", SearchOption.AllDirectories));

        // Every other field of the retired manifest is kept.
        var kept = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "meta.json"))).RootElement;
        Assert.Equal(("AnimeClick Plugin", "1.5.0.0", true), (kept.GetProperty("name").GetString(), kept.GetProperty("version").GetString(), kept.GetProperty("autoUpdate").GetBoolean()));
    }

    [Fact]
    public void TheDefaultUserAgentNamesThisProjectSoForksCanBeToldApart()
    {
        // NOTICE, point 2(b): a modified version must not send this User-Agent.
        var agent = new AnimeClick.Plugin.Configuration.PluginConfiguration().UserAgent;
        Assert.StartsWith("AnimeClick-Jellyfin-Plugin/", agent, StringComparison.Ordinal);
        Assert.Contains("https://github.com/iCosiSenpai/jellyfin-plugin-animeclick", agent, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFolderOrPathNeverStopsThePlugin()
    {
        Assert.Empty(AnimeClickPluginVersions.SupersedeOlderCopies(null, AnimeClick, new Version(1, 6), null));
        Assert.Empty(AnimeClickPluginVersions.SupersedeOlderCopies(Path.Combine(_plugins, "missing"), AnimeClick, new Version(1, 6), null));
    }
}
