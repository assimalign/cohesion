using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies platform extension data a resource carries in its plan, as <c>&lt;platform&gt;/&lt;name&gt;</c>, such
/// as <c>kubernetes/hpa</c>.
/// </summary>
/// <remarks>
/// Both parts are lowercase ASCII letters and digits in hyphen-separated words of at most 64 characters. The
/// platform part is the name of the gateway that owns the key: that gateway's compiler reads the data and refuses a
/// key it does not know, and every other gateway ignores it.
/// </remarks>
public partial struct ResourceExtensionKey
{
    /// <summary>Gets the platform part of the key, the name of the gateway that owns it.</summary>
    public string Platform => Value is null ? string.Empty : Value[..Value.IndexOf('/')];

    /// <summary>Validates the extension key grammar.</summary>
    /// <param name="value">The candidate key.</param>
    /// <param name="message">The rejection reason, or null when valid.</param>
    /// <returns>Whether the key is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (ResourceSurfaceGrammar.IsExtensionKey(value))
        {
            message = null;
            return true;
        }

        message = ErrorMessages.ResourceExtensionKeyInvalid;
        return false;
    }
}
