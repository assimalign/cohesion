using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// An <see cref="IApplicationGateway"/> that records its lifecycle calls so tests can assert
/// start/stop ordering without a real platform.
/// </summary>
internal sealed class FakeGateway : IApplicationGateway
{
    public ResourceName Name => "fake";

    public List<string> Calls { get; } = new();

    public IApplicationModel? StartedModel { get; private set; }

    public Task StartAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        Calls.Add("start");
        StartedModel = model;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("stop");
        return Task.CompletedTask;
    }
}
