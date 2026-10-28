using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>
/// Publishes a resource instance's manifest to its gateway over HTTP.
/// </summary>
/// <remarks>
/// Publication is one <c>POST</c> of the manifest, written by the content serializer, to the
/// endpoint. The gateway answers with the list of snapshots the instance must restore, or
/// <c>204 No Content</c> when there are none. Any other status refuses the publication:
/// <see cref="PublishAsync"/> throws, so the instance's host does not start, and the gateway tears
/// down an instance whose manifest never arrives. Publication is anonymous; a local gateway accepts
/// it as such.
/// </remarks>
internal sealed class HttpResourceManifestPublisher : ResourceManifestPublisher
{
    // Publishers created without a client factory share one handler, and so one connection pool.
    // Its connections are recycled, so a gateway that moves is reached at its new address.
    private static readonly SocketsHttpHandler _sharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

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
    /// Posts the manifest and returns the snapshots the gateway answers with.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client factory returned <see langword="null"/>.</exception>
    /// <exception cref="HttpRequestException">The gateway could not be reached, or refused the manifest.</exception>
    /// <exception cref="ResourceContentException">The gateway's answer is not a list of snapshots.</exception>
    public override async ValueTask<IReadOnlyList<ResourceStateSnapshot>> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var content = new ReadOnlyMemoryContent(_serializer.Serialize(manifest));
        content.Headers.ContentType = new MediaTypeHeaderValue(_serializer.MediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_serializer.MediaType));

        // One client per publication, the way a client factory's clients are used; disposing it
        // leaves a factory's pooled handler, or the shared one, in place.
        using HttpClient client = _createClient.Invoke() ?? throw new InvalidOperationException(ErrorMessages.ManifestHttpClientNull);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ManifestPublicationRefused, _endpoint, (int)response.StatusCode),
                inner: null,
                response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return [];
        }

        return _serializer.Deserialize<IReadOnlyList<ResourceStateSnapshot>>(body) ?? [];
    }

    /// <summary>
    /// Creates a client over the handler every publisher without a client factory shares.
    /// </summary>
    public static HttpClient CreateDefaultClient()
    {
        return new HttpClient(_sharedHandler, disposeHandler: false);
    }
}
