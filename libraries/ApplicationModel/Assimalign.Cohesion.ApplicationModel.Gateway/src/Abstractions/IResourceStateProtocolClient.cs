using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Speaks a versioned resource state protocol using gateway-owned wire contracts.</summary>
public interface IResourceStateProtocolClient
{
    /// <summary>Reads the authenticated runtime catalog and process incarnation.</summary>
    /// <param name="target">The trusted platform target.</param>
    /// <param name="application">The application identity.</param>
    /// <param name="resource">The logical resource identity.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The process identity and catalog.</returns>
    ValueTask<ResourceRuntimeDescription> DescribeAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default);
    /// <summary>Releases traffic admission only after the process verifies required state.</summary>
    /// <param name="target">The trusted platform target.</param>
    /// <param name="application">The application identity.</param>
    /// <param name="resource">The logical resource.</param>
    /// <param name="processStartId">The verified process incarnation.</param>
    /// <param name="states">All currently required slot revisions.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>Whether actual runtime state permitted admission.</returns>
    ValueTask<bool> AdmitAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource,
        Guid processStartId, IReadOnlyList<ResourceBootstrapState> states, CancellationToken cancellationToken = default);
    /// <summary>Validates catalog compatibility and reads one process's actual state.</summary>
    /// <param name="target">The trusted platform target.</param>
    /// <param name="address">The logical slot.</param>
    /// <param name="contractDigest">The required state contract digest.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The process-bound state observation.</returns>
    ValueTask<ResourceRuntimeStateObservation> ReadAsync(ResourceManagementTarget target, ResourceStateAddress address,
        string contractDigest, CancellationToken cancellationToken = default);
    /// <summary>Applies one target-bound revision to actual resource state.</summary>
    /// <param name="target">The trusted platform target.</param>
    /// <param name="delivery">The revision and verified process identity.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The actual application result.</returns>
    ValueTask<ResourceStateDeliveryResult> ApplyAsync(ResourceManagementTarget target, ResourceStateDelivery delivery,
        CancellationToken cancellationToken = default);
}
