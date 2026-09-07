namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Binds one manifest endpoint to a port on the plan's container.
/// </summary>
/// <param name="Endpoint">The manifest endpoint name.</param>
/// <param name="ContainerPort">The port exposed by the container.</param>
/// <param name="Protocol">The transport protocol, normally <c>tcp</c> or <c>udp</c>.</param>
public sealed record PortBinding(string Endpoint, int ContainerPort, string Protocol);
