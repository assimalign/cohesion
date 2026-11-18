using Assimalign.Cohesion.Fabric.Internal;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The last segment of a resource's address: one or more dot-separated lowercase labels.
/// </summary>
/// <remarks>
/// A name a user declares has one label (<c>orders-api</c>). A composite's private child is named
/// <c>{owner}.{suffix}</c> (<c>identity.store</c>), so a user name and a child name never collide.
/// The authoring layer refuses a dot in a user name; this type accepts both forms, and
/// <see cref="IsChildName"/> tells them apart.
/// </remarks>
public partial struct ResourceName
{
    /// <summary>
    /// Gets a value indicating whether this is a composite's private child name (it contains a dot).
    /// </summary>
    public bool IsChildName => Value is not null && Value.Contains('.');

    /// <summary>
    /// Determines whether <paramref name="value"/> is a valid resource name.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when every dot-separated segment is a valid label.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (!FabricGrammar.IsDottedLabels(value))
        {
            message = ErrorMessages.ResourceNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
