using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplication"/>. It dispatches the model's run mode; ordinary Run
/// starts the gateway, blocks until cancellation, then lets the gateway apply its per-resource
/// shutdown budgets, while Describe writes the model without gateway contact.
/// </summary>
internal sealed class CohesionApplication : IApplication
{
    private readonly IApplicationGateway _gateway;
    private readonly TimeSpan? _shutdownTimeout;
    private readonly GatewayCommand? _command;

    public CohesionApplication(
        IApplicationModel model,
        IApplicationGateway gateway,
        TimeSpan? shutdownTimeout = null,
        GatewayCommand? command = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _shutdownTimeout = shutdownTimeout;
        _command = command;
    }

    public IApplicationModel Model { get; }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return Model.RunMode switch
        {
            GatewayRunMode.Run => RunCoreAsync(cancellationToken),
            GatewayRunMode.Apply => _gateway.ReconcileAsync(Model, cancellationToken),
            GatewayRunMode.Teardown => _gateway.UninstallAsync(Model, cancellationToken),
            GatewayRunMode.Describe => ApplicationModelDocumentWriter.WriteAsync(Model, cancellationToken),
            GatewayRunMode.TrustIssue or GatewayRunMode.TrustAdd => RunCommandAsync(cancellationToken),
            _ => throw new NotSupportedException(
                $"Gateway run mode '{Model.RunMode}' is not implemented until its platform execution/compiler support is available. No gateway operation was attempted."),
        };
    }

    private Task RunCommandAsync(CancellationToken cancellationToken)
    {
        if (_command is null)
        {
            throw new InvalidOperationException(
                $"Gateway run mode '{Model.RunMode}' has no validated command arguments.");
        }

        if (_gateway is not IApplicationGatewayCommandHandler handler)
        {
            throw new NotSupportedException(
                $"Gateway '{_gateway.Name}' does not implement command mode '{Model.RunMode}'.");
        }

        return handler.ExecuteCommandAsync(Model, _command, cancellationToken);
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

        if (_shutdownTimeout is TimeSpan shutdownTimeout)
        {
            using var shutdown = new CancellationTokenSource(shutdownTimeout);
            await _gateway.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        else
        {
            await _gateway.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
