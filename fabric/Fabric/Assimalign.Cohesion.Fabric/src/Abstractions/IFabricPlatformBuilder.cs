namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What a platform registers when it is configured.
/// </summary>
/// <remarks>
/// <para>
/// The v2 design also lists <c>AddSystemDriver</c>, which deploys the ResourceManager itself.
/// How the ResourceManager is deployed is not decided (the design's decision 2), so the member
/// is not part of this contract yet.
/// </para>
/// <para>
/// The design does not say how a platform registers the drivers of the fabric areas
/// (<c>IWorkloadDriver&lt;T&gt;</c> and its siblings). This contract cannot name them, because
/// the fabric root references no area (rule F4). That is an open question for the design.
/// </para>
/// </remarks>
public interface IFabricPlatformBuilder
{
    /// <summary>
    /// Registers a typed platform extension on a fabric resource type (escape-hatch tier T1).
    /// </summary>
    /// <typeparam name="THub">The hub type every version of the extension converts to.</typeparam>
    /// <param name="extension">The extension.</param>
    /// <returns>This builder.</returns>
    IFabricPlatformBuilder AddExtension<THub>(FabricExtension<THub> extension) where THub : class;

    /// <summary>
    /// Registers a platform-native resource type and its provider (tiers T2 and T3), in the
    /// platform's own namespace.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>This builder.</returns>
    IFabricPlatformBuilder AddResourceType(IFabricResourceProvider provider);

    /// <summary>
    /// Registers the classifier that maps the platform's reasons onto <see cref="FaultCode"/>.
    /// </summary>
    /// <param name="classifier">The classifier.</param>
    /// <returns>This builder.</returns>
    IFabricPlatformBuilder AddFaultClassifier(IFabricFaultClassifier classifier);
}
