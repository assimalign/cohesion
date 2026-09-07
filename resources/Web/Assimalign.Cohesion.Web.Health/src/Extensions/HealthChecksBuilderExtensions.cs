using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web.Health.Internal;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// Convenience registration members for <see cref="IHealthChecksBuilder"/>.
/// </summary>
public static class HealthChecksBuilderExtensions
{
    private static readonly string[] DefaultContributorTags =
    [
        HealthTags.Ready,
        HealthTags.Live
    ];

    extension(IHealthChecksBuilder builder)
    {
        /// <summary>
        /// Registers a transport-neutral Cohesion host health contributor as an HTTP health check.
        /// </summary>
        /// <param name="contributor">The contributor to evaluate.</param>
        /// <param name="failureStatus">The status reported when the contributor throws or times out.</param>
        /// <param name="tags">
        /// Optional tags for endpoint filtering. When omitted, the contributor participates in both
        /// readiness and liveness checks. Pass an empty collection to include it only in aggregate checks.
        /// </param>
        /// <param name="timeout">The per-contributor timeout, or <see langword="null"/> for none.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="builder"/> or <paramref name="contributor"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <see cref="IHealthContributor.Name"/> is <see langword="null"/> or empty.
        /// </exception>
        public IHealthChecksBuilder AddContributor(
            IHealthContributor contributor,
            HealthStatus failureStatus = HealthStatus.Unhealthy,
            IEnumerable<string>? tags = null,
            TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(contributor);

            return builder.AddCheck(
                contributor.Name,
                (_, cancellationToken) => CheckContributorAsync(contributor, cancellationToken),
                failureStatus,
                tags ?? DefaultContributorTags,
                timeout);
        }

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

    private static async ValueTask<HealthCheckResult> CheckContributorAsync(
        IHealthContributor contributor,
        CancellationToken cancellationToken)
    {
        HealthContribution contribution =
            await contributor.CheckAsync(cancellationToken).ConfigureAwait(false);

        return contribution.Status switch
        {
            Assimalign.Cohesion.Hosting.HealthStatus.Healthy =>
                HealthCheckResult.Healthy(contribution.Description, contribution.Data),
            Assimalign.Cohesion.Hosting.HealthStatus.Degraded =>
                HealthCheckResult.Degraded(contribution.Description, data: contribution.Data),
            Assimalign.Cohesion.Hosting.HealthStatus.Unhealthy =>
                HealthCheckResult.Unhealthy(contribution.Description, data: contribution.Data),
            _ => throw new ArgumentOutOfRangeException(
                nameof(contribution),
                contribution.Status,
                "The contributor returned an unsupported health status.")
        };
    }
}
