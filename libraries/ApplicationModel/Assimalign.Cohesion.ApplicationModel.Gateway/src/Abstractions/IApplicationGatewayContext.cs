namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// What a running gateway hands the gateway half of its platform: the model it runs, and the
/// publisher the platform's channel hands its instances' publications and renewals to.
/// </summary>
/// <remarks>
/// The gateway passes it to <see cref="IApplicationGatewayInfrastructureProvider.StartAsync"/>. It
/// carries no access to the gateway's record: only the gateway saves it, so every save stays behind
/// the record's epoch fence.
/// </remarks>
public interface IApplicationGatewayContext
{
    /// <summary>
    /// Gets the model the gateway runs.
    /// </summary>
    IApplicationModel Model { get; }

    /// <summary>
    /// Gets the gateway as the publisher its instances publish to and renew with.
    /// </summary>
    IApplicationInstancePublisher Publisher { get; }
}
