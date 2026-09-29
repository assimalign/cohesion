using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Internal;
using Assimalign.Cohesion.SecretStore.Client;

namespace Assimalign.Cohesion.ApplicationModel;

// Deviates from the repo interface-first rule per the owner-approved BYO design (2026-09-25): the
// design names this concrete provider as the package's public type so an application can register
// it in builder.Providers.Sources itself; the contract a gateway depends on is IResourceSourceProvider.

/// <summary>
/// Resolves <c>&lt;store&gt;:&lt;key&gt;</c> <see cref="ResourceMountKind.Secret"/> mount sources —
/// secret values and endpoint certificates — by reading them from a SecretStore resource of the
/// application.
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>builder.UseSecretStore(store)</c>, which binds it under the store resource's
/// name in <see cref="ApplicationProviders.Sources"/>. The gateway then hands every read a
/// <see cref="ResourceProviderConnection"/> to that store, and the provider calls the store's
/// control plane through <see cref="SecretStoreClient.CreateForControlPlane(Uri, ClientCredential, HttpMessageInvoker)"/>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="ReadSecretAsync"/> issues <c>GET &lt;control plane&gt;/secrets?path=&lt;key&gt;</c>
///     and returns the response bytes.
///   </description></item>
///   <item><description>
///     <see cref="ReadCertificateAsync"/> issues <c>GET &lt;control plane&gt;/certificates?name=&lt;key&gt;</c>
///     for the certificate bundle, then <c>GET &lt;control plane&gt;/certificates?name=ca/root</c> for
///     the store authority's root, which becomes <see cref="ResourceCertificate.TrustAnchorsPem"/>.
///   </description></item>
/// </list>
/// <para>
/// Configuration mounts are not supplied: <see cref="IResourceSourceProvider.ReadConfigurationAsync"/>
/// keeps its default body and throws <see cref="NotSupportedException"/>. The provider holds no
/// state; one instance may serve any number of stores and concurrent reads.
/// </para>
/// </remarks>
public sealed class SecretStoreSourceProvider : IResourceSourceProvider
{
    private readonly Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> _transportFactory;

    /// <summary>
    /// Initializes a provider that reads over a new HTTP transport per operation, with redirects
    /// and cookies disabled and the store's TLS certificate validated by the connection's
    /// <see cref="ResourceProviderConnection.ServerCertificateValidator"/>.
    /// </summary>
    public SecretStoreSourceProvider()
        : this(SecretStoreHttpTransport.Create)
    {
    }

    /// <summary>
    /// Initializes a provider over a caller-supplied transport factory.
    /// </summary>
    /// <param name="transportFactory">
    /// Creates the transport for one operation from the connection's server-certificate validator.
    /// The provider disposes the transport once the operation finishes.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="transportFactory"/> is <see langword="null"/>.</exception>
    internal SecretStoreSourceProvider(Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);

        _transportFactory = transportFactory;
    }

    /// <summary>
    /// Gets <c>SecretStore</c>: the source name must be a SecretStore resource of the declaring
    /// application.
    /// </summary>
    public string? ResourceKind => SecretStoreProtocol.ResourceKind;

    /// <summary>
    /// Reads the secret at <see cref="ResourceSourceRequest.Key"/> from the SecretStore resource in
    /// <see cref="ResourceSourceRequest.Store"/>.
    /// </summary>
    /// <param name="request">
    /// The Secret mount being resolved. <see cref="ResourceSourceRequest.Store"/> must connect to a
    /// SecretStore resource.
    /// </param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The secret bytes the store holds at the path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourceSourceRequest.Store"/> is <see langword="null"/> or connects to a resource
    /// that is not a SecretStore; its control-plane address is not an HTTP(S) endpoint; its bearer
    /// credential is blank; or <see cref="ResourceSourceRequest.Key"/> is blank.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ResourceSourceRequest.Kind"/> is not <see cref="ResourceMountKind.Secret"/>.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// The store cannot be reached or refuses the read; <see cref="HttpRequestException.StatusCode"/>
    /// carries the store's status, for example <c>404</c> for a path the store does not hold.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ResourceProviderConnection store = RequireSecretMountStore(request);
        using HttpMessageInvoker transport = _transportFactory.Invoke(store.ServerCertificateValidator);

        return await SecretStoreProtocol.CreateClient(store, transport)
            .GetSecretAsync(request.Key, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the certificate named by <see cref="ResourceSourceRequest.Key"/> from the SecretStore
    /// resource in <see cref="ResourceSourceRequest.Store"/>, together with the store authority's
    /// root certificate.
    /// </summary>
    /// <param name="request">
    /// The endpoint certificate mount being resolved. <see cref="ResourceSourceRequest.Store"/> must
    /// connect to a SecretStore resource.
    /// </param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>
    /// The PEM bundle the store returns for the name (for a <c>certs/&lt;name&gt;</c> key the store
    /// issues the leaf on first resolution), and the store's <c>ca/root</c> certificate as its trust
    /// anchors.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourceSourceRequest.Store"/> is <see langword="null"/> or connects to a resource
    /// that is not a SecretStore; its control-plane address is not an HTTP(S) endpoint; its bearer
    /// credential is blank; or <see cref="ResourceSourceRequest.Key"/> is blank.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ResourceSourceRequest.Kind"/> is not <see cref="ResourceMountKind.Secret"/>.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// The store cannot be reached or refuses either read; <see cref="HttpRequestException.StatusCode"/>
    /// carries the store's status.
    /// </exception>
    /// <exception cref="InvalidDataException">The store returns an empty certificate.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>
    /// The leaf is read first and the root second, the order the gateway used. The gateway validates
    /// the bundle and adds the anchors to the application's transport trust only when the bundle is
    /// usable.
    /// </remarks>
    public async ValueTask<ResourceCertificate> ReadCertificateAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ResourceProviderConnection store = RequireSecretMountStore(request);
        using HttpMessageInvoker transport = _transportFactory.Invoke(store.ServerCertificateValidator);
        ISecretStoreClient client = SecretStoreProtocol.CreateClient(store, transport);

        string certificate = await client
            .GetCertificateAsync(request.Key, cancellationToken)
            .ConfigureAwait(false);
        string anchors = await client
            .GetCertificateAsync(SecretStoreProtocol.RootCertificateName, cancellationToken)
            .ConfigureAwait(false);
        return new ResourceCertificate(certificate, anchors);
    }

    private static ResourceProviderConnection RequireSecretMountStore(ResourceSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind != ResourceMountKind.Secret)
        {
            throw new NotSupportedException(
                $"Mount '{request.Mount}' on resource '{request.Consumer}' is '{request.Kind}', but a " +
                $"{SecretStoreProtocol.ResourceKind} source supplies Secret mounts only.");
        }

        ResourceProviderConnection store = request.Store ?? throw new ArgumentException(
            $"Mount '{request.Mount}' on resource '{request.Consumer}' reads a {SecretStoreProtocol.ResourceKind} " +
            "source, but the request carries no connection to the store resource.",
            nameof(request));
        SecretStoreProtocol.EnsureSecretStore(store, nameof(request));
        return store;
    }
}
