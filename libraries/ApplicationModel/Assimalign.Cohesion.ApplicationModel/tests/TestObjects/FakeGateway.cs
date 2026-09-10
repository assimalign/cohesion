using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// An <see cref="IApplicationGateway"/> that records its lifecycle calls so tests can assert
/// start/stop ordering without a real platform.
/// </summary>
internal class FakeGateway : IApplicationGateway
{
    public FakeGateway(string name = "fake")
    {
        Name = name;
    }

    public ResourceName Name { get; }

    public List<string> Calls { get; } = new();

    public IApplicationModel? StartedModel { get; private set; }

    public IApplicationModel? ValidatedModel { get; private set; }

    public bool? StopTokenCanBeCanceled { get; private set; }

    public Func<CancellationToken, Task>? StartBehavior { get; init; }

    public void Validate(IApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidatedModel = model;
    }

    public Task StartAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        Calls.Add("start");
        StartedModel = model;
        return StartBehavior?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("stop");
        StopTokenCanBeCanceled = cancellationToken.CanBeCanceled;
        return Task.CompletedTask;
    }

    public Task ReconcileAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        Calls.Add("reconcile");
        StartedModel = model;
        return Task.CompletedTask;
    }

    public Task UninstallAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        Calls.Add("uninstall");
        StartedModel = model;
        return Task.CompletedTask;
    }
}
