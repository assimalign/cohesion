using System.Diagnostics;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One operator tool of a resource's runtime surface, such as a SQL session a management portal can open. A tool is
/// metadata: the gateway brokers the connection, and a tool that reaches the resource's data requires a data-plane
/// credential that control-plane permission never grants.
/// </summary>
[DebuggerDisplay("{Name,nq} ({Protocol}) on {Endpoint,nq}")]
public sealed class ResourceSurfaceTool
{
    internal ResourceSurfaceTool(
        string name,
        string endpoint,
        ResourceSurfaceToolProtocol protocol,
        string? subprotocol,
        bool requiresDataAccess)
    {
        Name = name;
        Endpoint = endpoint;
        Protocol = protocol;
        Subprotocol = subprotocol;
        RequiresDataAccess = requiresDataAccess;
    }

    /// <summary>Gets the tool name: lowercase ASCII letters and digits in hyphen-separated words.</summary>
    public string Name { get; }

    /// <summary>Gets the name of the resource endpoint that serves the tool.</summary>
    public string Endpoint { get; }

    /// <summary>Gets the transport the tool uses.</summary>
    public ResourceSurfaceToolProtocol Protocol { get; }

    /// <summary>Gets the WebSocket subprotocol the tool speaks, if any.</summary>
    public string? Subprotocol { get; }

    /// <summary>Gets whether opening the tool requires a data-plane credential.</summary>
    public bool RequiresDataAccess { get; }
}
