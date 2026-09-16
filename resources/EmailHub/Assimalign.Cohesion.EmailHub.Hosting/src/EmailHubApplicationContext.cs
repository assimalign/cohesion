using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.EmailHub.Hosting;

/// <summary>
/// Provides the EmailHub application environment and runtime composition.
/// </summary>
public sealed class EmailHubApplicationContext : HostContext, IEmailHubApplicationContext, IHealthContributor
{
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;

    internal EmailHubApplicationContext(ResourceContext? resourceContext = null)
    {
        _environment = new HostEnvironment(resourceContext?.EnvironmentName ?? "production")
        {
            ContentRootPath = resourceContext is null ? (FileSystemPath?)null : FileSystemPath.Parse(resourceContext.ContentRootPath),
        };
    }

    /// <summary>
    /// Gets the name used for this application health contribution.
    /// </summary>
    public string Name => "EmailHub";

    /// <summary>
    /// Reports the health of the application.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the health check.</param>
    /// <returns>The current application health contribution.</returns>
    /// <exception cref="OperationCanceledException">The cancellation token is cancelled.</exception>
    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(State is HostState.Failed
            ? HealthContribution.Unhealthy("The EmailHub host failed.")
            : HealthContribution.Healthy());
    }

    /// <summary>
    /// Gets the configured application content root.
    /// </summary>
    public FileSystemPath? ContentRootPath => Environment.ContentRootPath;

    /// <summary>
    /// Gets the host environment for this application.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services in registration and startup order.
    /// </summary>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);
        _hostedServices = hostedServices;
    }
}
