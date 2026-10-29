namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Whether a gateway stands alone or joins a parent gateway.
/// </summary>
public enum ApplicationGatewayRole
{
    /// <summary>
    /// A gateway that joins no parent. Its own deployment pipeline deploys it, and child gateways
    /// may join it.
    /// </summary>
    Root,

    /// <summary>
    /// A gateway that joins a parent. Its own deployment pipeline deploys it too; it registers with
    /// the parent and reads the parent's outputs its settings use.
    /// </summary>
    Composite,
}
