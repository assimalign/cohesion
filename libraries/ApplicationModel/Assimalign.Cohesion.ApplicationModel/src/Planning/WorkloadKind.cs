namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies the platform-neutral controller semantics required by a resource workload.
/// </summary>
public enum WorkloadKind
{
    /// <summary>A replicated, continuously running workload without stable replica identity.</summary>
    Deployment = 0,

    /// <summary>A replicated workload whose replicas require stable identity.</summary>
    StatefulSet,

    /// <summary>A continuously running workload with one instance per eligible node.</summary>
    DaemonSet,

    /// <summary>A finite workload that succeeds after a clean exit.</summary>
    Job
}
