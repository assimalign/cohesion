using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The base of a manifest publisher, and the factory of the HTTP publisher a provisioned instance
/// uses.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Create"/> returns the HTTP publisher. Publication is one <c>POST</c> of the manifest,
/// written by the options' content serializer, to the gateway's endpoint. The gateway answers with a
/// <see cref="ResourcePublication"/>: the snapshots the instance must restore and its lease, or
/// <c>204 No Content</c> for neither. Any other status refuses the publication:
/// <see cref="PublishAsync"/> throws, so the instance's host does not start, and the gateway tears
/// down an instance whose manifest never arrives. A renewal is a <c>PUT</c> of the same manifest to
/// the same endpoint: <c>404 Not Found</c> means the gateway does not know the instance. Both are
/// anonymous; a local gateway accepts them as such.
/// </para>
/// <para>
/// Derive from this class to publish over another transport.
/// </para>
/// </remarks>
public abstract class ResourceManifestPublisher : IResourceManifestPublisher
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    protected ResourceManifestPublisher()
    {
    }

    /// <inheritdoc/>
    public abstract ValueTask<ResourcePublication> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract ValueTask<bool> RenewAsync(ResourceManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a publisher that posts the manifest to a gateway over HTTP.
    /// </summary>
    /// <param name="options">
    /// The gateway's endpoint, how the publisher gets its HTTP client, and its content serializer.
    /// They are read once: changing them later does not change the publisher.
    /// </param>
    /// <returns>The publisher.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourceManifestPublisherOptions.Endpoint"/> is <see langword="null"/> or is not an
    /// absolute <c>http</c> or <c>https</c> URL.
    /// </exception>
    public static ResourceManifestPublisher Create(ResourceManifestPublisherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Uri? endpoint = options.Endpoint;
        if (endpoint is null || !endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(ErrorMessages.ManifestEndpointInvalid, nameof(options));
        }

        return new HttpResourceManifestPublisher(
            endpoint,
            options.HttpClientFactory ?? HttpResourceManifestPublisher.CreateDefaultClient,
            options.Serializer ?? JsonResourceContentSerializer.Create());
    }
}
