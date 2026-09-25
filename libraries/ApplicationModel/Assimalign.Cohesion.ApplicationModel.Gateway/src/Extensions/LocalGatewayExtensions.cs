using System;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Extensions for selecting the <see cref="LocalGateway"/> on an <see cref="IApplicationBuilder"/>.
/// </summary>
public static class LocalGatewayExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds a manifest-less executable that only the local gateway can realize.
        /// </summary>
        /// <param name="name">The resource name.</param>
        /// <param name="path">The apphost or native executable path.</param>
        /// <param name="configure">
        /// Configures the executable's probe or stdout ready marker, endpoints, environment,
        /// and restart policy.
        /// </param>
        /// <returns>The resource descriptor, for declaring dependencies.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="configure"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Neither a readiness probe nor a stdout ready marker was configured.
        /// </exception>
        public IApplicationResourceDescriptor AddExecutable(
            ResourceName name,
            string path,
            Action<IExecutableResourceOptionsBuilder> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new ExecutableResourceOptionsBuilder();
            configure(options);
            options.Validate();

            return builder.AddResource(new LocalExecutableResource(name, path, options));
        }

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

        /// <summary>
        /// Selects a <see cref="LocalGateway"/> and applies gateway-runtime arguments such as
        /// repeatable <c>--parameter name=value</c> bindings.
        /// </summary>
        /// <param name="args">The original gateway command-line arguments.</param>
        /// <param name="configure">Optional local gateway configuration.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">A gateway-runtime argument is malformed.</exception>
        public IApplicationBuilder UseLocalGateway(
            string[] args,
            Action<LocalGatewayOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(args);

            var options = new LocalGatewayOptions();
            configure?.Invoke(options);
            ApplicationGatewayCommandLine.Apply(options, args);
            return builder.UseGateway(new LocalGateway(options));
        }
    }
}
