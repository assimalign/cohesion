namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Defines the transport an operator tool uses to reach its endpoint.</summary>
public enum ResourceToolProtocol
{
    /// <summary>HTTP request and response exchanges.</summary>
    Http,

    /// <summary>A WebSocket session, bootstrapped over HTTP/1.1 Upgrade or extended CONNECT.</summary>
    WebSocket,

    /// <summary>A raw TCP stream, tunneled by the gateway.</summary>
    Tcp,
}
