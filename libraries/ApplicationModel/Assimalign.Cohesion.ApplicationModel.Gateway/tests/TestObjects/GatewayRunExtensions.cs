using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Runs a gateway as <c>RunAsync</c> does, and hands the test its startup as well.
/// </summary>
internal static class GatewayRunExtensions
{
    extension(IApplicationGateway gateway)
    {
        /// <summary>
        /// Starts the gateway, stops it when the token is cancelled, and returns its whole run.
        /// </summary>
        /// <param name="cancellationToken">Stops the gateway.</param>
        /// <param name="started">The gateway's startup, as <see cref="IApplicationGateway.StartAsync"/> returned it.</param>
        /// <returns>The run: it completes as <see cref="IApplicationGateway.Completion"/> does.</returns>
        public Task RunAsync(CancellationToken cancellationToken, out Task started)
        {
            started = gateway.StartAsync(cancellationToken);
            return WaitAsync(gateway, started, cancellationToken);
        }
    }

    private static async Task WaitAsync(IApplicationGateway gateway, Task started, CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(static state => _ = ((IApplicationGateway)state!).StopAsync(CancellationToken.None), gateway))
        {
            await started.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await gateway.Completion;
        }
    }
}
