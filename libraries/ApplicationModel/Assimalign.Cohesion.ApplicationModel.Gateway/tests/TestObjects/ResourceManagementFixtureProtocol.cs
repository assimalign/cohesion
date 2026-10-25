using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests.TestObjects;

internal sealed class ResourceManagementFixtureProtocol : IResourceStateProtocolClient
{
    private readonly Dictionary<string, ResourceRuntimeStateObservation> _state = new(StringComparer.Ordinal);
    public HashSet<string> Unavailable { get; } = new(StringComparer.Ordinal);
    public int ApplyCount { get; private set; }
    public long LastFence { get; private set; }
    public Action? OnApply { get; set; }
    public Action? OnDescribe { get; set; }
    public ConcurrentQueue<(string HostId, Guid ProcessStartId, IReadOnlyList<ResourceBootstrapState> States)> Admissions { get; } = new();
    public Func<ResourceManagementTarget, JsonElement>? Catalog { get; set; }
    public Dictionary<string, JsonElement> CatalogOverrides { get; } = new(StringComparer.Ordinal);

    public async ValueTask<ResourceRuntimeDescription> DescribeAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        OnDescribe?.Invoke();
        ResourceRuntimeStateObservation state = await ReadAsync(target, new ResourceStateAddress(application, resource, "web.cors", "configuration"), "test-contract", cancellationToken);
        using JsonDocument document = JsonDocument.Parse("{}");
        return new ResourceRuntimeDescription(state.ProcessStartId, CatalogOverrides.TryGetValue(target.HostId, out JsonElement changed)
            ? changed : Catalog?.Invoke(target) ?? document.RootElement.Clone());
    }

    public ValueTask<bool> AdmitAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource, Guid processStartId,
        IReadOnlyList<ResourceBootstrapState> states, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Admissions.Enqueue((target.HostId, processStartId, states.ToArray()));
        return ValueTask.FromResult(true);
    }

    public void Restart(string host) => _state.Remove(host);

    public ValueTask<ResourceRuntimeStateObservation> ReadAsync(ResourceManagementTarget target, ResourceStateAddress address, string contractDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable.Contains(target.HostId)) { throw new IOException("fixture unavailable"); }
        if (!_state.TryGetValue(target.HostId, out ResourceRuntimeStateObservation? value))
        {
            using JsonDocument document = JsonDocument.Parse("{}");
            value = new ResourceRuntimeStateObservation(new ResourceDesiredValue(true, document.RootElement.Clone()), 0, string.Empty, Guid.NewGuid());
            _state[target.HostId] = value;
        }
        return ValueTask.FromResult(value);
    }

    public ValueTask<ResourceStateDeliveryResult> ApplyAsync(ResourceManagementTarget target, ResourceStateDelivery delivery, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResourceRuntimeStateObservation previous = _state[target.HostId];
        if (previous.ProcessStartId != delivery.ProcessStartId || previous.Revision > delivery.Revision)
        {
            return ValueTask.FromResult(new ResourceStateDeliveryResult(previous.Revision, "stale-process-or-revision"));
        }
        ApplyCount++;
        LastFence = delivery.Fence;
        _state[target.HostId] = new ResourceRuntimeStateObservation(delivery.Desired, delivery.Revision, delivery.ValueDigest, delivery.ProcessStartId);
        OnApply?.Invoke();
        return ValueTask.FromResult(new ResourceStateDeliveryResult(delivery.Revision));
    }
}
