using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Internal;
using Assimalign.Cohesion.SecretStore.Client;

namespace Assimalign.Cohesion.ApplicationModel;

// Deviates from the repo interface-first rule per the owner-approved BYO design (2026-09-25): the
// design names this concrete store as the package's public type so an application can bind it in
// builder.Providers.TrustStore itself; the contract a gateway depends on is ITrustedIssuerStore.

/// <summary>
/// Persists the peer issuers an application trusts in a SecretStore resource of the application.
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>builder.UseSecretStore(store).AsTrustStore()</c>, which binds it to the
/// store resource in <see cref="ApplicationProviders.TrustStore"/>.
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="ReadAsync"/> issues <c>GET &lt;control plane&gt;/secrets?path=trusted-issuers.json</c>
///     and parses the exported <c>{"issuers":[...]}</c> document. A <c>404</c> means nothing has
///     been persisted yet and returns <see langword="null"/>.
///   </description></item>
///   <item><description>
///     <see cref="AddAsync"/> sends a <c>cohesion.trust.add</c> command
///     (<c>POST &lt;control plane&gt;/commands</c>) whose key is the issuer name, whose payload is
///     the issuer's public JWK (or <c>{"trustKey":...,"allowedCommandKinds":[...]}</c> for a
///     restricted grant), and whose id is <c>trust-</c> plus the lowercase hex SHA-256 of the owner,
///     issuer, and payload — so an identical grant is idempotent at the store.
///   </description></item>
/// </list>
/// <para>
/// The store holds no state; one instance may serve any number of concurrent calls.
/// </para>
/// </remarks>
public sealed class SecretStoreTrustedIssuerStore : ITrustedIssuerStore
{
    private readonly Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> _transportFactory;

    /// <summary>
    /// Initializes a trust store that calls the SecretStore over a new HTTP transport per
    /// operation, with redirects and cookies disabled and the store's TLS certificate validated by
    /// the connection's <see cref="ResourceProviderConnection.ServerCertificateValidator"/>.
    /// </summary>
    public SecretStoreTrustedIssuerStore()
        : this(SecretStoreHttpTransport.Create)
    {
    }

    /// <summary>
    /// Initializes a trust store over a caller-supplied transport factory.
    /// </summary>
    /// <param name="transportFactory">
    /// Creates the transport for one operation from the connection's server-certificate validator.
    /// The store disposes the transport once the operation finishes.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="transportFactory"/> is <see langword="null"/>.</exception>
    internal SecretStoreTrustedIssuerStore(Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);

        _transportFactory = transportFactory;
    }

    /// <summary>
    /// Gets <c>SecretStore</c>: the trust store must be bound to a SecretStore resource of the
    /// declaring application.
    /// </summary>
    public string? ResourceKind => SecretStoreProtocol.ResourceKind;

    /// <summary>
    /// Reads the trusted issuers the bound SecretStore exports.
    /// </summary>
    /// <param name="store">The connection to the bound SecretStore resource.</param>
    /// <param name="cancellationToken">Signals that the read should be abandoned.</param>
    /// <returns>
    /// The exported issuers in document order, or <see langword="null"/> when the store answers
    /// <c>404</c> because nothing has been persisted yet.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="store"/> connects to a resource that is not a SecretStore, its control-plane
    /// address is not an HTTP(S) endpoint, or its bearer credential is blank.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// The store cannot be reached or refuses the read with a status other than <c>404</c>.
    /// </exception>
    /// <exception cref="JsonException">The store returns content that is not JSON.</exception>
    /// <exception cref="InvalidDataException">
    /// The document is not an <c>{"issuers":[...]}</c> object, repeats an issuer, or carries an
    /// invalid trust key or command-kind restriction.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public async ValueTask<IReadOnlyList<TrustedIssuer>?> ReadAsync(
        ResourceProviderConnection? store,
        CancellationToken cancellationToken = default)
    {
        ResourceProviderConnection connection = SecretStoreProtocol.RequireStore(store, "trust store", nameof(store));
        ReadOnlyMemory<byte> content;
        using (HttpMessageInvoker transport = _transportFactory.Invoke(connection.ServerCertificateValidator))
        {
            try
            {
                content = await SecretStoreProtocol.CreateClient(connection, transport)
                    .GetSecretAsync(SecretStoreProtocol.TrustedIssuersPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                // A store that has never held a grant has no persisted document yet.
                return null;
            }
        }

        return SecretStoreTrustedIssuerDocument.Parse(content);
    }

    /// <summary>
    /// Persists one trusted-issuer grant in the bound SecretStore through a
    /// <c>cohesion.trust.add</c> command.
    /// </summary>
    /// <param name="store">The connection to the bound SecretStore resource.</param>
    /// <param name="owner">
    /// The ownership identity recorded with the grant. The store accepts a grant only when it equals
    /// the authenticated caller's <c>&lt;application&gt;@&lt;subject&gt;</c>, which is the model's
    /// <see cref="IApplicationModel.Owner"/> for a gateway-minted credential.
    /// </param>
    /// <param name="issuer">The issuer to trust.</param>
    /// <param name="cancellationToken">Signals that the write should be abandoned.</param>
    /// <returns>A task that completes when the store accepts the grant.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="store"/> or <paramref name="issuer"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is blank; <paramref name="store"/> connects to a resource that is not
    /// a SecretStore, its control-plane address is not an HTTP(S) endpoint, or its bearer credential
    /// is blank.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// The store cannot be reached or refuses the grant, for example <c>403</c> when the owner is not
    /// the authenticated caller or <c>409</c> when another owner already holds the issuer.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public async ValueTask AddAsync(
        ResourceProviderConnection? store,
        string owner,
        TrustedIssuer issuer,
        CancellationToken cancellationToken = default)
    {
        ResourceProviderConnection connection = SecretStoreProtocol.RequireStore(store, "trust store", nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(issuer);

        ResourceCommand command = SecretStoreTrustGrant.Create(owner, issuer);
        using HttpMessageInvoker transport = _transportFactory.Invoke(connection.ServerCertificateValidator);
        await SecretStoreProtocol.CreateClient(connection, transport)
            .SendCommandAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }
}
