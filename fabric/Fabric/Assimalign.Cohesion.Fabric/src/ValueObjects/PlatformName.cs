using Assimalign.Cohesion.Fabric.Internal;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The name of a deployment target, such as <c>inprocess</c>, <c>docker</c>, or <c>kubernetes</c>.
/// </summary>
/// <remarks>
/// An environment binds exactly one platform. The name keys a resource's typed platform
/// extensions (<c>spec.platforms.&lt;platform&gt;</c>) and its platform read side
/// (<c>status.outputs.platform.&lt;platform&gt;</c>).
/// </remarks>
public partial struct PlatformName
{
    /// <summary>
    /// Determines whether <paramref name="value"/> is a valid platform name.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is one lowercase label.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (!FabricGrammar.IsLabel(value))
        {
            message = ErrorMessages.PlatformNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
