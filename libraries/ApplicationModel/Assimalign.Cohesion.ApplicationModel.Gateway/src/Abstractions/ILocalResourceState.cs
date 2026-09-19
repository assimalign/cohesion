using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Persists endpoint allocations and protected runtime files for resources hosted on the local machine.</summary>
/// <remarks>Instances serialize their own port allocation operations; they do not coordinate independent gateway processes.</remarks>
public interface ILocalResourceState
{
    /// <summary>Resolves stable loopback endpoints and writes their runtime environment values.</summary>
    /// <param name="application">The owning application.</param>
    /// <param name="resource">The resource receiving the endpoints.</param>
    /// <param name="endpoints">The declared endpoint names and schemes.</param>
    /// <param name="environment">The mutable environment containing optional endpoint overrides.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The resolved endpoint addresses, in declaration order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> or <paramref name="environment"/> is null.</exception>
    /// <exception cref="ArgumentException">An endpoint scheme or host is invalid.</exception>
    /// <exception cref="InvalidDataException">A name, override, or persisted allocation is invalid or conflicting.</exception>
    /// <exception cref="IOException">State cannot be read or written, or no free port can be allocated.</exception>
    /// <exception cref="UnauthorizedAccessException">The allocation state cannot be accessed with the current permissions.</exception>
    /// <exception cref="JsonException">The persisted allocation document contains invalid JSON.</exception>
    /// <exception cref="SocketException">The operating system cannot bind a loopback socket for port allocation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task<IReadOnlyList<ResourceEndpoint>> ResolveEndpointsAsync(
        ApplicationName application,
        ResourceName resource,
        IReadOnlyList<ResourceEndpoint> endpoints,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a resource's persisted endpoint allocations without deleting its files.</summary>
    /// <param name="application">The owning application.</param>
    /// <param name="resource">The resource whose allocations are removed.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>A task that completes when the allocations have been removed.</returns>
    /// <exception cref="InvalidDataException">The application name or persisted state is invalid.</exception>
    /// <exception cref="IOException">The allocation state cannot be read or updated.</exception>
    /// <exception cref="UnauthorizedAccessException">The allocation state cannot be accessed with the current permissions.</exception>
    /// <exception cref="JsonException">The persisted allocation document contains invalid JSON.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task DeleteEndpointAllocationAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default);

    /// <summary>Writes protected trust and telemetry files and updates their runtime environment paths.</summary>
    /// <param name="application">The owning application.</param>
    /// <param name="resource">The resource receiving the files.</param>
    /// <param name="trustBundle">The transport trust bundle; empty content removes any existing carrier.</param>
    /// <param name="telemetryHeaders">The telemetry headers document; empty content removes any existing carrier.</param>
    /// <param name="environment">The mutable environment receiving the protected file paths.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>A task that completes after both carriers have been updated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    /// <exception cref="ArgumentException">An application or resource name is empty.</exception>
    /// <exception cref="InvalidDataException">A name escapes the configured state directory.</exception>
    /// <exception cref="IOException">A protected file cannot be written or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A runtime file or directory cannot be accessed with the current permissions.</exception>
    /// <exception cref="CryptographicException">The platform cannot protect the runtime file content.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task MaterializeRuntimeFilesAsync(
        ApplicationName application,
        ResourceName resource,
        ReadOnlyMemory<byte> trustBundle,
        ReadOnlyMemory<byte> telemetryHeaders,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken = default);
}
