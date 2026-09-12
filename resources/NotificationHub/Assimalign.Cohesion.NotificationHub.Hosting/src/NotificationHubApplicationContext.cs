using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

internal sealed class NotificationHubApplicationContext : HostContext, IHealthContributor
{
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;

    internal NotificationHubApplicationContext(ResourceContext? resourceContext = null)
    {
        _environment = new HostEnvironment(resourceContext?.EnvironmentName ?? "production")
        {
            ContentRootPath = resourceContext is null ? (FileSystemPath?)null : FileSystemPath.Parse(resourceContext.ContentRootPath),
        };
    }

    public string Name => "NotificationHub";

    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(State is HostState.Failed
            ? HealthContribution.Unhealthy("The NotificationHub host failed.")
            : HealthContribution.Healthy());
    }

    public override IHostEnvironment Environment => _environment;

    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    internal void SetHostedServices(IHostService[] hostedServices)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);

        _hostedServices = Array.AsReadOnly(hostedServices);
    }
}
