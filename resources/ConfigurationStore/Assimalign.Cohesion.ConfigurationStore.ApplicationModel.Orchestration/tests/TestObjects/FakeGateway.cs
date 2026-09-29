using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// A gateway that realizes nothing, so <see cref="IApplicationBuilder.Build"/> can run without a
/// platform.
/// </summary>
internal sealed class FakeGateway : IApplicationGateway
{
    public ResourceName Name { get; } = (ResourceName)"fake";

    public void Validate(IApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
    }

    public Task StartAsync(IApplicationModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReconcileAsync(IApplicationModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UninstallAsync(IApplicationModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
