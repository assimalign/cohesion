using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Defines the Core-only surface every enabled resource exposes to its gateway.
/// </summary>
public interface IResourceControlPlane
{
    /// <summary>Gets the area-defined command kinds accepted by this control plane.</summary>
    IReadOnlyList<string> AcceptedCommandKinds { get; }

    /// <summary>Gets the latest observed endpoint snapshot keyed by endpoint name.</summary>
    IReadOnlyDictionary<string, EndpointAddress> ObservedEndpoints { get; }

    /// <summary>Adds a contributor to health, readiness, and liveness aggregation.</summary>
    /// <param name="contributor">The contributor to add.</param>
    void AddHealthContributor(IHealthContributor contributor);

    /// <summary>Records the realized address of a resource endpoint.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="address">The realized endpoint address.</param>
    void ObserveEndpoint(string name, EndpointAddress address);

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
    ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default);
}
