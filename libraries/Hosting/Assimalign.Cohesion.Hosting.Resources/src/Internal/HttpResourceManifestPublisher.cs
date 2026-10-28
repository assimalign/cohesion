using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>
/// Publishes a resource instance's manifest to its gateway over HTTP, and renews its registration.
/// </summary>
/// <remarks>
/// Publication is one <c>POST</c> of the manifest, written by the content serializer, to the
/// endpoint. The gateway answers with a <see cref="ResourcePublication"/>, or <c>204 No Content</c>
/// when it has no snapshots and no lease. Any other status refuses the publication:
/// <see cref="PublishAsync"/> throws, so the instance's host does not start, and the gateway tears
/// down an instance whose manifest never arrives. A renewal is a <c>PUT</c> of the same manifest to
/// the same endpoint; <c>404 Not Found</c> means the gateway does not know the instance. Both are
/// anonymous; a local gateway accepts them as such.
/// </remarks>
internal sealed class HttpResourceManifestPublisher : ResourceManifestPublisher
{
    // Publishers created without a client factory share one handler, and so one connection pool.
    // Its connections are recycled, so a gateway that moves is reached at its new address.
    private static readonly SocketsHttpHandler _sharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    private static readonly ResourcePublication _nothing = new([], null);

    private readonly Uri _endpoint;
    private readonly Func<HttpClient> _createClient;
    private readonly IResourceContentSerializer _serializer;

    public HttpResourceManifestPublisher(Uri endpoint, Func<HttpClient> createClient, IResourceContentSerializer serializer)
    {
        _endpoint = endpoint;
        _createClient = createClient;
        _serializer = serializer;
    }

    /// <summary>
    /// Posts the manifest and returns what the gateway answers with.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client factory returned <see langword="null"/>.</exception>
    /// <exception cref="HttpRequestException">The gateway could not be reached, or refused the manifest.</exception>
    /// <exception cref="ResourceContentException">The gateway's answer is not a publication.</exception>
    public override async ValueTask<ResourcePublication> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, manifest);

        // One client per publication, the way a client factory's clients are used; disposing it
        // leaves a factory's pooled handler, or the shared one, in place.
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Refused(ErrorMessages.ManifestPublicationRefused, response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return _nothing;
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return _nothing;
        }

        return _serializer.Deserialize<ResourcePublication>(body) ?? _nothing;
    }

    /// <summary>
    /// Puts the manifest again to renew the instance's registration.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client factory returned <see langword="null"/>.</exception>
    /// <exception cref="HttpRequestException">The gateway could not be reached, or refused the renewal.</exception>
    public override async ValueTask<bool> RenewAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, manifest);
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw Refused(ErrorMessages.ManifestRenewalRefused, response.StatusCode);
        }

        return true;
    }

    /// <summary>
    /// Creates a client over the handler every publisher without a client factory shares.
    /// </summary>
    public static HttpClient CreateDefaultClient()
    {
        return new HttpClient(_sharedHandler, disposeHandler: false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, ResourceManifest manifest)
    {
        var content = new ReadOnlyMemoryContent(_serializer.Serialize(manifest));
        content.Headers.ContentType = new MediaTypeHeaderValue(_serializer.MediaType);

        var request = new HttpRequestMessage(method, _endpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_serializer.MediaType));
        return request;
    }

    private HttpClient CreateClient()
    {
        return _createClient.Invoke() ?? throw new InvalidOperationException(ErrorMessages.ManifestHttpClientNull);
    }

    private HttpRequestException Refused(string format, HttpStatusCode status)
    {
        return new HttpRequestException(
            string.Format(CultureInfo.CurrentCulture, format, _endpoint, (int)status),
            inner: null,
            status);
    }
}
