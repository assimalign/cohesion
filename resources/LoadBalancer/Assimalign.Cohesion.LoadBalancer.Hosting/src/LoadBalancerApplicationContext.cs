using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.LoadBalancer.Hosting;

internal sealed class LoadBalancerApplicationContext : HostContext, IHealthContributor
{
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;

    internal LoadBalancerApplicationContext(ResourceContext? resourceContext = null)
    {
        _environment = new HostEnvironment(resourceContext?.EnvironmentName ?? "production")
        {
            ContentRootPath = resourceContext is null ? (FileSystemPath?)null : FileSystemPath.Parse(resourceContext.ContentRootPath),
        };
    }

    public string Name => "LoadBalancer";

    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(State is HostState.Failed
            ? HealthContribution.Unhealthy("The LoadBalancer host failed.")
            : HealthContribution.Healthy());
    }

    public override IHostEnvironment Environment => _environment;

    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);
        _hostedServices = hostedServices;
    }
}
