namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A gateway's position in an application's hierarchy.
/// </summary>
public enum GatewayRole
{
    /// <summary>
    /// The gateway at the top of the hierarchy. A deployment pipeline deploys it, and it deploys the
    /// rest.
    /// </summary>
    Root,

    /// <summary>
    /// A gateway another gateway deploys. It deploys its own set's resources and child gateways.
    /// </summary>
    Composite,
}
