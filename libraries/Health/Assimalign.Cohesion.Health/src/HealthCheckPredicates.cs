using System;

namespace Assimalign.Cohesion.Health;

/// <summary>
/// Ready-made registration predicates for the common readiness/liveness slices, plus a helper
/// for building tag filters. Pass one of these to
/// <see cref="IHealthCheckService.CheckHealthAsync"/> or a health endpoint's options.
/// </summary>
public static class HealthCheckPredicates
{
    /// <summary>
    /// Gets a predicate matching checks tagged <see cref="HealthTags.Ready"/>.
    /// </summary>
    public static Func<HealthCheckRegistration, bool> Ready { get; } =
        registration => registration.HasTag(HealthTags.Ready);

    /// <summary>
    /// Gets a predicate matching checks tagged <see cref="HealthTags.Live"/>.
    /// </summary>
    public static Func<HealthCheckRegistration, bool> Live { get; } =
        registration => registration.HasTag(HealthTags.Live);

    /// <summary>
    /// Builds a predicate matching any registration carrying at least one of the supplied tags.
    /// An empty tag list matches every registration.
    /// </summary>
    /// <param name="tags">The tags to match (case-insensitive).</param>
    /// <returns>A predicate over <see cref="HealthCheckRegistration"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tags"/> is <see langword="null"/>.</exception>
    public static Func<HealthCheckRegistration, bool> WithAnyTag(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        if (tags.Length == 0)
        {
            return static _ => true;
        }

        string[] snapshot = (string[])tags.Clone();

        return registration =>
        {
            foreach (string tag in snapshot)
            {
                if (registration.HasTag(tag))
                {
                    return true;
                }
            }

            return false;
        };
    }
}
