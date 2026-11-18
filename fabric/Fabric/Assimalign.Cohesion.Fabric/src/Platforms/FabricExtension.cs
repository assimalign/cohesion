using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The base of a typed platform extension: reads each of its versions into one hub type.
/// </summary>
/// <typeparam name="THub">The hub type every version converts to.</typeparam>
public abstract class FabricExtension<THub> : IFabricExtension where THub : class
{
    /// <inheritdoc />
    public abstract PlatformName Platform { get; }

    /// <inheritdoc />
    public abstract ResourceTypeName HostType { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<ApiVersion> ApiVersions { get; }

    /// <inheritdoc />
    public Type HubType => typeof(THub);

    /// <summary>
    /// Reads one version of the extension's block into the hub type. Unknown members are refused.
    /// </summary>
    /// <param name="version">The block's version, one of <see cref="ApiVersions"/>.</param>
    /// <param name="block">The block, <c>spec.platforms.&lt;platform&gt;</c> without its <c>apiVersion</c>.</param>
    /// <returns>The hub.</returns>
    /// <exception cref="JsonException">Thrown when the block is not a valid instance of <paramref name="version"/>.</exception>
    public abstract THub Read(ApiVersion version, JsonElement block);
}
