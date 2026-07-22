using System;
using System.Threading;

namespace AnimeClick.Plugin.Services;

/// <summary>
/// Fixed-size pool of <see cref="SemaphoreSlim"/> gates keyed by string. Guarantees that the
/// same key always maps to the same gate while keeping memory bounded — a per-key dictionary
/// of semaphores would grow without limit on a long-running server (one entry per distinct
/// cache key ever touched). Distinct keys may occasionally share a gate (rare, benign contention),
/// which is the same trade-off already used by <see cref="AnimeClickMetadataAuthorityStore"/>.
/// </summary>
internal sealed class SemaphoreStripe
{
    private readonly SemaphoreSlim[] _gates;

    public SemaphoreStripe(int size = 64)
    {
        if (size < 1)
        {
            size = 1;
        }

        _gates = new SemaphoreSlim[size];
        for (var index = 0; index < _gates.Length; index++)
        {
            _gates[index] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>Returns the gate that serializes access for <paramref name="key"/>.</summary>
    public SemaphoreSlim Get(string key)
        => _gates[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_gates.Length)];
}
