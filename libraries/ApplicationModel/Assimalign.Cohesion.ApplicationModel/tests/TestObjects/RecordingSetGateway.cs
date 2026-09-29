using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// A multi-model gateway that records the member models an application set hands it, so tests can
/// assert what each member carries without a real platform.
/// </summary>
internal sealed class RecordingSetGateway : IMultiModelApplicationGateway
{
    public RecordingSetGateway(string name = "set-gateway")
    {
        Name = name;
    }

    public ResourceName Name { get; }

    public List<string> Calls { get; } = new();

    public IReadOnlyList<IApplicationModel>? ValidatedModels { get; private set; }

    public IReadOnlyList<IApplicationModel>? ReconciledModels { get; private set; }

    public void Validate(IApplicationModel model) => Calls.Add("validate");

    public void Validate(IReadOnlyList<IApplicationModel> models)
    {
        Calls.Add("validate-batch");
        ValidatedModels = models;
    }

    public Task StartAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        Calls.Add("start");
        return Task.CompletedTask;
    }

    public Task StartAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default)
    {
        Calls.Add("start-batch");
        return Task.CompletedTask;
    }

    public Task ReconcileAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        Calls.Add("reconcile");
        return Task.CompletedTask;
    }

    public Task ReconcileAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default)
    {
        Calls.Add("reconcile-batch");
        ReconciledModels = models;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("stop");
        return Task.CompletedTask;
    }

    public Task UninstallAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        Calls.Add("uninstall");
        return Task.CompletedTask;
    }

    public Task UninstallAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default)
    {
        Calls.Add("uninstall-batch");
        return Task.CompletedTask;
    }
}
