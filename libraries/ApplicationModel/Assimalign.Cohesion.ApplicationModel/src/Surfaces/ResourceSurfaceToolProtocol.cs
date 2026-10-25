namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Describes the transport an operator tool of a runtime surface uses.</summary>
public enum ResourceSurfaceToolProtocol
{
    /// <summary>Plain HTTP requests (wire value <c>http</c>).</summary>
    Http,

    /// <summary>A WebSocket session (wire value <c>websocket</c>).</summary>
    WebSocket,

    /// <summary>A raw TCP stream (wire value <c>tcp</c>).</summary>
    Tcp,
}
