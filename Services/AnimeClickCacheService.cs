using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Services;

public class AnimeClickCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    // Fixed pool of gates keyed by the final path. Read/write and administrative clear
    // operations synchronize on the same gate even when two logical keys sanitize to one
    // filename. A per-path dictionary of semaphores is intentionally avoided: it would grow
    // without limit on a long-running server (one entry per distinct cache key ever touched).
    private static readonly SemaphoreStripe Locks = new();

    // Serializes cache publication against administrative clear operations. Per-key locks
    // alone are insufficient because a clear cannot enumerate a write that is still a .tmp.
    private static readonly SemaphoreSlim MutationGate = new(1, 1);

    private readonly string _cacheDirectory;
    private readonly ILogger<AnimeClickCacheService> _logger;

    public AnimeClickCacheService(
        IApplicationPaths applicationPaths,
        ILogger<AnimeClickCacheService> logger)
    {
        _cacheDirectory = Path.Combine(applicationPaths.CachePath, "AnimeClickMetadata");
        _logger = logger;
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Retrieves a cached value if it exists, is valid JSON and has not expired.
    /// Corrupt or truncated entries are removed and treated as cache misses.
    /// </summary>
    public async Task<T?> GetAsync<T>(string key, int maxAgeHours, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        var asyncLock = GetLock(path);
        await asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (maxAgeHours <= 0 || fileAge.TotalHours > maxAgeHours)
            {
                DeleteFileBestEffort(path);
                return default;
            }

            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await JsonSerializer
                    .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException)
            {
                DeleteFileBestEffort(path);
                return default;
            }
            catch (IOException)
            {
                DeleteFileBestEffort(path);
                return default;
            }
            catch (UnauthorizedAccessException)
            {
                // A cache miss is preferable to failing the metadata pipeline.
                return default;
            }
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

    /// <summary>
    /// Serializes to a temporary file and atomically replaces the final entry. A cancelled
    /// write can therefore never leave a partially-written JSON file visible to readers.
    /// A write that cannot complete is logged and ignored: several callers invoke this
    /// outside any try/catch, and a failed cache write must not abort an item's metadata.
    /// </summary>
    public async Task SetAsync<T>(string key, T payload, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var asyncLock = GetLock(path);

        await MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await JsonSerializer
                        .SerializeAsync(stream, payload, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempPath, path, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or JsonException)
            {
                // Disk full, a read-only cache directory, or a key that sanitizes to a
                // filename past the filesystem limit would otherwise propagate out of
                // GetMetadata and lose every AnimeClick field for that item.
                _logger.LogWarning(
                    ex,
                    "AnimeClick: cache write failed for {Key}; continuing without caching",
                    key);
            }
            finally
            {
                DeleteFileBestEffort(tempPath);
                asyncLock.Release();
            }
        }
        finally
        {
            MutationGate.Release();
        }
    }

    public int ClearByPrefix(string prefix)
    {
        MutationGate.Wait();
        try
        {
            var safePrefix = SanitizePathComponent(prefix);
            var removed = 0;

            // Do not pass user-controlled prefixes as a glob: '*' and '?' are valid filename
            // characters on Linux and could otherwise broaden a targeted clear unexpectedly.
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*.json"))
            {
                var fileKey = Path.GetFileNameWithoutExtension(path);
                if (fileKey.StartsWith(safePrefix, StringComparison.Ordinal)
                    && DeletePathSynchronized(path))
                {
                    removed++;
                }
            }

            return removed;
        }
        finally
        {
            MutationGate.Release();
        }
    }

    public int ClearKey(string key)
    {
        MutationGate.Wait();
        try
        {
            return DeletePathSynchronized(GetPath(key)) ? 1 : 0;
        }
        finally
        {
            MutationGate.Release();
        }
    }

    /// <summary>
    /// Removes every JSON entry in the metadata cache. Individual locked/unavailable files
    /// are skipped so an administrative cleanup does not fail as a whole.
    /// </summary>
    public int ClearAll()
    {
        MutationGate.Wait();
        try
        {
            var removed = 0;
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*.json"))
            {
                if (DeletePathSynchronized(path))
                {
                    removed++;
                }
            }

            return removed;
        }
        finally
        {
            MutationGate.Release();
        }
    }

    private static SemaphoreSlim GetLock(string path) => Locks.Get(path);

    private bool DeletePathSynchronized(string path)
    {
        var asyncLock = GetLock(path);
        asyncLock.Wait();
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
        finally
        {
            asyncLock.Release();
        }
    }

    private string GetPath(string key)
        => Path.Combine(_cacheDirectory, SanitizeFileKey(key) + ".json");

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A cache miss is preferable to failing the metadata pipeline.
        }
        catch (UnauthorizedAccessException)
        {
            // A cache miss is preferable to failing the metadata pipeline.
        }
    }

    /// <summary>
    /// Budget in bytes for a cache file name, comfortably inside the 255-byte limit that ext4, btrfs
    /// and most other filesystems impose on a single path component. The ".json" suffix and a margin
    /// for future key prefixes are already accounted for.
    /// </summary>
    private const int MaximumFileNameBytes = 200;

    /// <summary>
    /// Replaces the characters a file name cannot contain. Used on its own for prefix comparison,
    /// where shortening the value would break the match.
    /// </summary>
    private static string SanitizePathComponent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

    private static string SanitizeFileKey(string key)
    {
        key = SanitizePathComponent(key);
        if (Encoding.UTF8.GetByteCount(key) <= MaximumFileNameBytes)
        {
            return key;
        }

        // A translation key is already ~190 characters of fixed structure — prefix, field, languages
        // and five hashes — before the anime's own id and slug are added, and an accented character
        // costs two bytes. A real title was enough to push the name past the limit, at which point
        // the write failed with "File name too long", SetAsync swallowed the IOException and the
        // consequences were entirely silent: the translation was never stored, so the same synopsis
        // was paid for again at every refresh, and the failure backoff was never written either, so
        // a broken endpoint was retried for every episode forever.
        //
        // Names that already fit are left exactly as they are, so no existing cache entry is
        // invalidated by this; only the ones that could never be written change shape.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        var budget = MaximumFileNameBytes - digest.Length - 1;
        var head = key;
        while (Encoding.UTF8.GetByteCount(head) > budget)
        {
            head = head[..(head.Length - 1)];
        }

        // Never leave a dangling high surrogate: on its own it encodes as a replacement character.
        if (head.Length > 0 && char.IsHighSurrogate(head[^1]))
        {
            head = head[..^1];
        }

        return head + "~" + digest;
    }
}
