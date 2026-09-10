using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes the platform-neutral controller and lifecycle semantics of a resource workload.
/// </summary>
/// <param name="Kind">The controller semantics to realize.</param>
/// <param name="Replicas">The requested replica count.</param>
/// <param name="StableIdentity">Whether replicas require stable identity.</param>
/// <param name="Gate">The initial readiness completion and success states.</param>
/// <param name="StopGraceSeconds">The graceful-stop budget, in seconds.</param>
/// <param name="RestartPolicy">
/// The platform-neutral restart policy name, or an empty string for a legacy plan that omitted it.
/// </param>
[method: JsonConstructor]
public sealed record WorkloadSpec(
    WorkloadKind Kind,
    int Replicas,
    bool StableIdentity,
    ReadinessGate Gate,
    int StopGraceSeconds,
    string RestartPolicy = "")
{
    /// <summary>Initializes a legacy v1 workload without an explicit restart policy.</summary>
    /// <param name="Kind">The controller semantics to realize.</param>
    /// <param name="Replicas">The requested replica count.</param>
    /// <param name="StableIdentity">Whether replicas require stable identity.</param>
    /// <param name="Gate">The initial readiness completion and success states.</param>
    /// <param name="StopGraceSeconds">The graceful-stop budget, in seconds.</param>
    public WorkloadSpec(
        WorkloadKind Kind,
        int Replicas,
        bool StableIdentity,
        ReadinessGate Gate,
        int StopGraceSeconds)
        : this(Kind, Replicas, StableIdentity, Gate, StopGraceSeconds, string.Empty)
    {
    }

    /// <summary>Deconstructs a workload using the original version 1 field set.</summary>
    /// <param name="Kind">Receives the controller kind.</param>
    /// <param name="Replicas">Receives the replica count.</param>
    /// <param name="StableIdentity">Receives whether stable identity is required.</param>
    /// <param name="Gate">Receives the readiness gate.</param>
    /// <param name="StopGraceSeconds">Receives the graceful-stop budget.</param>
    public void Deconstruct(
        out WorkloadKind Kind,
        out int Replicas,
        out bool StableIdentity,
        out ReadinessGate Gate,
        out int StopGraceSeconds)
    {
        Kind = this.Kind;
        Replicas = this.Replicas;
        StableIdentity = this.StableIdentity;
        Gate = this.Gate;
        StopGraceSeconds = this.StopGraceSeconds;
    }
}
