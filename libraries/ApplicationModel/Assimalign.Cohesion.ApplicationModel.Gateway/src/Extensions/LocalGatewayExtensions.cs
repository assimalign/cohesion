using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Extensions for selecting the <see cref="LocalGateway"/> on an <see cref="IApplicationBuilder"/>.
/// </summary>
public static class LocalGatewayExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Selects a <see cref="LocalGateway"/> with default options.
        /// </summary>
        /// <returns>The builder, for chaining.</returns>
        public IApplicationBuilder UseLocalGateway()
            => builder.UseGateway(new LocalGateway());

        /// <summary>
        /// Selects a <see cref="LocalGateway"/> configured by <paramref name="configure"/>.
        /// </summary>
        /// <param name="configure">Configures the local gateway options.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        public IApplicationBuilder UseLocalGateway(Action<LocalGatewayOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var options = new LocalGatewayOptions();
            configure(options);
            return builder.UseGateway(new LocalGateway(options));
        }
    }
}
