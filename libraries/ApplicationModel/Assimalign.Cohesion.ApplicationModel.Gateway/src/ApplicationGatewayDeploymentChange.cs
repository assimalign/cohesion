namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// What deploying a resource would change on the infrastructure.
/// </summary>
public enum ApplicationGatewayDeploymentChange
{
    /// <summary>The workload already runs as planned; its instances keep running.</summary>
    None,

    /// <summary>The workload does not exist; deploying it starts new instances.</summary>
    Create,

    /// <summary>The workload exists but differs from the plan; deploying it replaces its instances.</summary>
    Update,
}
