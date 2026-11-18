using System;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The identity of a resource: a version 7 UUID that the ResourceManager assigns when the
/// resource is created.
/// </summary>
/// <remarks>
/// <para>
/// A resource's <see cref="ResourceId"/> is its address and can change when the resource moves
/// between applications. Its uid never changes and is never reused. Owner references,
/// <c>$ref</c> edges, role assignments, operations, and platform labels all use the uid.
/// </para>
/// <para>
/// <see langword="default"/> holds <see cref="Guid.Empty"/> and is never a valid identity; a
/// boundary that requires a resource refuses it.
/// </para>
/// </remarks>
public partial struct ResourceUid
{
    /// <summary>
    /// Creates a new uid from the current time, as the ResourceManager does at create.
    /// </summary>
    /// <returns>A new version 7 uid.</returns>
    public static ResourceUid CreateVersion7() => new(Guid.CreateVersion7());

    /// <summary>
    /// Determines whether <paramref name="value"/> is a non-empty version 7 UUID.
    /// </summary>
    /// <param name="value">The candidate uid.</param>
    /// <param name="message">The reason the value is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a version 7 UUID.</returns>
    public static partial bool IsValid(Guid value, out string? message)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            message = ErrorMessages.ResourceUidInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
