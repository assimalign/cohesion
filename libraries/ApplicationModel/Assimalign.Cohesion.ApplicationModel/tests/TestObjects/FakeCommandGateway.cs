using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// A fake gateway that records one-shot command dispatch while reusing the ordinary lifecycle fake.
/// </summary>
internal sealed class FakeCommandGateway : FakeGateway, IApplicationGatewayCommandHandler
{
    public IApplicationModel? ExecutedModel { get; private set; }

    public GatewayCommand? ExecutedCommand { get; private set; }

    public CancellationToken ExecutedCancellationToken { get; private set; }

    public Task ExecuteCommandAsync(
        IApplicationModel model,
        GatewayCommand command,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("command");
        ExecutedModel = model;
        ExecutedCommand = command;
        ExecutedCancellationToken = cancellationToken;
        return Task.CompletedTask;
    }
}
