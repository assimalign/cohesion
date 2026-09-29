namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Classifies the authenticated identity calling a resource.</summary>
public enum ResourceCallerKind
{
    /// <summary>The resource's own application gateway.</summary>
    Gateway,

    /// <summary>A trusted issuer of another application, such as a peer gateway.</summary>
    Peer,

    /// <summary>A developer or operator acting through a developer credential.</summary>
    Developer,

    /// <summary>A resource emitting telemetry to a telemetry sink.</summary>
    TelemetryEmitter,
}
