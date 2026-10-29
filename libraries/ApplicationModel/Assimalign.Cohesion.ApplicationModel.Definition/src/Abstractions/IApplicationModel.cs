namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The immutable model of one gateway's application: what the gateway deploys, and the parent it
/// joins, as validated manifest data.
/// </summary>
/// <remarks>
/// A model is built from code, by <see cref="IApplicationModelBuilder.Build"/>, or from manifest
/// data, by <see cref="ApplicationModel.Create(ApplicationManifest)"/>. Both paths enforce the same
/// rules, so a gateway treats the two alike. A model covers one gateway: a child gateway has a model
/// of its own, built by its own program.
/// </remarks>
public interface IApplicationModel
{
    /// <summary>
    /// Gets the id of the gateway the model is for.
    /// </summary>
    GatewayId Id => GatewayId.FromName(Name);

    /// <summary>
    /// Gets the name of the gateway the model is for.
    /// </summary>
    GatewayName Name => Manifest.Name;

    /// <summary>
    /// Gets the name of the parent gateway the gateway joins, or <see langword="null"/> for a root
    /// gateway.
    /// </summary>
    GatewayName? Parent => Manifest.Parent;

    /// <summary>
    /// Gets whether the gateway is a root or joins a parent.
    /// </summary>
    ApplicationGatewayRole Role => Parent is null ? ApplicationGatewayRole.Root : ApplicationGatewayRole.Composite;

    /// <summary>
    /// Gets the gateway's manifest: its name, its parent, and the resources it runs.
    /// </summary>
    ApplicationManifest Manifest { get; }
}
