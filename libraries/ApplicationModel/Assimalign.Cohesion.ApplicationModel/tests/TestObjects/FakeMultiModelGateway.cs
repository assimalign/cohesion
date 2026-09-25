using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

internal sealed class FakeMultiModelGateway : FakeGateway, IMultiModelApplicationGateway
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FakeMultiModelGateway"/> class.
    /// </summary>
    /// <param name="name">The gateway name passed to the base fake gateway.</param>
    public FakeMultiModelGateway(string name)
        : base(name)
    {
    }

    public void Validate(IReadOnlyList<IApplicationModel> models) { }

    public Task StartAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReconcileAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UninstallAsync(IReadOnlyList<IApplicationModel> models, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
