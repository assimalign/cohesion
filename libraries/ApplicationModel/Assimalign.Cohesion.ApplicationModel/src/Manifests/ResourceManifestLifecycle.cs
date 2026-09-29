namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes the resource's platform-neutral lifecycle requirements.
/// </summary>
public sealed record ResourceManifestLifecycle
{
    /// <summary>
    /// Gets the workload shape requested by the resource.
    /// </summary>
    public WorkloadKind Workload { get; init; }

    /// <summary>
    /// Gets the desired replica count.
    /// </summary>
    public int Replicas { get; init; } = 1;

    /// <summary>
    /// Gets the maximum replica count allowed by the resource, or <see langword="null"/> when unbounded.
    /// </summary>
    public int? MaxReplicas { get; init; }

    /// <summary>
    /// Gets the number of seconds allowed for graceful shutdown.
    /// </summary>
    public int StopGraceSeconds { get; init; } = 30;

    /// <summary>
    /// Gets the restart policy name.
    /// </summary>
    public string RestartPolicy { get; init; } = "OnFailure";

    /// <summary>
    /// Gets the exit-code contract understood by the gateway.
    /// </summary>
    public string ExitCodes { get; init; } = "cohesion/sysexits/v1";
}
