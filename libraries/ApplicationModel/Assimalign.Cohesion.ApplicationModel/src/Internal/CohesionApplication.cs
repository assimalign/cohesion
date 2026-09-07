using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplication"/>. It dispatches the model's run mode; ordinary Run
/// starts the gateway, blocks until cancellation, then stops supervision within a bounded
/// shutdown window, while Describe writes the model without gateway contact.
/// </summary>
internal sealed class CohesionApplication : IApplication
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(30);

    private readonly IApplicationGateway _gateway;
    private readonly TimeSpan _shutdownTimeout;

    public CohesionApplication(IApplicationModel model, IApplicationGateway gateway, TimeSpan? shutdownTimeout = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _shutdownTimeout = shutdownTimeout ?? DefaultShutdownTimeout;
    }

    public IApplicationModel Model { get; }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return Model.RunMode switch
        {
            GatewayRunMode.Run => RunCoreAsync(cancellationToken),
            GatewayRunMode.Describe => ApplicationModelDocumentWriter.WriteAsync(Model, cancellationToken),
            _ => throw new NotSupportedException(
                $"Gateway run mode '{Model.RunMode}' is not implemented until its platform execution/compiler support is available. No gateway operation was attempted."),
        };
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration registration = cancellation.Token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            stopped);

        await _gateway.StartAsync(Model, cancellation.Token).ConfigureAwait(false);

        await stopped.Task.ConfigureAwait(false);

        using var shutdown = new CancellationTokenSource(_shutdownTimeout);
        await _gateway.StopAsync(shutdown.Token).ConfigureAwait(false);
    }
}
