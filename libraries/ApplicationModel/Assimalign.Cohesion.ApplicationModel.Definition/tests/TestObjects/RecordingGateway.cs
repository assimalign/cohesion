using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

/// <summary>
/// A gateway that records the model it was asked to run and returns at once.
/// </summary>
internal sealed class RecordingGateway : IApplicationGateway
{
    public IApplicationModel? Model { get; private set; }

    public int Runs { get; private set; }

    public Task RunAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        Model = model;
        Runs++;
        return Task.CompletedTask;
    }
}
