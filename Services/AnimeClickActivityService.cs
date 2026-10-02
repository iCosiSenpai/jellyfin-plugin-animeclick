using System.Collections.Concurrent;

namespace AnimeClick.Plugin.Services;

/// <summary>Process-local activity snapshots. No library paths, secrets or telemetry.</summary>
public sealed class AnimeClickActivityService
{
    public const string Titles = "titles";
    public const string Synopses = "synopses";
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, AnimeClickActivity> _activities = new();

    public AnimeClickActivity Get(string key)
        => _activities.GetValueOrDefault(key) ?? new AnimeClickActivity { Key = key };

    public bool TryQueue(string key)
    {
        lock (_gate)
        {
            var current = Get(key);
            if (current.IsActive && (current.State != "Queued" || current.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-2)))
                return false;
            _activities[key] = new AnimeClickActivity
            {
                Key = key, State = "Queued", Message = "Avvio in attesa di Jellyfin…",
                StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public bool Begin(string key)
    {
        lock (_gate)
        {
            if (Get(key).State == "Running") return false;
            if (Get(key).State == "Cancelling")
            {
                Finish(key, "Cancelled", "Attività interrotta prima dell’avvio.");
                return false;
            }
            _activities[key] = new AnimeClickActivity
            {
                Key = key, State = "Running", Message = "Analisi della libreria…",
                StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public void Update(string key, int total, int processed, int applied, int skipped, int errors, string message)
    {
        lock (_gate)
        {
            var current = Get(key);
            _activities[key] = current with
            {
                Total = total, Processed = processed, Applied = applied, Skipped = skipped, Errors = errors,
                Message = message, UpdatedAt = DateTimeOffset.UtcNow,
                Progress = total > 0 ? Math.Clamp(processed * 100d / total, 0, 99) : 0
            };
        }
    }

    public void Cancel(string key)
    {
        lock (_gate)
        {
            var current = Get(key);
            if (current.IsActive)
                _activities[key] = current with { State = "Cancelling", Message = "Interruzione in corso…", UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    public void Finish(string key, string state, string message)
    {
        lock (_gate)
        {
            var current = Get(key);
            _activities[key] = current with
            {
                State = state, Message = message, UpdatedAt = DateTimeOffset.UtcNow,
                Progress = state == "Completed" ? 100 : current.Progress
            };
        }
    }
}

public sealed record AnimeClickActivity
{
    public string Key { get; init; } = string.Empty;
    public string State { get; init; } = "Idle";
    public string Message { get; init; } = "Nessuna attività in corso.";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int Total { get; init; }
    public int Processed { get; init; }
    public int Applied { get; init; }
    public int Skipped { get; init; }
    public int Errors { get; init; }
    public double Progress { get; init; }
    public bool IsActive => State is "Queued" or "Running" or "Cancelling";
}
