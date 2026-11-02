namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One application resource: a logical service, such as <c>orders-api</c>, under its provider, such
/// as <c>Web</c>.
/// </summary>
/// <remarks>
/// A resource appears in three forms that share its identity: its definition, which a model
/// declares (<c>IApplicationResourceDefinition</c>); its deployment, a gateway's view of it across
/// its instances (<c>IApplicationGatewayDeployment</c>); and each instance, one running copy
/// (<c>IApplicationInstance</c>).
/// </remarks>
public interface IApplicationResource
{
    /// <summary>
    /// Gets the id every form of the resource shares.
    /// </summary>
    /// <remarks>
    /// The id is derived from <see cref="Provider"/> and <see cref="Name"/> by
    /// <see cref="ResourceId.FromName(ProviderName, ResourceName)"/>, so it is unique among one
    /// gateway's resources.
    /// </remarks>
    ResourceId Id => ResourceId.FromName(Provider, Name);

    /// <summary>
    /// Gets the resource's provider: its kind, such as <c>Web</c> or <c>Database.Sql</c>.
    /// </summary>
    ProviderName Provider { get; }

    /// <summary>
    /// Gets the resource's name, such as <c>orders-api</c>, unique among one gateway's resources of
    /// the same provider.
    /// </summary>
    ResourceName Name { get; }
}
