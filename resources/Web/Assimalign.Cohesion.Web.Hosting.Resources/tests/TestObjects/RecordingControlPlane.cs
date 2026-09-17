using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

internal sealed class RecordingControlPlane : IResourceControlPlane
{
    private readonly IResourceControlPlane _inner = ResourceControlPlane.Create();
    internal List<CancellationToken> StopTokens { get; } = [];
    public IReadOnlyList<string> AcceptedCommandKinds => _inner.AcceptedCommandKinds;
    public IReadOnlyDictionary<string, Uri> ObservedEndpoints => _inner.ObservedEndpoints;
    public void AddHealthContributor(IHealthContributor contributor) => _inner.AddHealthContributor(contributor);
    public void ObserveEndpoint(string name, Uri address) => _inner.ObserveEndpoint(name, address);
    public ValueTask<ResourceHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default) => _inner.CheckHealthAsync(cancellationToken);
    public ValueTask<ResourceHealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default) => _inner.CheckReadinessAsync(cancellationToken);
    public ValueTask<ResourceHealthReport> CheckLivenessAsync(CancellationToken cancellationToken = default) => _inner.CheckLivenessAsync(cancellationToken);
    public void AttachHost(IHost host) => _inner.AttachHost(host);
    public ValueTask RequestStopAsync(CancellationToken cancellationToken = default)
    {
        StopTokens.Add(cancellationToken);
        return ValueTask.CompletedTask;
    }
    public ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) => _inner.ExecuteCommandAsync(command, cancellationToken);
}
