using System.Net;
using AnimeClick.Plugin.Configuration;

namespace AnimeClick.Plugin.Services;

/// <summary>Redirects are validated before sending another request, including image proxy requests.</summary>
internal static class AnimeClickHttp
{
    internal const string ClientName = "AnimeClick.NoRedirect";

    internal static async Task<HttpResponseMessage> GetImageAsync(
        IHttpClientFactory factory, string url, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(ClientName);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.MaxResponseContentBufferSize = 12 * 1024 * 1024;
        return await GetAsync(client, url, configuration, imagesOnly: true, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string url, PluginConfiguration configuration, bool imagesOnly, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!TryResolve(configuration.BaseUrl, url, imagesOnly, out var uri))
                throw new HttpRequestException("AnimeClick refused an untrusted request or redirect destination.");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", AnimeClickClient.GetEffectiveUserAgent(configuration));
            request.Headers.Referrer = new Uri(configuration.BaseUrl);
            if (!imagesOnly) request.Headers.TryAddWithoutValidation("Cookie", "ac_campaign=show");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("AnimeClick returned a redirect without a destination.");
            url = new Uri(uri, location).AbsoluteUri;
        }
        throw new HttpRequestException("AnimeClick returned too many redirects.");
    }

    internal static bool TryResolve(string baseUrl, string url, bool imagesOnly, out Uri uri)
    {
        uri = null!;
        if (imagesOnly)
        {
            if (AnimeClickArtwork.IsTrustedUrl(url)) { uri = new Uri(url); return true; }
            return AnimeClickClient.TryResolveAllowedImageUri(baseUrl, url, out uri);
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin)
            || !Uri.TryCreate(origin, url, out var target)
            || !string.IsNullOrEmpty(target.UserInfo)) return false;
        var sameOrigin = target.Scheme == origin.Scheme && target.IdnHost == origin.IdnHost && target.Port == origin.Port
            && target.Scheme is "http" or "https";
        if (!sameOrigin && !AnimeClickClient.TryResolveAllowedImageUri(baseUrl, url, out _)) return false;
        uri = target;
        return true;
    }
}
