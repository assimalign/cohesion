namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a stable service address declared by a resource plan.
/// </summary>
/// <param name="Name">The service name.</param>
/// <param name="Endpoint">
/// The manifest endpoint served by this service, or <see langword="null"/> for a governing
/// headless service that has no endpoint of its own.
/// </param>
/// <param name="Port">The service port, or <see langword="null"/> for a portless governing service.</param>
/// <param name="Protocol">The transport protocol.</param>
/// <param name="Headless">Whether the service must expose workload identities directly.</param>
/// <param name="Governing">Whether the service governs stable workload identity.</param>
public sealed record ServiceSpec(
    string Name,
    string? Endpoint,
    int? Port,
    string Protocol,
    bool Headless,
    bool Governing);
