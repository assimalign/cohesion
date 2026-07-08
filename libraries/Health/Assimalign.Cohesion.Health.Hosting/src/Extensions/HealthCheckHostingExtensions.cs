using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Health.Hosting.Internal;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Health.Hosting;

/// <summary>
/// Builder-time dependency-injection members that compose the health-check set, register the
/// periodic publisher on the Hosting execution menu, and register health publishers.
/// </summary>
/// <remarks>
/// This is the only health-related DI/registration seam — the core
/// <c>Assimalign.Cohesion.Health</c> library is container-free. Call
/// <see cref="AddHealthChecks(IServiceProviderBuilder, Action{HealthCheckPublisherOptions})"/>
/// once during host build and chain <c>AddCheck</c> on the returned builder; the check set is
/// snapshotted the first time <see cref="IHealthCheckService"/> is resolved.
/// </remarks>
public static class HealthCheckHostingExtensions
{
    extension(IServiceProviderBuilder services)
    {
        /// <summary>
        /// Registers the health-check service (and the periodic publisher host service) and returns
        /// the builder used to add checks. Call once and chain <c>AddCheck</c> on the result.
        /// </summary>
        /// <param name="configurePublisher">An optional callback to configure the periodic publisher (interval, delay, timeout, predicate).</param>
        /// <returns>The <see cref="IHealthChecksBuilder"/> used to register checks.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
        public IHealthChecksBuilder AddHealthChecks(Action<HealthCheckPublisherOptions>? configurePublisher = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var publisherOptions = new HealthCheckPublisherOptions();
            configurePublisher?.Invoke(publisherOptions);

            IHealthChecksBuilder checksBuilder = HealthChecks.CreateBuilder();

            // The check set is captured lazily: Build() runs the first time IHealthCheckService is
            // resolved, so every AddCheck made before that (all builder-time) is included.
            services.AddSingleton<IHealthCheckService>(_ => checksBuilder.Build());
            services.AddSingleton(publisherOptions);
            services.AddSingleton<IHostService>(provider => new HealthCheckPublisherService(
                provider.GetRequiredService<IHealthCheckService>(),
                provider.GetServices<IHealthPublisher>(),
                provider.GetRequiredService<HealthCheckPublisherOptions>()));

            return checksBuilder;
        }

        /// <summary>
        /// Registers a health publisher instance. The publisher receives every periodic
        /// <see cref="HealthReport"/>.
        /// </summary>
        /// <param name="publisher">The publisher to register.</param>
        /// <returns>The same service builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="publisher"/> is <see langword="null"/>.</exception>
        public IServiceProviderBuilder AddHealthCheckPublisher(IHealthPublisher publisher)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(publisher);

            services.AddSingleton(publisher);
            return services;
        }

        /// <summary>
        /// Registers a health publisher resolved from the container. The publisher receives every
        /// periodic <see cref="HealthReport"/>.
        /// </summary>
        /// <typeparam name="TPublisher">The publisher implementation type.</typeparam>
        /// <returns>The same service builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
        public IServiceProviderBuilder AddHealthCheckPublisher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPublisher>()
            where TPublisher : class, IHealthPublisher
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddSingleton<IHealthPublisher, TPublisher>();
            return services;
        }
    }
}
