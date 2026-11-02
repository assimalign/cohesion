namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The parent gateway a gateway joins, as the gateway's model refers to it: the source of the
/// outputs its resources' settings use from above.
/// </summary>
/// <remarks>
/// A child's model declares none of its parent's resources; it names them. The parent answers the
/// child's join with the values of the outputs named here, and a setting that uses one is filled in
/// from that answer.
/// </remarks>
public interface IApplicationModelParent
{
    /// <summary>
    /// Gets the id of the parent gateway.
    /// </summary>
    /// <remarks>The id is derived from <see cref="Name"/> by <see cref="GatewayId.FromName(GatewayName)"/>.</remarks>
    GatewayId Id => GatewayId.FromName(Name);

    /// <summary>
    /// Gets the name of the parent gateway.
    /// </summary>
    GatewayName Name { get; }

    /// <summary>
    /// Gets a placeholder for the endpoint of one of the parent's resources.
    /// </summary>
    /// <param name="provider">The resource's provider.</param>
    /// <param name="name">The resource's name.</param>
    /// <returns>The placeholder for the resource's <see cref="OutputName.Endpoint"/> output.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>.
    /// </exception>
    ApplicationResourceOutput Endpoint(ProviderName provider, ResourceName name) => Output(provider, name, OutputName.Endpoint);

    /// <summary>
    /// Gets a placeholder for one output of one of the parent's resources.
    /// </summary>
    /// <param name="provider">The resource's provider.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="output">The output's name.</param>
    /// <returns>The placeholder, for a setting of this gateway's resources.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="provider"/>, <paramref name="name"/>, or <paramref name="output"/> is <see langword="default"/>.
    /// </exception>
    ApplicationResourceOutput Output(ProviderName provider, ResourceName name, OutputName output);
}
