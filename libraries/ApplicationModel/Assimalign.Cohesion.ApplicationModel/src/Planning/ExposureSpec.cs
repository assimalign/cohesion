namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Declares that a service endpoint is exposed outside the application boundary.
/// </summary>
/// <param name="Name">The exposure name.</param>
/// <param name="Endpoint">The manifest endpoint being exposed.</param>
/// <param name="Service">The service that backs the exposure.</param>
/// <param name="Scheme">The endpoint scheme.</param>
/// <param name="Protocol">The endpoint transport protocol.</param>
/// <param name="Port">The exposed service port.</param>
public sealed record ExposureSpec(
    string Name,
    string Endpoint,
    string Service,
    string Scheme,
    string Protocol,
    int Port);
