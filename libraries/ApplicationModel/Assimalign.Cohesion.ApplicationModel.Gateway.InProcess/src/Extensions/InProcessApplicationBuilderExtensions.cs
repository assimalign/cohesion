using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Runs a gateway's resources inside the gateway's own process.
/// </summary>
public static class InProcessApplicationBuilderExtensions
{
    extension(ApplicationBuilder builder)
    {
        /// <summary>
        /// Runs the gateway's resources on the in-process infrastructure, which is also the transport
        /// its programs publish through.
        /// </summary>
        /// <remarks>
        /// The gateway program owns the infrastructure: dispose it once the gateway has stopped, to
        /// stop the programs it runs.
        /// </remarks>
        /// <param name="infrastructure">The infrastructure, with a program bound to every resource the gateway deploys.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The infrastructure is already the builder's transport.</exception>
        /// <exception cref="InvalidOperationException">The gateway is built.</exception>
        public ApplicationBuilder UseInProcess(InProcessInfrastructure infrastructure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(infrastructure);

            return builder.UseInfrastructure(infrastructure).UseTransport(infrastructure);
        }
    }
}
