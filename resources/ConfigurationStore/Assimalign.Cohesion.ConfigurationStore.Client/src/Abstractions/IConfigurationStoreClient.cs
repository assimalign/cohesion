using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ConfigurationStore.Client;

/// <summary>
/// Reads configuration namespaces and sends commands to a configuration-store endpoint.
/// </summary>
public interface IConfigurationStoreClient
{
    /// <summary>
    /// Lists the available configuration namespace names.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>A task whose result contains the available namespace names.</returns>
    /// <exception cref="HttpRequestException">The endpoint rejects the request or cannot be reached.</exception>
    /// <exception cref="JsonException">The endpoint returns an invalid namespace list.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    Task<IReadOnlyList<string>> ListNamespacesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the values in a configuration namespace.
    /// </summary>
    /// <param name="name">The area-defined configuration namespace name.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>
    /// A task whose result contains the namespace's configuration keys and nullable values.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="HttpRequestException">The endpoint rejects the request or cannot be reached.</exception>
    /// <exception cref="JsonException">The endpoint returns an invalid namespace document.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    Task<IReadOnlyDictionary<string, string?>> GetNamespaceAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a generic command envelope to the configuration-store control plane.
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
