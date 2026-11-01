using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Runs a gateway's resources inside the gateway's own process.
/// </summary>
public static class InProcessApplicationGatewayBuilderExtensions
{
    extension(ApplicationGatewayBuilder builder)
    {
        /// <summary>
        /// Runs the gateway's resources on the in-process infrastructure, which is also the channel
        /// its programs publish through and the client the gateway calls their control planes with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The gateway program owns the infrastructure: dispose it once the gateway has stopped, to
        /// stop the programs it runs.
        /// </para>
        /// <para>
        /// Each program opts into the platform too, with <c>UseInProcess()</c> on its resource's
        /// options from the in-process hosting package; a program that does not fails to build its
        /// resource under the gateway.
        /// </para>
        /// </remarks>
        /// <param name="infrastructure">The infrastructure, with a program bound to every resource the gateway deploys.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The gateway is built.</exception>
        public ApplicationGatewayBuilder UseInProcess(InProcessInfrastructure infrastructure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(infrastructure);

            return builder.UseInfrastructure(infrastructure).UseInstanceClient(infrastructure);
        }
    }
}
