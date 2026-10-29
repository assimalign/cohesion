namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The failures an <see cref="ApplicationGatewayException"/> reports.
/// </summary>
public enum ApplicationGatewayErrorCode
{
    /// <summary>The gateway is not running, so it accepts no publications.</summary>
    PublicationsClosed,

    /// <summary>The gateway does not run the resource an instance published for.</summary>
    ResourceUnknown,

    /// <summary>The gateway tore down the resource after it missed its startup window.</summary>
    ResourceTornDown,

    /// <summary>A resource published no manifest within the startup window, so the gateway tore it down.</summary>
    StartupTimedOut,

    /// <summary>A setting uses an output that its resource's infrastructure provider did not report.</summary>
    OutputMissing,
}
