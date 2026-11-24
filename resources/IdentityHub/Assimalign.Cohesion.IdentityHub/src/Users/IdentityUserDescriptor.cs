using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The contents of a user, as they are written down before
/// <see cref="IdentityUser.Create(IdentityUserDescriptor)"/> checks them as a whole.
/// </summary>
/// <remarks>
/// A descriptor is plain, mutable, and unchecked: it is what code, a builder callback, or a
/// management API fills in. Nothing reads a user from a descriptor until
/// <see cref="IdentityUser.Create(IdentityUserDescriptor)"/> has taken its snapshot, so a
/// descriptor changed afterwards changes no user.
/// </remarks>
public sealed class IdentityUserDescriptor
{
    /// <summary>
    /// Gets or sets the user's identifier. Left <see langword="default"/>, it is derived from
    /// <see cref="Name"/> with <see cref="IdentityUserId.FromName"/>.
    /// </summary>
    /// <remarks>
    /// Set it to keep an identifier across a change of name: a replacement of a stored user
    /// carries the identifier the user was created with, and code that renames a user it
    /// defines names the old identifier here.
    /// </remarks>
    public IdentityUserId Id { get; set; }

    /// <summary>
    /// Gets or sets the name the user signs in with. It is required.
    /// </summary>
    public IdentityUserName Name { get; set; }

    /// <summary>
    /// Gets or sets the name a person is shown for the user. Left <see langword="null"/> or
    /// empty, it is the text of <see cref="Name"/>.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the user's given name.
    /// </summary>
    public string? GivenName { get; set; }

    /// <summary>
    /// Gets or sets the user's family name.
    /// </summary>
    public string? FamilyName { get; set; }

    /// <summary>
    /// Gets or sets the user's email address.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="Email"/> is known to reach the user.
    /// </summary>
    public bool IsEmailVerified { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user may sign in. It is
    /// <see langword="true"/> unless set otherwise.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Creates a descriptor with the contents of a user, to change and create a replacement
    /// from.
    /// </summary>
    /// <param name="user">The user to copy.</param>
    /// <returns>A descriptor that creates a user equal to <paramref name="user"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> is <see langword="null"/>.</exception>
    public static IdentityUserDescriptor From(IdentityUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new IdentityUserDescriptor
        {
            Id = user.Id,
            Name = user.Name,
            DisplayName = user.DisplayName,
            GivenName = user.GivenName,
            FamilyName = user.FamilyName,
            Email = user.Email,
            IsEmailVerified = user.IsEmailVerified,
            IsEnabled = user.IsEnabled,
        };
    }
}
