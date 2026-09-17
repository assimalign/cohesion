namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Creates application-scoped gateway control planes for a gateway session.
/// </summary>
public interface IApplicationGatewayControlPlaneFactory
{
    /// <summary>Creates the control plane for one application.</summary>
    /// <param name="application">The application the control plane will serve.</param>
    /// <returns>A new, unstarted control plane.</returns>
    IApplicationGatewayControlPlane Create(ApplicationName application);
}
