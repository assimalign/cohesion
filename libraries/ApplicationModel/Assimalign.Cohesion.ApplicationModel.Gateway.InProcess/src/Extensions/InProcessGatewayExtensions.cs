using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>Extensions for selecting the in-process application gateway.</summary>
public static partial class InProcessGatewayExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Selects an <see cref="InProcessGateway"/> with default options.</summary>
        /// <returns>The builder, for chaining.</returns>
        public IApplicationBuilder UseInProcessGateway() =>
            builder.UseGateway(new InProcessGateway());

        /// <summary>Selects and configures an <see cref="InProcessGateway"/>.</summary>
        /// <param name="configure">Configures invocation, probe, restart, and state options.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="configure"/> is <see langword="null"/>.
        /// </exception>
        public IApplicationBuilder UseInProcessGateway(
            Action<InProcessGatewayOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var options = new InProcessGatewayOptions();
            configure(options);
            return builder.UseGateway(new InProcessGateway(options));
        }
    }
}
