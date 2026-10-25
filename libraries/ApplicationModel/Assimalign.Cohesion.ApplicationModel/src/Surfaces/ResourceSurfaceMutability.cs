namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Describes when a configuration section of a runtime surface may change after deployment.</summary>
public enum ResourceSurfaceMutability
{
    /// <summary>Only a deployment sets the section; the management API cannot override it (wire value <c>deployment</c>).</summary>
    Deployment,

    /// <summary>An override is stored, and an instance applies it when it next starts (wire value <c>restart</c>).</summary>
    Restart,

    /// <summary>An override is stored, and running instances apply it without restarting (wire value <c>live</c>).</summary>
    Live,
}
