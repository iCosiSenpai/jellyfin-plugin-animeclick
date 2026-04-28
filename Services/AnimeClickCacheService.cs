using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;

namespace AnimeClick.Plugin.Services;

public class AnimeClickCacheService
{
    private readonly string _cacheDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private static SemaphoreSlim GetLock(string key) => Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    public AnimeClickCacheService(IApplicationPaths applicationPaths)
    {
        _cacheDirectory = Path.Combine(applicationPaths.CachePath, "AnimeClickMetadata");
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Retrieves a cached value if it exists and has not expired.
    /// </summary>
    /// <param name="key">Cache key.</param>
    /// <param name="maxAgeHours">Maximum age in hours. If the cached file is older, it is treated as expired.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<T?> GetAsync<T>(string key, int maxAgeHours, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        var asyncLock = GetLock(key);
        await asyncLock.WaitAsync(cancellationToken);
        
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (fileAge.TotalHours > maxAgeHours)
            {
                // Cache expired — delete stale file and return null.
                try { File.Delete(path); } catch { /* best effort */ }
                return default;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        finally
        {
            asyncLock.Release();
        }
    }

    /// <summary>
    /// Overload without TTL for backward compatibility — defaults to no expiration check.
    /// </summary>
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
        => GetAsync<T>(key, int.MaxValue, cancellationToken);

    public async Task SetAsync<T>(string key, T payload, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        var asyncLock = GetLock(key);
        await asyncLock.WaitAsync(cancellationToken);
        
        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
        }
        finally
        {
            asyncLock.Release();
        }
    }

    public int ClearByPrefix(string prefix)
    {
        var safePrefix = SanitizeFileKey(prefix);
        var removed = 0;

        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, safePrefix + "*.json"))
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch
            {
                // Best effort: cache cleanup should not fail diagnostics.
            }
        }

        return removed;
    }

    public int ClearKey(string key)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return 0;
        }

        File.Delete(path);
        return 1;
    }

    private string GetPath(string key)
        => Path.Combine(_cacheDirectory, SanitizeFileKey(key) + ".json");

    private static string SanitizeFileKey(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            key = key.Replace(c, '_');
        }

        return key;
    }
}
