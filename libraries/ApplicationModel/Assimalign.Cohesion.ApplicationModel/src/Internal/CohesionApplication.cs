using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplication"/>. <see cref="RunAsync"/> starts the gateway, blocks
/// until cancellation, then stops the gateway within a bounded shutdown window — mirroring the
/// <c>Host&lt;TContext&gt;.RunAsync</c> pattern (a linked token source plus a task-completion
/// source completed on cancellation).
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

    public async Task RunAsync(CancellationToken cancellationToken = default)
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
