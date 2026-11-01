using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.SecretStore.Client;

/// <summary>
/// Resolves secret and certificate material from a secret-store endpoint.
/// </summary>
public interface ISecretStoreClient
{
    /// <summary>
    /// Gets the bytes stored at a secret path.
    /// </summary>
    /// <param name="path">The area-defined secret path.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>A task whose result contains the secret bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="HttpRequestException">The endpoint rejects the request or cannot be reached.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    Task<ReadOnlyMemory<byte>> GetSecretAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a PEM-encoded certificate from the secret store.
    /// </summary>
    /// <param name="name">The exact <c>ca/root</c> reference or <c>certs/</c> followed by a certificate leaf name.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>A task whose result contains the PEM-encoded certificate.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    /// <exception cref="HttpRequestException">The endpoint rejects the request or cannot be reached.</exception>
    /// <exception cref="InvalidDataException">The endpoint returns an empty certificate.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    Task<string> GetCertificateAsync(
        SecretStoreCertificateReference name,
        CancellationToken cancellationToken = default);
}
