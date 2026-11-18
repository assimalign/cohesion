using Assimalign.Cohesion.Fabric.Internal;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The address of a resource in the ResourceManager.
/// </summary>
/// <remarks>
/// <para>The address forms are:</para>
/// <code>
/// /environments/{env}
/// /environments/{env}/groups/{group}
/// /environments/{env}/applications/{app}
/// /environments/{env}/applications/{app}/providers/{Namespace}/{type}/{name}
/// /environments/{env}/operations/{operation}
/// </code>
/// <para>
/// The address can change when a resource moves between applications; its
/// <see cref="ResourceUid"/> cannot. Store an edge by uid, never by id.
/// </para>
/// </remarks>
public partial struct ResourceId
{
    /// <summary>
    /// Determines whether <paramref name="value"/> is one of the address forms.
    /// </summary>
    /// <param name="value">The candidate address.</param>
    /// <param name="message">The reason the address is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a valid address.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (!FabricGrammar.IsResourceId(value))
        {
            message = ErrorMessages.ResourceIdInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
