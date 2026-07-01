namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A network endpoint exposed by a resource. Used both as a declared (desired) endpoint on
/// <see cref="IEndpointResource"/> and as an observed (allocated) endpoint reported through
/// <see cref="IApplicationResourceStateManager"/>.
/// </summary>
/// <param name="Name">A logical name for the endpoint, for example <c>http</c> or <c>grpc</c>.</param>
/// <param name="Scheme">The endpoint scheme, for example <c>http</c>, <c>https</c>, or <c>tcp</c>.</param>
/// <param name="Port">The port the endpoint listens on. Zero means "let the platform allocate".</param>
/// <param name="IsPublic">
/// <see langword="true"/> when the endpoint should be reachable from outside the application
/// (for example exposed via a public service or ingress); <see langword="false"/> for
/// application-internal endpoints.
/// </param>
/// <param name="Host">The resolved host, when observed; <see langword="null"/> for a declared endpoint.</param>
public readonly record struct ResourceEndpoint(
    string Name,
    string Scheme,
    int Port,
    bool IsPublic = false,
    string? Host = null);
