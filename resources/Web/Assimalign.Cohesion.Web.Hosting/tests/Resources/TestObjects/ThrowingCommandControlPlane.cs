using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

using Assimalign.Cohesion.Web.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal sealed class ThrowingCommandControlPlane : IResourceControlPlane
{
    private readonly IResourceControlPlane _inner = ResourceControlPlane.Create();

    public IReadOnlyList<string> AcceptedCommandKinds => ["registered.kind"];
    public IReadOnlyDictionary<string, Uri> ObservedEndpoints => _inner.ObservedEndpoints;
    public void AddHealthContributor(IHealthContributor contributor) => _inner.AddHealthContributor(contributor);
    public void ObserveEndpoint(string name, Uri address) => _inner.ObserveEndpoint(name, address);
    public ValueTask<ResourceHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default) => _inner.CheckHealthAsync(cancellationToken);
    public ValueTask<ResourceHealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default) => _inner.CheckReadinessAsync(cancellationToken);
    public ValueTask<ResourceHealthReport> CheckLivenessAsync(CancellationToken cancellationToken = default) => _inner.CheckLivenessAsync(cancellationToken);
    public void AttachHost(IHost host) => _inner.AttachHost(host);
    public ValueTask RequestStopAsync(CancellationToken cancellationToken = default) => _inner.RequestStopAsync(cancellationToken);
    public ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        throw new ArgumentException("command-payload-secret command-key-secret command-owner-secret");
    public ValueTask<ReadOnlyMemory<byte>> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        throw new ArgumentException("command-payload-secret command-key-secret command-owner-secret");
}
