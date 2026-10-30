using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The base of a manifest publisher, and the factory of the publisher a provisioned instance uses for
/// its gateway's endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Create"/> chooses the transport by the endpoint's scheme. For <c>http</c> and
/// <c>https</c> it returns the HTTP publisher. Publication is one <c>POST</c> of the manifest, written
/// by the options' content serializer, to the gateway's endpoint. The gateway answers with an
/// <see cref="ApplicationInstancePublication"/>: the snapshots the instance must restore and its lease, or
/// <c>204 No Content</c> for neither. Any other status refuses the publication:
/// <see cref="PublishAsync"/> throws, so the instance's host does not start, and the gateway tears
/// down an instance whose manifest never arrives. A renewal is a <c>PUT</c> of the same manifest to
/// the same endpoint: <c>404 Not Found</c> means the gateway does not know the instance. Both are
/// anonymous; a local gateway accepts them as such.
/// </para>
/// <para>
/// For <see cref="InProcessScheme"/> it returns a publisher for an endpoint inside the instance's
/// own process, where a gateway that runs its resources in that process receives them. Each
/// publication and renewal goes to the publisher registered for the endpoint
/// (<see cref="Register"/>) at the time it is made, with no serialization; one made while nothing is
/// registered fails as one to an HTTP endpoint that nothing listens on does.
/// </para>
/// <para>
/// Derive from this class to publish over another transport.
/// </para>
/// </remarks>
public abstract class ApplicationInstancePublisher : IApplicationInstancePublisher
{
    /// <summary>
    /// The URI scheme of an endpoint inside the instance's own process, such as
    /// <c>inprocess://local/</c>.
    /// </summary>
    public const string InProcessScheme = "inprocess";

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    protected ApplicationInstancePublisher()
    {
    }

    /// <inheritdoc/>
    public abstract ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a publisher for a gateway's endpoint: over HTTP for an <c>http</c> or <c>https</c>
    /// URL, or inside the process for an <see cref="InProcessScheme"/> URL.
    /// </summary>
    /// <param name="options">
    /// The gateway's endpoint, how the publisher gets its HTTP client, and its content serializer.
    /// They are read once: changing them later does not change the publisher. An in-process
    /// publisher uses only the endpoint.
    /// </param>
    /// <returns>The publisher.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ApplicationInstancePublisherOptions.Endpoint"/> is <see langword="null"/> or is not an
    /// absolute <c>http</c>, <c>https</c>, or <see cref="InProcessScheme"/> URL.
    /// </exception>
    public static ApplicationInstancePublisher Create(ApplicationInstancePublisherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Uri? endpoint = options.Endpoint;
        if (endpoint is not null && InProcessApplicationInstancePublisher.IsInProcess(endpoint))
        {
            return new InProcessApplicationInstancePublisher(endpoint);
        }

        if (endpoint is null || !endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(ErrorMessages.ManifestEndpointInvalid, nameof(options));
        }

        return new HttpApplicationInstancePublisher(
            endpoint,
            options.HttpClientFactory ?? HttpApplicationInstancePublisher.CreateDefaultClient,
            options.Serializer ?? JsonApplicationContentSerializer.Create());
    }

    /// <summary>
    /// Registers an endpoint inside this process: every publication and renewal a publisher of
    /// <see cref="Create"/> makes to it goes to <paramref name="publisher"/>, until the returned
    /// registration is disposed.
    /// </summary>
    /// <remarks>
    /// A gateway that runs its resources in its own process registers the endpoint it names in their
    /// <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable. The publisher it registers is
    /// the transport between the instances and the gateway, such as a channel the gateway drains.
    /// </remarks>
    /// <param name="endpoint">The endpoint, an absolute <see cref="InProcessScheme"/> URL such as <c>inprocess://local/</c>.</param>
    /// <param name="publisher">Where the publications and renewals made to the endpoint go.</param>
    /// <returns>The registration; disposing it removes the endpoint.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/> is not an absolute <see cref="InProcessScheme"/> URL, or is already
    /// registered.
    /// </exception>
    public static IDisposable Register(Uri endpoint, IApplicationInstancePublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(publisher);

        if (!InProcessApplicationInstancePublisher.IsInProcess(endpoint))
        {
            throw new ArgumentException(ErrorMessages.InProcessEndpointInvalid, nameof(endpoint));
        }

        return InProcessApplicationInstancePublisher.AddEndpoint(endpoint, publisher);
    }
}
