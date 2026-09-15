using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Health.Internal;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// Convenience registration members for <see cref="IHealthChecksBuilder"/>.
/// </summary>
public static class HealthChecksBuilderExtensions
{
    extension(IHealthChecksBuilder builder)
    {
        /// <summary>
        /// Registers a health check instance.
        /// </summary>
        /// <param name="name">The unique name of the check.</param>
        /// <param name="check">The check to run.</param>
        /// <param name="failureStatus">The status reported when the check throws or times out.</param>
        /// <param name="tags">Optional tags for readiness/liveness filtering.</param>
        /// <param name="timeout">The per-check timeout, or <see langword="null"/> for none.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="check"/> is <see langword="null"/>.</exception>
        public IHealthChecksBuilder AddCheck(
            string name,
            IHealthCheck check,
            HealthStatus failureStatus = HealthStatus.Unhealthy,
            IEnumerable<string>? tags = null,
            TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(check);

            return builder.Add(new HealthCheckRegistration(name, check, failureStatus, tags, timeout));
        }

        /// <summary>
        /// Registers a check produced by a factory. Use this to defer construction or resolve the
        /// check from a captured dependency at evaluation time.
        /// </summary>
        /// <param name="name">The unique name of the check.</param>
        /// <param name="factory">A factory that produces the check to run.</param>
        /// <param name="failureStatus">The status reported when the check throws or times out.</param>
        /// <param name="tags">Optional tags for readiness/liveness filtering.</param>
        /// <param name="timeout">The per-check timeout, or <see langword="null"/> for none.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="factory"/> is <see langword="null"/>.</exception>
        public IHealthChecksBuilder AddCheck(
            string name,
            Func<IHealthCheck> factory,
            HealthStatus failureStatus = HealthStatus.Unhealthy,
            IEnumerable<string>? tags = null,
            TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(factory);

            return builder.Add(new HealthCheckRegistration(name, factory, failureStatus, tags, timeout));
        }

        /// <summary>
        /// Registers an inline asynchronous probe.
        /// </summary>
        /// <param name="name">The unique name of the check.</param>
        /// <param name="probe">The probe delegate returning a <see cref="HealthCheckResult"/>.</param>
        /// <param name="failureStatus">The status reported when the probe throws or times out.</param>
        /// <param name="tags">Optional tags for readiness/liveness filtering.</param>
        /// <param name="timeout">The per-check timeout, or <see langword="null"/> for none.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="probe"/> is <see langword="null"/>.</exception>
        public IHealthChecksBuilder AddCheck(
            string name,
            Func<HealthCheckContext, CancellationToken, ValueTask<HealthCheckResult>> probe,
            HealthStatus failureStatus = HealthStatus.Unhealthy,
            IEnumerable<string>? tags = null,
            TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(probe);

            return builder.Add(new HealthCheckRegistration(
                name,
                () => new DelegateHealthCheck(probe),
                failureStatus,
                tags,
                timeout));
        }

        /// <summary>
        /// Registers an inline synchronous probe.
        /// </summary>
        /// <param name="name">The unique name of the check.</param>
        /// <param name="probe">The probe delegate returning a <see cref="HealthCheckResult"/>.</param>
        /// <param name="failureStatus">The status reported when the probe throws or times out.</param>
        /// <param name="tags">Optional tags for readiness/liveness filtering.</param>
        /// <param name="timeout">The per-check timeout, or <see langword="null"/> for none.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="probe"/> is <see langword="null"/>.</exception>
        public IHealthChecksBuilder AddCheck(
            string name,
            Func<HealthCheckResult> probe,
            HealthStatus failureStatus = HealthStatus.Unhealthy,
            IEnumerable<string>? tags = null,
            TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(probe);

            return builder.Add(new HealthCheckRegistration(
                name,
                () => new DelegateHealthCheck((_, _) => new ValueTask<HealthCheckResult>(probe())),
                failureStatus,
                tags,
                timeout));
        }
    }
}
