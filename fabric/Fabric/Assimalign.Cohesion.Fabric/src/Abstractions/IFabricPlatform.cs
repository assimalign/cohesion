namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One deployment target, compiled into the ResourceManager and the AppHost. A platform is never
/// loaded as a plug-in.
/// </summary>
/// <remarks>Derive from <see cref="FabricPlatform"/> rather than implementing this directly.</remarks>
public interface IFabricPlatform
{
    /// <summary>
    /// Gets the platform's name, such as <c>kubernetes</c>.
    /// </summary>
    PlatformName Name { get; }

    /// <summary>
    /// Gets the version of the extension and type set this build serves. The ResourceManager
    /// refuses a platform built against an incompatible contract.
    /// </summary>
    ApiVersion ContractVersion { get; }

    /// <summary>
    /// Registers what the platform contributes.
    /// </summary>
    /// <param name="platform">The builder to register on.</param>
    void Configure(IFabricPlatformBuilder platform);
}
