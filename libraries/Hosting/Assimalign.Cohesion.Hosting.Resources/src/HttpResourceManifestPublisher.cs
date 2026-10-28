using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Publishes a resource instance's manifest to its gateway over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Publication is one <c>POST</c> of the manifest as JSON (<see cref="ResourceJsonContext"/>) to
/// <see cref="Endpoint"/>. The gateway answers with a JSON array of the snapshots the instance must
/// restore, or <c>204 No Content</c> when there are none. Any other status refuses the publication:
/// <see cref="PublishAsync"/> throws, so the instance's host does not start, and the gateway tears
/// down an instance whose manifest never arrives.
/// </para>
/// <para>
/// A gateway that provisions the instance names the endpoint in the
/// <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable (<see cref="FromEnvironment"/>).
/// Publication is anonymous; a local gateway accepts it as such.
/// </para>
/// </remarks>
public sealed class HttpResourceManifestPublisher : IResourceManifestPublisher, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    private HttpResourceManifestPublisher(Uri endpoint, HttpClient client, bool ownsClient)
    {
        Endpoint = endpoint;
        _client = client;
        _ownsClient = ownsClient;
    }

    /// <summary>
    /// Gets the URL the manifest is posted to.
    /// </summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// Creates a publisher that posts to an endpoint with its own HTTP client.
    /// </summary>
    /// <param name="endpoint">The absolute <c>http</c> or <c>https</c> URL the gateway receives manifests on.</param>
    /// <returns>The publisher; dispose it to release its HTTP client.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> is not an absolute <c>http</c> or <c>https</c> URL.</exception>
    public static HttpResourceManifestPublisher Create(Uri endpoint)
    {
        ValidateEndpoint(endpoint);

        return new HttpResourceManifestPublisher(endpoint, new HttpClient(), ownsClient: true);
    }

    /// <summary>
    /// Creates a publisher that posts to an endpoint with the caller's HTTP client.
    /// </summary>
    /// <param name="endpoint">The absolute <c>http</c> or <c>https</c> URL the gateway receives manifests on.</param>
    /// <param name="client">The HTTP client; the caller keeps ownership of it.</param>
    /// <returns>The publisher.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> or <paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> is not an absolute <c>http</c> or <c>https</c> URL.</exception>
    public static HttpResourceManifestPublisher Create(Uri endpoint, HttpClient client)
    {
        ValidateEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(client);

        return new HttpResourceManifestPublisher(endpoint, client, ownsClient: false);
    }

    /// <summary>
    /// Creates a publisher for the endpoint the provisioning gateway named in the
    /// <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable.
    /// </summary>
    /// <returns>The publisher, or <see langword="null"/> when the variable is unset: no gateway manages the instance.</returns>
    /// <exception cref="ArgumentException">The variable is not an absolute <c>http</c> or <c>https</c> URL.</exception>
    public static HttpResourceManifestPublisher? FromEnvironment()
    {
        string? value = System.Environment.GetEnvironmentVariable(AppEnvironment.Variables.ManifestEndpoint);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint))
        {
            throw new ArgumentException(ErrorMessages.ManifestEndpointInvalid, AppEnvironment.Variables.ManifestEndpoint);
        }

        return Create(endpoint);
    }

    /// <summary>
    /// Posts the manifest and returns the snapshots the gateway answers with.
    /// </summary>
    /// <param name="manifest">The instance's manifest.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>The snapshots to restore; empty when the gateway has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="HttpRequestException">The gateway could not be reached, or refused the manifest.</exception>
    /// <exception cref="JsonException">The gateway's answer is not a list of snapshots.</exception>
    public async ValueTask<IReadOnlyList<ResourceStateSnapshot>> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using JsonContent content = JsonContent.Create(manifest, ResourceJsonContext.Default.ResourceManifest);
        using HttpResponseMessage response = await _client.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ManifestPublicationRefused, Endpoint, (int)response.StatusCode),
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

        return JsonSerializer.Deserialize(body, ResourceJsonContext.Default.IReadOnlyListResourceStateSnapshot) ?? [];
    }

    /// <summary>
    /// Releases the HTTP client when the publisher created it.
    /// </summary>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(ErrorMessages.ManifestEndpointInvalid, nameof(endpoint));
        }
    }
}
