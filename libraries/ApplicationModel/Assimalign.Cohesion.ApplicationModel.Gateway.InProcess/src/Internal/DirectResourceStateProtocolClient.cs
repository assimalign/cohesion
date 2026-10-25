using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Runtime = Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

[System.Diagnostics.DebuggerDisplay("DirectResourceStateProtocolClient")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class DirectResourceStateProtocolClient : IResourceStateProtocolClient
{
    private readonly Func<ApplicationName, ResourceName, Runtime.IResourceControlPlane?> _resolve;

    public DirectResourceStateProtocolClient(Func<ApplicationName, ResourceName, Runtime.IResourceControlPlane?> resolve)
    {
        _resolve = resolve;
    }

    public ValueTask<ResourceRuntimeDescription> DescribeAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        Runtime.IResourceControlPlane plane = Resolve(application, resource);
        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument manifest = JsonDocument.Parse(plane.RuntimeManifest!.Export());
        return ValueTask.FromResult(new ResourceRuntimeDescription(plane.ProcessStartId, manifest.RootElement.Clone()));
    }

    public async ValueTask<ResourceRuntimeStateObservation> ReadAsync(ResourceManagementTarget target, ResourceStateAddress address, string contractDigest, CancellationToken cancellationToken = default)
    {
        Runtime.IResourceControlPlane plane = Resolve(address.Application, address.Resource);
        if (!Matches(plane, address, contractDigest)) { throw new InvalidOperationException(ErrorMessages.RuntimeStateContractMismatch); }
        Runtime.ResourceStateObservation observation = await plane.ReadStateAsync(address.State.Value, address.Key.Value, cancellationToken).ConfigureAwait(false);
        return new ResourceRuntimeStateObservation(new ResourceDesiredValue(observation.Value.Exists, observation.Value.Value), observation.Revision, observation.ValueDigest, observation.ProcessStartId);
    }

    public async ValueTask<ResourceStateDeliveryResult> ApplyAsync(ResourceManagementTarget target, ResourceStateDelivery delivery, CancellationToken cancellationToken = default)
    {
        Runtime.IResourceControlPlane plane = Resolve(delivery.Address.Application, delivery.Address.Resource);
        if (!Matches(plane, delivery.Address, delivery.ContractDigest)) { return new ResourceStateDeliveryResult(0, "contract-mismatch"); }
        var context = new Runtime.ResourceStateApplyContext(delivery.Incarnation, delivery.ProcessStartId, delivery.Revision, delivery.ValueDigest, delivery.Fence);
        Runtime.ResourceStateApplyResult result = await plane.ApplyStateAsync(context, delivery.Address.State.Value, delivery.Address.Key.Value,
            new Runtime.ResourceStateValue(delivery.Desired.Exists, delivery.Desired.Value), cancellationToken).ConfigureAwait(false);
        return new ResourceStateDeliveryResult(result.Revision, result.Status is Runtime.ResourceStateApplyStatus.Applied or Runtime.ResourceStateApplyStatus.AlreadyCurrent ? null : result.ErrorCode ?? "state-rejected");
    }

    public ValueTask<bool> AdmitAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource, Guid processStartId, IReadOnlyList<ResourceBootstrapState> states, CancellationToken cancellationToken = default)
    {
        Runtime.IResourceControlPlane plane = Resolve(application, resource);
        return
            plane.AdmitStateAsync(new Runtime.ResourceStateAdmission(processStartId,
                states.Select(state => new Runtime.ResourceStateAdmissionSlot(state.Name.Value, state.Key.Value, state.Revision, state.ValueDigest)).ToArray()), cancellationToken);
    }

    private Runtime.IResourceControlPlane Resolve(ApplicationName application, ResourceName resource) =>
        _resolve(application, resource) is { RuntimeManifest: not null } plane ? plane : throw new IOException(ErrorMessages.RuntimeStateUnavailable);
    private static bool Matches(Runtime.IResourceControlPlane plane, ResourceStateAddress address, string digest) =>
        plane.RuntimeManifest?.States.Any(state => state.Name.Value == address.State.Value && state.ContractDigest == digest) is true;

    private sealed class DebugView
    {
        public DebugView(DirectResourceStateProtocolClient value) { }
    }
}
