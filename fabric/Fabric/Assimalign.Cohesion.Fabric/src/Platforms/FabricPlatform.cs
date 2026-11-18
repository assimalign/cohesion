namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The base of every platform: one per deployment target, compiled in, never loaded as a plug-in.
/// </summary>
public abstract class FabricPlatform : IFabricPlatform
{
    /// <inheritdoc />
    public abstract PlatformName Name { get; }

    /// <inheritdoc />
    public abstract ApiVersion ContractVersion { get; }

    /// <inheritdoc />
    public abstract void Configure(IFabricPlatformBuilder platform);
}
