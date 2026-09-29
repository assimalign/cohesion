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
// design names this concrete authority as the package's public type so an application can bind it
// in builder.Providers.CertificateAuthority itself; the contract a gateway depends on is
// IResourceCertificateAuthority.

/// <summary>
/// Issues TLS leaves for resource endpoints from a SecretStore resource's private certificate
/// authority.
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>builder.UseSecretStore(store).AsCertificateAuthority()</c>, which binds it
/// to the store resource in <see cref="ApplicationProviders.CertificateAuthority"/>. For each leaf
/// the provider issues <c>GET &lt;control plane&gt;/certificates?name=certs/&lt;leaf&gt;</c> — the
/// store creates a durable leaf on first resolution of a <c>certs/</c> name and returns the same
/// leaf (renewed near expiry) afterwards — then
/// <c>GET &lt;control plane&gt;/certificates?name=ca/root</c> for the trust anchors.
/// </para>
/// <para>
/// The store chooses the leaf's subject alternative names itself (the leaf name,
/// <c>localhost</c>, <c>127.0.0.1</c>, and <c>::1</c>): its certificate read carries no SAN list,
/// so <see cref="ResourceCertificateRequest.SubjectAlternativeNames"/> is not forwarded. That is
/// the gateway's behaviour before the provider seams existed; honouring the request's names is a
/// recorded follow-up in this package's design notes.
/// </para>
/// <para>
/// The provider holds no state; one instance may serve any number of concurrent requests.
/// </para>
/// </remarks>
public sealed class SecretStoreCertificateAuthority : IResourceCertificateAuthority
{
    private readonly Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> _transportFactory;

    /// <summary>
    /// Initializes an authority that calls the store over a new HTTP transport per leaf, with
    /// redirects and cookies disabled and the store's TLS certificate validated by the connection's
    /// <see cref="ResourceProviderConnection.ServerCertificateValidator"/>.
    /// </summary>
    public SecretStoreCertificateAuthority()
        : this(SecretStoreHttpTransport.Create)
    {
    }

    /// <summary>
    /// Initializes an authority over a caller-supplied transport factory.
    /// </summary>
    /// <param name="transportFactory">
    /// Creates the transport for one leaf from the connection's server-certificate validator. The
    /// authority disposes the transport once the leaf is issued.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="transportFactory"/> is <see langword="null"/>.</exception>
    internal SecretStoreCertificateAuthority(Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);

        _transportFactory = transportFactory;
    }

    /// <summary>
    /// Gets <c>SecretStore</c>: the authority must be bound to a SecretStore resource of the
    /// declaring application.
    /// </summary>
    public string? ResourceKind => SecretStoreProtocol.ResourceKind;

    /// <summary>
    /// Issues (or returns the already-issued) leaf <c>certs/&lt;LeafName&gt;</c> from the bound
    /// SecretStore.
    /// </summary>
    /// <param name="request">The leaf to issue; <see cref="ResourceCertificateRequest.LeafName"/> names it.</param>
    /// <param name="authority">The connection to the bound SecretStore resource.</param>
    /// <param name="cancellationToken">Signals that issuance should be abandoned.</param>
    /// <returns>
    /// The PEM bundle (leaf, PKCS#8 key, issuer chain) and the store's <c>ca/root</c> certificate as
    /// its trust anchors.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="authority"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourceCertificateRequest.LeafName"/> is blank; <paramref name="authority"/>
    /// connects to a resource that is not a SecretStore; its control-plane address is not an HTTP(S)
    /// endpoint; or its bearer credential is blank.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// The store cannot be reached or refuses either read; <see cref="HttpRequestException.StatusCode"/>
    /// carries the store's status.
    /// </exception>
    /// <exception cref="InvalidDataException">The store returns an empty certificate.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public async ValueTask<ResourceCertificate> IssueAsync(
        ResourceCertificateRequest request,
        ResourceProviderConnection? authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LeafName, nameof(request));
        ResourceProviderConnection store = SecretStoreProtocol.RequireStore(
            authority,
            "certificate authority",
            nameof(authority));

        using HttpMessageInvoker transport = _transportFactory.Invoke(store.ServerCertificateValidator);
        ISecretStoreClient client = SecretStoreProtocol.CreateClient(store, transport);

        string certificate = await client
            .GetCertificateAsync(SecretStoreProtocol.LeafCertificatePrefix + request.LeafName, cancellationToken)
            .ConfigureAwait(false);
        string anchors = await client
            .GetCertificateAsync(SecretStoreProtocol.RootCertificateName, cancellationToken)
            .ConfigureAwait(false);
        return new ResourceCertificate(certificate, anchors);
    }
}
