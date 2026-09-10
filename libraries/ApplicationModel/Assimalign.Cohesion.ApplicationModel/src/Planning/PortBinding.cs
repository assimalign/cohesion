using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Binds one manifest endpoint to a port on the plan's container.
/// </summary>
/// <param name="Endpoint">The manifest endpoint name.</param>
/// <param name="ContainerPort">The port exposed by the container.</param>
/// <param name="Protocol">The transport protocol, normally <c>tcp</c> or <c>udp</c>.</param>
/// <param name="Scheme">
/// The endpoint URI scheme, or an empty string for a legacy plan that omitted endpoint schemes.
/// </param>
[method: JsonConstructor]
public sealed record PortBinding(
    string Endpoint,
    int ContainerPort,
    string Protocol,
    string Scheme = "")
{
    /// <summary>Initializes a legacy v1 binding without an endpoint URI scheme.</summary>
    /// <param name="Endpoint">The manifest endpoint name.</param>
    /// <param name="ContainerPort">The port exposed by the container.</param>
    /// <param name="Protocol">The transport protocol.</param>
    public PortBinding(string Endpoint, int ContainerPort, string Protocol)
        : this(Endpoint, ContainerPort, Protocol, string.Empty)
    {
    }

    /// <summary>Deconstructs a binding using the original version 1 field set.</summary>
    /// <param name="Endpoint">Receives the manifest endpoint name.</param>
    /// <param name="ContainerPort">Receives the container port.</param>
    /// <param name="Protocol">Receives the transport protocol.</param>
    public void Deconstruct(out string Endpoint, out int ContainerPort, out string Protocol)
    {
        Endpoint = this.Endpoint;
        ContainerPort = this.ContainerPort;
        Protocol = this.Protocol;
    }
}
