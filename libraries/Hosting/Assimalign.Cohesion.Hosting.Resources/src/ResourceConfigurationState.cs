namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Reports which value of a configuration section is in force on this instance.</summary>
public enum ResourceConfigurationState
{
    /// <summary>The section runs on the resource's own configuration; the deployment record does not change it.</summary>
    Baseline,

    /// <summary>The value the deployment record describes is in force.</summary>
    Applied,

    /// <summary>The section refused the value the deployment record describes; its previous value stays in force.</summary>
    Rejected,

    /// <summary>The deployment record changed a section that is applied only when the instance starts.</summary>
    RestartRequired,
}
