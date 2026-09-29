using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.SecretStore.Client;

/// <summary>
/// Resolves secret and certificate material from a secret-store endpoint.
/// </summary>
public interface ISecretStoreClient
{
    /// <summary>Applies a command and observes success or the provider's refusal.</summary>
    /// <param name="command">The command envelope.</param>
    /// <param name="cancellationToken">Cancels delivery and response reading.</param>
    /// <returns>The observed status and actionable detail; an empty successful response is Applied.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="HttpRequestException">The endpoint cannot be reached.</exception>
    /// <exception cref="OperationCanceledException">Delivery is canceled.</exception>
    /// <exception cref="JsonException">The endpoint returns malformed observation JSON.</exception>
    ValueTask<ResourceCommandObservation> ObserveCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default);

    /// <summary>Deletes an owned declaration and observes the provider's result.</summary>
    /// <param name="command">The previously applied declaration.</param>
    /// <param name="cancellationToken">Cancels delivery and response reading.</param>
    /// <returns>The observed status and actionable detail.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="HttpRequestException">The endpoint cannot be reached.</exception>
    /// <exception cref="OperationCanceledException">Delivery is canceled.</exception>
    /// <exception cref="JsonException">The endpoint returns malformed observation JSON.</exception>
    ValueTask<ResourceCommandObservation> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default);

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
    /// <param name="name">The area-defined certificate name.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>A task whose result contains the PEM-encoded certificate.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="HttpRequestException">The endpoint rejects the request or cannot be reached.</exception>
    /// <exception cref="InvalidDataException">The endpoint returns an empty certificate.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    Task<string> GetCertificateAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a generic command envelope to the secret-store control plane.
    /// </summary>
    /// <param name="command">The command envelope to send.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>A task that completes when the endpoint accepts the command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="HttpRequestException">The endpoint rejects the request or cannot be reached.</exception>
    /// <exception cref="JsonException">The command cannot be serialized.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    Task SendCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default);
}
