using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalResourceStateStore : ILocalResourceState
{
    private readonly LocalPortStore _ports;
    private readonly LocalMountMaterializer _mounts;

    internal LocalResourceStateStore(string stateDirectory)
    {
        _ports = new LocalPortStore(stateDirectory);
        _mounts = new LocalMountMaterializer(stateDirectory);
    }

    public Task<IReadOnlyList<ResourceEndpoint>> ResolveEndpointsAsync(
        ApplicationName application,
        ResourceName resource,
        IReadOnlyList<ResourceEndpoint> endpoints,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(environment);
        return _ports.ResolveAsync(application, resource, endpoints, environment, cancellationToken);
    }

    public Task DeleteEndpointAllocationAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default) =>
        _ports.DeleteAsync(application, resource, cancellationToken);

    public async Task MaterializeRuntimeFilesAsync(
        ApplicationName application,
        ResourceName resource,
        ReadOnlyMemory<byte> trustBundle,
        ReadOnlyMemory<byte> telemetryHeaders,
        IDictionary<string, string> environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        await _mounts.MaterializeTrustBundleAsync(application, resource, trustBundle, environment, cancellationToken).ConfigureAwait(false);
        await _mounts.MaterializeTelemetryHeadersAsync(application, resource, telemetryHeaders, environment, cancellationToken).ConfigureAwait(false);
    }
}
