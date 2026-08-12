using System.Reflection;
using AnimeClick.Plugin.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class TestDoubles
{
    public static T Proxy<T>(Func<MethodInfo, object?[]?, object?>? handler = null)
        where T : class
        => DynamicProxy<T>.Create(handler);

    public static object? DefaultReturn(MethodInfo method)
    {
        var returnType = method.ReturnType;
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(string))
        {
            return string.Empty;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType == typeof(ValueTask))
        {
            return ValueTask.CompletedTask;
        }

        if (returnType.IsArray)
        {
            return Array.CreateInstance(returnType.GetElementType()!, 0);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [result]);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return Activator.CreateInstance(returnType, result);
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

internal class DynamicProxy<T> : DispatchProxy
    where T : class
{
    private Func<MethodInfo, object?[]?, object?>? _handler;

    public static T Create(Func<MethodInfo, object?[]?, object?>? handler)
    {
        var proxy = DispatchProxy.Create<T, DynamicProxy<T>>();
        ((DynamicProxy<T>)(object)proxy)._handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return _handler?.Invoke(targetMethod, args) ?? TestDoubles.DefaultReturn(targetMethod);
    }
}

internal sealed class TemporaryAnimeClickCache : IDisposable
{
    public TemporaryAnimeClickCache()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "animeclick-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        var paths = TestDoubles.Proxy<IApplicationPaths>((method, _) =>
            method.Name == "get_CachePath"
                ? RootPath
                : TestDoubles.DefaultReturn(method));
        Cache = new AnimeClickCacheService(paths, NullLogger<AnimeClickCacheService>.Instance);
    }

    public string RootPath { get; }

    public AnimeClickCacheService Cache { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // A failed test cleanup must not hide the assertion that produced the useful failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same principle as the production cache: cleanup is best-effort.
        }
    }
}
