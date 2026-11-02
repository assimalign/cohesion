using System;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;

/// <summary>
/// Opts a resource of any area into the in-process platform.
/// </summary>
public static class InProcessApplicationInstanceOptionsExtensions
{
    extension<TOptions>(TOptions options) where TOptions : ApplicationInstanceOptions
    {
        /// <summary>
        /// Opts the resource into the in-process platform: when a gateway runs the instance inside
        /// the gateway's own process, the instance publishes to that gateway and the gateway calls the
        /// instance's control plane directly, with no listener.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The client reads the gateway's endpoint from the instance's environment
        /// (<see cref="AppEnvironment.Variables.ManifestEndpoint"/>) now, through
        /// <see cref="AppEnvironment"/>, so call it while the resource's options are configured. The
        /// gateway's in-process platform sets the variable, and the platform name its instances
        /// select the client by, for every program it runs.
        /// </para>
        /// <para>
        /// On any other platform the client is not used: a resource that runs both in process and
        /// elsewhere opts into each platform, and an instance uses the client of the one its gateway
        /// names.
        /// </para>
        /// </remarks>
        /// <returns>The same options, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The resource already uses an in-process client.</exception>
        public TOptions UseInProcess()
        {
            ArgumentNullException.ThrowIfNull(options);

            options.UseGatewayClient(new InProcessGatewayClient(AppEnvironment.GetValue(AppEnvironment.Variables.ManifestEndpoint)));
            return options;
        }
    }
}
