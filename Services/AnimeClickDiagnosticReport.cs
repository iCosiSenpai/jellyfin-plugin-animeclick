using System.Reflection;
using System.Runtime.InteropServices;
using AnimeClick.Plugin.Configuration;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// What an administrator can attach to a bug report: versions, the settings that explain a
/// behaviour, where AnimeClick is enabled, the cache and the running activities.
/// <para>
/// It is written to be pasted in public. Keys and tokens are reduced to "set or not", addresses to
/// their kind, and nothing from the library — titles, paths, users, the server's address — is read
/// at all, only counted.
/// </para>
/// </summary>
public sealed class AnimeClickDiagnosticReport
{
    public DateTimeOffset GeneratedAt { get; set; }
    public string PluginVersion { get; set; } = string.Empty;
    public string? ServerVersion { get; set; }
    public string Runtime { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public SortedDictionary<string, object?> Configuration { get; set; } = new(StringComparer.Ordinal);
    public List<AnimeClickDiagnosticLibrary> Libraries { get; set; } = [];
    public int IdentifiedSeries { get; set; }
    public int IdentifiedMovies { get; set; }
    public List<AnimeClickDiagnosticActivity> Activities { get; set; } = [];
    public AnimeClickCacheStatistics Cache { get; set; } = new();
    public int RepairLedgerEntries { get; set; }

    internal const string Set = "impostata";
    internal const string NotSet = "non impostata";

    /// <summary>String settings that are neither secret nor personal and explain behaviour as they are.</summary>
    private static readonly HashSet<string> PlainTextSettings = new(StringComparer.Ordinal)
    {
        nameof(PluginConfiguration.AiProvider),
        nameof(PluginConfiguration.AiModel),
        nameof(PluginConfiguration.CommunitySharingMode)
    };

    public static AnimeClickDiagnosticReport Create(
        PluginConfiguration configuration,
        string? serverVersion,
        IEnumerable<AnimeClickLibraryStatus> libraries,
        (int Series, int Movies) identified,
        IEnumerable<AnimeClickActivity> activities,
        AnimeClickCacheStatistics cache,
        int repairLedgerEntries)
        => new()
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            PluginVersion = AnimeClickRepairLedger.CurrentPluginVersion,
            ServerVersion = serverVersion,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture,
            Configuration = Redact(configuration),
            Libraries = [.. libraries.Select(library => new AnimeClickDiagnosticLibrary
            {
                CollectionType = library.CollectionType,
                State = library.State,
                Types = library.Types
            })],
            IdentifiedSeries = identified.Series,
            IdentifiedMovies = identified.Movies,
            // State and counters only: while a repair runs its message names the series being read.
            Activities = [.. activities.Select(activity => new AnimeClickDiagnosticActivity
            {
                Key = activity.Key,
                State = activity.State,
                StartedAt = activity.StartedAt,
                UpdatedAt = activity.UpdatedAt,
                Total = activity.Total,
                Processed = activity.Processed,
                Applied = activity.Applied,
                Skipped = activity.Skipped,
                Errors = activity.Errors
            })],
            Cache = cache,
            RepairLedgerEntries = repairLedgerEntries
        };

    /// <summary>
    /// The configuration without anything that should not be published. The rule is safe by default:
    /// a string setting is reported as it is only when it is on the short list of plain ones, so a
    /// key added by a later version is redacted without anyone having to remember this method.
    /// </summary>
    internal static SortedDictionary<string, object?> Redact(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = property.GetValue(configuration);
            result[property.Name] = value switch
            {
                bool or int or long or double => value,
                string text when PlainTextSettings.Contains(property.Name) => text,
                string text => property.Name switch
                {
                    nameof(PluginConfiguration.BaseUrl) => DescribeDefault(text, ConfigurationLimits.DefaultBaseUrl),
                    // The saved value carries the version it was saved with; the one actually sent is what counts.
                    nameof(PluginConfiguration.UserAgent) => DescribeDefault(
                        AnimeClickClient.GetEffectiveUserAgent(configuration),
                        AnimeClickClient.GetEffectiveUserAgent(new PluginConfiguration())),
                    nameof(PluginConfiguration.AiEndpoint) or nameof(PluginConfiguration.OllamaCloudEndpoint) => DescribeEndpoint(text),
                    nameof(PluginConfiguration.EpisodeLayoutOverrides) => CountOverrides(text),
                    _ => string.IsNullOrWhiteSpace(text) ? NotSet : Set
                },
                null => null,
                _ => value.GetType().Name
            };
        }

        return result;
    }

    private static string DescribeDefault(string value, string defaultValue)
        => string.IsNullOrWhiteSpace(value)
            ? NotSet
            : string.Equals(value.Trim().TrimEnd('/'), defaultValue.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? "predefinito"
                : "personalizzato";

    private static string DescribeEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NotSet;
        }

        if (!AnimeClickAiTranslator.TryNormalizeEndpoint(value, out var uri))
        {
            return "non valido";
        }

        return uri.Scheme == Uri.UriSchemeHttps
            ? AnimeClickAiTranslator.IsPrivateDestination(uri) ? "HTTPS, rete locale" : "HTTPS, servizio online"
            : "HTTP, rete locale";
    }

    private static int CountOverrides(string value)
        => value.Split('\n')
            .Select(line => line.Trim())
            .Count(line => line.Length > 0 && !line.StartsWith('#'));
}

/// <summary>A library as the report describes it: its kind and AnimeClick's place in it, never its name.</summary>
public sealed class AnimeClickDiagnosticLibrary
{
    public string? CollectionType { get; set; }
    public string State { get; set; } = string.Empty;
    public List<AnimeClickLibraryTypeStatus> Types { get; set; } = [];
}

/// <summary>A background activity as the report describes it: where it stands, never what it is reading.</summary>
public sealed class AnimeClickDiagnosticActivity
{
    public string Key { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Applied { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
}
