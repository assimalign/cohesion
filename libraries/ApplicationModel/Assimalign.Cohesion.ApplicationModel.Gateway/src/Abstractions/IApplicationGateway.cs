using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Runs one gateway's application: deploys what its model declares onto an infrastructure provider,
/// joins its parent gateway when it has one, and manages its resources while it runs.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IApplicationGatewayBuilder"/> builds one with its model. A gateway runs once:
/// <see cref="StartAsync"/> starts the run and completes when startup completes, and
/// <see cref="StopAsync"/> ends it. <see cref="Completion"/> reports how the run ended, including a
/// failure after startup, such as a later run of the gateway taking its record over.
/// </para>
/// <para>
/// A program that only runs its gateway calls <c>RunAsync</c>
/// (<see cref="ApplicationGatewayExtensions"/>), which starts the gateway, waits for its run to end,
/// and stops it on cancellation. The gateway stops by itself on a shutdown signal, such as Ctrl+C or
/// a platform's SIGTERM.
/// </para>
/// </remarks>
public interface IApplicationGateway
{
    /// <summary>
    /// Gets what the gateway hands its platform: the model it runs and the publisher its instances
    /// publish to.
    /// </summary>
    IApplicationGatewayContext Context { get; }

    /// <summary>
    /// Gets the deployments of the resources the gateway runs, in dependency order, each after the
    /// resources it depends on; empty before <see cref="StartAsync"/>.
    /// </summary>
    IReadOnlyCollection<IApplicationGatewayDeployment> Resources { get; }

    /// <summary>
    /// Gets a task that completes when the gateway's run ends: successfully when it was stopped,
    /// cancelled, or signalled, and faulted with the run's failure otherwise.
    /// </summary>
    /// <remarks>
    /// It faults with what made the run fail: a startup failure, which <see cref="StartAsync"/> throws
    /// too; a later run of the gateway taking its record over
    /// (<see cref="ApplicationGatewayErrorCode.RecordSuperseded"/>); a failure of the loops that keep
    /// the run's registrations; or a failure of the provider's channel to stop. Before
    /// <see cref="StartAsync"/> it has not completed.
    /// </remarks>
    Task Completion { get; }

    /// <summary>
    /// Starts the gateway's run: loads its record, starts its provider's channel, joins its parent,
    /// deploys its model, and retires what the model no longer declares.
    /// </summary>
    /// <param name="cancellationToken">Cancels startup, which stops the run.</param>
    /// <returns>
    /// A task that completes when startup completes: every created or updated resource has published
    /// and the resources the model no longer declares are torn down. It faults with the startup's
    /// failure, as <see cref="Completion"/> does, and is cancelled when the run stops before startup
    /// completes.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">The gateway has already run.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the gateway's run; what the gateway deployed keeps running.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the run to end; the run still stops.</param>
    /// <returns>
    /// A task that completes when the run has ended. It does not report the run's failure, which
    /// <see cref="Completion"/> carries. A gateway that never started has nothing to stop.
    /// </returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
