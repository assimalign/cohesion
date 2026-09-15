using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Web.Health;
using WebHealthStatus = Assimalign.Cohesion.Web.Health.HealthStatus;

namespace Assimalign.Cohesion.Web.Hosting.Health;

/// <summary>
/// Adapts transport-neutral host health contributors to Web health checks.
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
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="timeout"/> is neither positive nor
        /// <see cref="HealthCheckRegistration.InfiniteTimeout"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a check with the contributor's name is already registered.
        /// </exception>
        public IHealthChecksBuilder AddContributor(
            IHealthContributor contributor,
            WebHealthStatus failureStatus = WebHealthStatus.Unhealthy,
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
    }

    private static async ValueTask<HealthCheckResult> CheckContributorAsync(
        IHealthContributor contributor,
        CancellationToken cancellationToken)
    {
        HealthContribution contribution =
            await contributor.CheckAsync(cancellationToken).ConfigureAwait(false);

        return contribution.Status switch
        {
            Assimalign.Cohesion.Hosting.Health.HealthStatus.Healthy =>
                HealthCheckResult.Healthy(contribution.Description, contribution.Data),
            Assimalign.Cohesion.Hosting.Health.HealthStatus.Degraded =>
                HealthCheckResult.Degraded(contribution.Description, data: contribution.Data),
            Assimalign.Cohesion.Hosting.Health.HealthStatus.Unhealthy =>
                HealthCheckResult.Unhealthy(contribution.Description, data: contribution.Data),
            _ => throw new ArgumentOutOfRangeException(
                nameof(contribution),
                contribution.Status,
                "The contributor returned an unsupported health status.")
        };
    }
}
