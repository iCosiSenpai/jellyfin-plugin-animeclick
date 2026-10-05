using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Retires older copies of this plugin that an update left next to the running one.
/// <para>
/// Jellyfin groups the versions of a plugin by the name in each folder's <c>meta.json</c>, not by its
/// ID. An install from the catalog writes the catalog's package name there, and the first successful
/// load replaces it with the plugin's own name: so the folder of a fresh update can carry a different
/// name from the one it updates, both get loaded, and every type shared between them — the
/// configuration first — fails to cast (issue #2). Marking the older copies Superseded makes Jellyfin
/// skip them from the next start, without deleting anything.
/// </para>
/// </summary>
public static class AnimeClickPluginVersions
{
    /// <summary>Folders whose manifest was changed; never throws.</summary>
    public static IReadOnlyList<string> SupersedeOlderCopies(string? pluginsPath, Guid id, Version current, string? currentDirectory)
    {
        var changed = new List<string>();
        if (string.IsNullOrWhiteSpace(pluginsPath) || !Directory.Exists(pluginsPath)) return changed;
        IEnumerable<string> folders;
        try { folders = Directory.EnumerateDirectories(pluginsPath).ToList(); }
        catch (Exception) { return changed; }

        foreach (var folder in folders)
        {
            if (currentDirectory is not null && string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                continue;
            var metafile = Path.Combine(folder, "meta.json");
            try
            {
                if (!File.Exists(metafile) || JsonNode.Parse(File.ReadAllText(metafile)) is not JsonObject manifest) continue;
                if (!Guid.TryParse(Text(manifest, "guid") ?? Text(manifest, "id"), out var guid) || guid != id) continue;
                if (!Version.TryParse(Text(manifest, "version"), out var version) || version >= current) continue;

                // Only an active copy is retired: a disabled or already superseded one is the administrator's choice.
                var status = Text(manifest, "status");
                if (status is not (null or "Active")) continue;
                manifest["status"] = "Superseded";
                var temporary = metafile + ".animeclick.tmp";
                File.WriteAllText(temporary, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, metafile, overwrite: true);
                changed.Add(folder);
            }
            catch (Exception)
            {
                // A folder that cannot be read or written stays as it is: never fail the plugin's start.
            }
        }

        return changed;
    }

    private static string? Text(JsonObject manifest, string name)
    {
        var node = manifest.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
