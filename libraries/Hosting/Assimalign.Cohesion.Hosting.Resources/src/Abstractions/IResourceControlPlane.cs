using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Defines the Core-only surface every enabled resource exposes to its gateway.
/// </summary>
public interface IResourceControlPlane
{
    /// <summary>Gets the area-defined command kinds accepted by this control plane.</summary>
    IReadOnlyList<string> AcceptedCommandKinds { get; }

    /// <summary>Gets accepted declarations, including their owners, without transport state.</summary>
    IReadOnlyList<ResourceCommand> Commands => Array.Empty<ResourceCommand>();

    /// <summary>Registers the runtime implementation of an advertised command kind.</summary>
    /// <param name="handler">The area-owned mutation handler.</param>
    /// <exception cref="ArgumentNullException">The handler is null.</exception>
    /// <exception cref="ArgumentException">The handler kind is not advertised.</exception>
    /// <exception cref="InvalidOperationException">A handler already exists for the kind.</exception>
    /// <exception cref="NotSupportedException">This control plane does not support registration.</exception>
    void RegisterCommandHandler(IResourceCommandHandler handler) =>
        throw new NotSupportedException("This resource control plane does not support command handlers.");

    /// <summary>Gets the latest observed endpoint snapshot keyed by endpoint name.</summary>
    IReadOnlyDictionary<string, Uri> ObservedEndpoints { get; }

    /// <summary>Adds a contributor to health, readiness, and liveness aggregation.</summary>
    /// <param name="contributor">The contributor to add.</param>
    void AddHealthContributor(IHealthContributor contributor);

    /// <summary>Records the realized address of a resource endpoint.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="address">The realized endpoint address.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="address"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, or <paramref name="address"/> is not an endpoint URI.
    /// </exception>
    void ObserveEndpoint(string name, Uri address);

    /// <summary>Checks the aggregate resource health.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate health report.</returns>
    ValueTask<ResourceHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks whether the resource is ready to receive work.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate readiness report.</returns>
    ValueTask<ResourceHealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks whether the resource process remains live.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate liveness report.</returns>
    ValueTask<ResourceHealthReport> CheckLivenessAsync(CancellationToken cancellationToken = default);

    /// <summary>Attaches the built resource host used for graceful stop requests.</summary>
    /// <param name="host">The resource host.</param>
    void AttachHost(IHost host);

    /// <summary>Requests a graceful stop from the attached resource host.</summary>
    /// <param name="cancellationToken">Cancels the request before it is accepted.</param>
    /// <returns>A value task that completes when the request has been accepted.</returns>
    ValueTask RequestStopAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes an area-defined declarative command.</summary>
    /// <param name="command">The command envelope.</param>
    /// <param name="cancellationToken">Cancels command execution.</param>
    /// <returns>The area-defined response payload.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="ArgumentException">An envelope identity field is blank.</exception>
    /// <exception cref="NotSupportedException">The command kind has no registered runtime handler.</exception>
    /// <exception cref="ResourceCommandRejectedException">Ownership or the runtime refuses the command.</exception>
    ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a previously applied declaration belonging to the supplied owner.</summary>
    /// <param name="command">The declaration identity to delete.</param>
    /// <param name="cancellationToken">Cancels deletion.</param>
    /// <returns>The area-defined response bytes.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="ArgumentException">An envelope identity field is blank.</exception>
    /// <exception cref="NotSupportedException">The command kind or deletion is unsupported.</exception>
    /// <exception cref="ResourceCommandRejectedException">Ownership or the runtime refuses deletion.</exception>
    ValueTask<ReadOnlyMemory<byte>> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Resource command kind '{command.Kind}' does not support deletion.");
}
