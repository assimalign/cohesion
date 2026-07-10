namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// Well-known health-check tags used to separate readiness from liveness.
/// </summary>
/// <remarks>
/// A Kubernetes <c>readinessProbe</c> should gate on checks tagged <see cref="Ready"/> (the
/// dependencies that must be up before traffic is routed), while a <c>livenessProbe</c> gates
/// on checks tagged <see cref="Live"/> (cheap in-process invariants; a failure here restarts
/// the pod). Tags are free-form — these constants are the conventional pair the Web endpoint
/// extensions default to, not a closed set.
/// </remarks>
public static class HealthTags
{
    /// <summary>
    /// The tag identifying checks that participate in readiness probes ("is this instance ready
    /// to receive traffic?").
    /// </summary>
    public const string Ready = "ready";

    /// <summary>
    /// The tag identifying checks that participate in liveness probes ("is this process still
    /// alive, or should it be restarted?").
    /// </summary>
    public const string Live = "live";
}
