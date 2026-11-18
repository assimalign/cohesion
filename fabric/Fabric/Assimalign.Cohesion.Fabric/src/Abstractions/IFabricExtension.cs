using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// A typed platform extension (escape-hatch tier T1): settings for one platform, attached to a
/// fabric resource at <c>spec.platforms.&lt;platform&gt;</c>, identified by
/// <c>(platform, host type, apiVersion)</c>.
/// </summary>
/// <remarks>Derive from <see cref="FabricExtension{THub}"/> rather than implementing this directly.</remarks>
public interface IFabricExtension
{
    /// <summary>
    /// Gets the platform the extension belongs to. A driver sees only its own platform's blocks.
    /// </summary>
    PlatformName Platform { get; }

    /// <summary>
    /// Gets the fabric resource type the extension attaches to.
    /// </summary>
    ResourceTypeName HostType { get; }

    /// <summary>
    /// Gets the extension's versions. They version independently of the host type.
    /// </summary>
    IReadOnlyList<ApiVersion> ApiVersions { get; }

    /// <summary>
    /// Gets the hub type every version converts to.
    /// </summary>
    Type HubType { get; }
}
