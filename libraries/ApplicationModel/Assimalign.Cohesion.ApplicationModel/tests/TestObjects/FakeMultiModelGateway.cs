using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

internal sealed class FakeMultiModelGateway(string name) : FakeGateway(name), IMultiModelApplicationGateway
{
    public void Validate(IReadOnlyList<IApplicationModel> models) { }

    public Task StartAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReconcileAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UninstallAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
