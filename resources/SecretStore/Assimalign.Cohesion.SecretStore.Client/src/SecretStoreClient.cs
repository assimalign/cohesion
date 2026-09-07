using System;
using System.Net.Http;

namespace Assimalign.Cohesion.SecretStore.Client;

/// <summary>
/// Creates secret-store protocol clients.
/// </summary>
public static class SecretStoreClient
{
    private static readonly HttpMessageInvoker _sharedTransport = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        },
        disposeHandler: true);

    /// <summary>
    /// Creates a client for a secret-store endpoint. Creation performs no network I/O.
    /// </summary>
    /// <param name="endpoint">The HTTP or HTTPS endpoint exposed by the secret store.</param>
    /// <param name="credential">The opaque bootstrap credential presented with each request.</param>
    /// <returns>A client bound to the endpoint and credential.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoint"/> or <paramref name="credential"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/> is not an endpoint URI or does not use HTTP or HTTPS.
    /// </exception>
    public static ISecretStoreClient Create(
        Uri endpoint,
        ClientCredential credential)
    {
        return Create(endpoint, credential, _sharedTransport);
    }

    internal static ISecretStoreClient Create(
        Uri endpoint,
        ClientCredential credential,
        HttpMessageInvoker transport)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(transport);

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The secret-store endpoint must use HTTP or HTTPS.", nameof(endpoint));
        }

        return new HttpSecretStoreClient(endpoint, credential, transport);
    }
}
