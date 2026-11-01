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
        /// Runs the gateway's resources on in-process infrastructure that the gateway builds and owns,
        /// which is also the channel its programs publish through and the client the gateway calls
        /// their control planes with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The gateway builds the infrastructure when it is built, with <paramref name="configure"/>
        /// applied, and disposing the gateway disposes it, which stops its programs. Without
        /// <paramref name="configure"/>, it runs every resource program the gateway program
        /// references with <c>CohesionResource</c> metadata, which the gateway program's build binds.
        /// </para>
        /// <para>
        /// Each program opts into the platform too, with <c>UseInProcess()</c> on its resource's
        /// options from the in-process hosting package; a program that does not fails to build its
        /// resource under the gateway.
        /// </para>
        /// <para>
        /// For the defaults, omit the argument: <c>UseInProcess()</c>. A literal <see langword="null"/>
        /// converts to the parameter of the overload that takes infrastructure too, so
        /// <c>UseInProcess(null)</c> is ambiguous and does not compile.
        /// </para>
        /// </remarks>
        /// <param name="configure">Configures the infrastructure's options and binds programs the build did not.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The gateway is built.</exception>
        public ApplicationGatewayBuilder UseInProcess(Action<InProcessInfrastructureBuilder>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.UsePlatform(() =>
            {
                var infrastructure = new InProcessInfrastructureBuilder();
                configure?.Invoke(infrastructure);
                return infrastructure.Build();
            });
        }

        /// <summary>
        /// Runs the gateway's resources on the in-process infrastructure, which is also the channel
        /// its programs publish through and the client the gateway calls their control planes with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The gateway borrows the infrastructure: dispose it once the gateway has stopped, to stop
        /// the programs it runs.
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
