using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Runs an <see cref="IApplicationGateway"/> from start to end in one call.
/// </summary>
public static class ApplicationGatewayExtensions
{
    extension(IApplicationGateway gateway)
    {
        /// <summary>
        /// Runs the gateway: starts it, waits for its run to end, and stops it when
        /// <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <remarks>
        /// The gateway also stops by itself on a shutdown signal. Cancellation and a signal end the run
        /// without an exception, whether startup completed or not; what the gateway deployed keeps
        /// running.
        /// </remarks>
        /// <param name="cancellationToken">Signals that the gateway should stop.</param>
        /// <returns>A task that completes when the run has ended.</returns>
        /// <exception cref="System.InvalidOperationException">The gateway has already run.</exception>
        /// <exception cref="ApplicationGatewayException">
        /// The run failed, as <see cref="IApplicationGateway.Completion"/> reports it, such as a
        /// resource that published no manifest within its startup window or a later run of the gateway
        /// taking its record over.
        /// </exception>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            Task started = gateway.StartAsync(cancellationToken);
            using (cancellationToken.Register(static state => _ = ((IApplicationGateway)state!).StopAsync(CancellationToken.None), gateway))
            {
                try
                {
                    await started.ConfigureAwait(false);
                }
                catch
                {
                    // Deliberately broad: a startup that failed or was cancelled ends the run, and
                    // Completion reports the run's outcome once it has ended, a failure included.
                }

                await gateway.Completion.ConfigureAwait(false);
            }
        }
    }
}
