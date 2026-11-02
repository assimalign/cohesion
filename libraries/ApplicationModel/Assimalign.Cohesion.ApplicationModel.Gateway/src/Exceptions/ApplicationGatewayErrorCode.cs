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

    /// <summary>
    /// A created or updated resource's workload stopped before any instance published, as its
    /// infrastructure provider reported. The inner exception is the workload's failure, when the
    /// platform reported one.
    /// </summary>
    ResourceFailed,

    /// <summary>
    /// A later run of the gateway took its record over, so this run can no longer save it and stops.
    /// </summary>
    RecordSuperseded,

    /// <summary>
    /// No registered instance that declares an operator's action could run it: the resource has no
    /// such instance that serves a control plane, or the gateway reached none of them. The inner
    /// exception is the failure, or an <see cref="System.AggregateException"/> of each, when the
    /// gateway called any.
    /// </summary>
    InstanceUnavailable,
}
