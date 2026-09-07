namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes the platform-neutral controller and lifecycle semantics of a resource workload.
/// </summary>
/// <param name="Kind">The controller semantics to realize.</param>
/// <param name="Replicas">The requested replica count.</param>
/// <param name="StableIdentity">Whether replicas require stable identity.</param>
/// <param name="Gate">The initial readiness completion and success states.</param>
/// <param name="StopGraceSeconds">The graceful-stop budget, in seconds.</param>
public sealed record WorkloadSpec(
    WorkloadKind Kind,
    int Replicas,
    bool StableIdentity,
    ReadinessGate Gate,
    int StopGraceSeconds);
