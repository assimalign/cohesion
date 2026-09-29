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
/// <param name="Certificate">The logical name of the Secret mount carrying this endpoint's PEM bundle, the reserved literal public, or an empty string.</param>
[method: JsonConstructor]
public sealed record PortBinding(
    string Endpoint,
    int ContainerPort,
    string Protocol,
    string Scheme = "",
    string Certificate = "")
{
    /// <summary>Initializes a legacy v1 binding without an endpoint URI scheme.</summary>
    /// <param name="Endpoint">The manifest endpoint name.</param>
    /// <param name="ContainerPort">The port exposed by the container.</param>
    /// <param name="Protocol">The transport protocol.</param>
    public PortBinding(string Endpoint, int ContainerPort, string Protocol)
        : this(Endpoint, ContainerPort, Protocol, string.Empty)
    {
    }

    /// <summary>Initializes a binding with its URI scheme and no certificate mount.</summary>
    /// <param name="Endpoint">The manifest endpoint name.</param>
    /// <param name="ContainerPort">The port exposed by the container.</param>
    /// <param name="Protocol">The transport protocol.</param>
    /// <param name="Scheme">The endpoint URI scheme.</param>
    public PortBinding(string Endpoint, int ContainerPort, string Protocol, string Scheme)
        : this(Endpoint, ContainerPort, Protocol, Scheme, string.Empty)
    {
    }

    /// <summary>Deconstructs a binding using the field set preceding certificate metadata.</summary>
    /// <param name="Endpoint">Receives the endpoint name.</param>
    /// <param name="ContainerPort">Receives the container port.</param>
    /// <param name="Protocol">Receives the transport protocol.</param>
    /// <param name="Scheme">Receives the URI scheme.</param>
    public void Deconstruct(out string Endpoint, out int ContainerPort, out string Protocol, out string Scheme)
    {
        Endpoint = this.Endpoint;
        ContainerPort = this.ContainerPort;
        Protocol = this.Protocol;
        Scheme = this.Scheme;
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
