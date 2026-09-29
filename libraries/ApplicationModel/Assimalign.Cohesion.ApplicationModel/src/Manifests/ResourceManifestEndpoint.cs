namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a network endpoint exposed by a resource.
/// </summary>
public sealed record ResourceManifestEndpoint
{
    /// <summary>
    /// Gets the endpoint's logical name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the URI scheme used by the endpoint.
    /// </summary>
    public string Scheme { get; init; } = string.Empty;

    /// <summary>
    /// Gets the transport protocol, normally <c>tcp</c> or <c>udp</c>.
    /// </summary>
    public string Protocol { get; init; } = "tcp";

    /// <summary>
    /// Gets the port exposed inside the resource's container or process boundary.
    /// </summary>
    public int ContainerPort { get; init; }

    /// <summary>
    /// Gets the stable standalone-development port, when one is declared.
    /// </summary>
    public int? DevPort { get; init; }

    /// <summary>
    /// Gets a value indicating whether the endpoint should receive public exposure.
    /// </summary>
    public bool Public { get; init; }

    /// <summary>
    /// Gets the certificate mount or certificate policy associated with the endpoint.
    /// </summary>
    public string? Certificate { get; init; }
}
