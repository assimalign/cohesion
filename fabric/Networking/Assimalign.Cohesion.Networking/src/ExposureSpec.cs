using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Networking;

/// <summary>
/// The desired state of a <c>Cohesion.Networking/exposures</c> resource: who may reach one of a
/// workload's ports.
/// </summary>
/// <remarks>
/// <para>
/// The target is a <c>$ref</c> to a workload, never a Workloads type, so the Networking and
/// Workloads areas do not reference each other (rule F4).
/// </para>
/// <para>
/// Init properties rather than a positional record, so routing through an owned gateway (a host,
/// a path, a gateway resource) can be added without breaking callers once it is designed. Nothing
/// here decides that shape.
/// </para>
/// </remarks>
public sealed record ExposureSpec
{
    /// <summary>
    /// Gets the workload the exposure targets.
    /// </summary>
    public required FabricRef Target { get; init; }

    /// <summary>
    /// Gets the name of the targeted workload port, such as <c>http</c>.
    /// </summary>
    public required string Port { get; init; }

    /// <summary>
    /// Gets who may reach the port.
    /// </summary>
    public ExposureVisibility Visibility { get; init; } = ExposureVisibility.Internal;
}
