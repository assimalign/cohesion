using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Internal;
using Assimalign.Cohesion.ConfigurationStore.Client;

namespace Assimalign.Cohesion.ApplicationModel;

// Deviates from the repo interface-first rule per the owner-approved BYO design (2026-09-25): the
// design names this concrete provider as the package's public type so an application can register
// it in builder.Providers.Sources itself; the contract a gateway depends on is IResourceSourceProvider.

/// <summary>
/// Resolves <c>&lt;store&gt;:&lt;namespace&gt;</c> <see cref="ResourceMountKind.Configuration"/>
/// mount sources by reading the named namespace from a ConfigurationStore resource of the
/// application.
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>builder.UseConfigurationStore(store)</c>, which binds it under the store
/// resource's name in <see cref="ApplicationProviders.Sources"/>. The gateway then hands every read
/// a <see cref="ResourceProviderConnection"/> to that store, and the provider issues one
/// authenticated <c>GET /cohesion/v1/namespaces?name=&lt;namespace&gt;</c> through
/// <see cref="ConfigurationStoreClient"/>. The gateway serializes the returned entries to the
/// mount as a JSON object with ordinally sorted keys.
/// </para>
/// <para>
/// The store serves its namespace route beneath the root of its control-plane endpoint whatever
/// the manifest control-plane path is, so the provider reads from the scheme, host, and port of
/// <see cref="ResourceProviderConnection.ControlPlaneAddress"/>. That is the request the gateway
/// issued before the provider seams existed.
/// </para>
/// <para>
/// The provider supplies Configuration mounts only: <see cref="IResourceSourceProvider.ReadSecretAsync"/>
/// and <see cref="IResourceSourceProvider.ReadCertificateAsync"/> keep their default bodies and throw
/// <see cref="NotSupportedException"/>. It holds no state; one instance may serve any number of
/// stores and concurrent reads.
/// </para>
/// </remarks>
public sealed class ConfigurationStoreSourceProvider : IResourceSourceProvider
{
    private const string configurationStoreKind = "ConfigurationStore";

    private readonly Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> _transportFactory;

    /// <summary>
    /// Initializes a provider that reads over a new HTTP transport per request, with redirects
    /// and cookies disabled and the store's TLS certificate validated by the connection's
    /// <see cref="ResourceProviderConnection.ServerCertificateValidator"/>.
    /// </summary>
    public ConfigurationStoreSourceProvider()
        : this(ConfigurationStoreHttpTransport.Create)
    {
    }

    /// <summary>
    /// Initializes a provider over a caller-supplied transport factory.
    /// </summary>
    /// <param name="transportFactory">
    /// Creates the transport for one read from the connection's server-certificate validator. The
    /// provider disposes the transport once the read finishes.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="transportFactory"/> is <see langword="null"/>.</exception>
    internal ConfigurationStoreSourceProvider(
        Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);

        _transportFactory = transportFactory;
    }

    /// <summary>
    /// Gets <c>ConfigurationStore</c>: the source name must be a ConfigurationStore resource of the
    /// declaring application.
    /// </summary>
    public string? ResourceKind => configurationStoreKind;

    /// <summary>
    /// Reads the namespace named by <see cref="ResourceSourceRequest.Key"/> from the ConfigurationStore
    /// resource in <see cref="ResourceSourceRequest.Store"/>.
    /// </summary>
    /// <param name="request">
    /// The Configuration mount being resolved. <see cref="ResourceSourceRequest.Store"/> must connect to
    /// a ConfigurationStore resource.
    /// </param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The namespace's configuration keys and their string or <see langword="null"/> values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourceSourceRequest.Store"/> is <see langword="null"/> or connects to a resource that
    /// is not a ConfigurationStore; its control-plane address is not an HTTP(S) endpoint; its bearer
    /// credential is blank; or <see cref="ResourceSourceRequest.Key"/> is blank.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ResourceSourceRequest.Kind"/> is not <see cref="ResourceMountKind.Configuration"/>.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// The store cannot be reached or refuses the read; <see cref="HttpRequestException.StatusCode"/>
    /// carries the store's status, for example <c>404</c> for a namespace the store does not hold.
    /// </exception>
    /// <exception cref="JsonException">The store returns an invalid or <c>null</c> namespace document.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public async ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind != ResourceMountKind.Configuration)
        {
            throw new NotSupportedException(
                $"Mount '{request.Mount}' on resource '{request.Consumer}' is '{request.Kind}', but a " +
                $"{configurationStoreKind} source supplies Configuration mounts only.");
        }

        ResourceProviderConnection store = request.Store ?? throw new ArgumentException(
            $"Mount '{request.Mount}' on resource '{request.Consumer}' reads a {configurationStoreKind} " +
            "source, but the request carries no connection to the store resource.",
            nameof(request));

        if (!string.Equals(store.ResourceKind, configurationStoreKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Mount '{request.Mount}' on resource '{request.Consumer}' reads source resource " +
                $"'{store.Resource}' of kind '{store.ResourceKind}', but this provider reads " +
                $"{configurationStoreKind} resources only.",
                nameof(request));
        }

        Uri endpoint = GetEndpointRoot(store.ControlPlaneAddress);
        var credential = new ClientCredential(store.BearerCredential);
        using HttpMessageInvoker transport = _transportFactory.Invoke(store.ServerCertificateValidator);

        return await ConfigurationStoreClient
            .Create(endpoint, credential, transport)
            .GetNamespaceAsync(request.Key, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Uri GetEndpointRoot(Uri controlPlaneAddress)
    {
        Uri address = Uri.ThrowIfNotEndpoint(controlPlaneAddress);
        return Uri.CreateEndpoint(address.Scheme, address.Host, address.Port);
    }
}
