namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Defines when a configuration section may change after the resource was deployed.</summary>
public enum ResourceConfigurationMutability
{
    /// <summary>Only a deployment sets the section; the management API refuses to change it.</summary>
    Deployment,

    /// <summary>The management API may change the section; instances apply the change when they restart.</summary>
    Restart,

    /// <summary>The management API may change the section; running instances apply the change in place.</summary>
    Live,
}
