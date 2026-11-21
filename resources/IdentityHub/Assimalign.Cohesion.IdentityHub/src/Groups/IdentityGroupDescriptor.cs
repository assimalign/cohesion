using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The contents of a group, as they are written down before
/// <see cref="IdentityGroup.Create(IdentityGroupDescriptor)"/> checks them as a whole.
/// </summary>
/// <remarks>
/// A descriptor is plain, mutable, and unchecked: it is what code, a builder callback, or a
/// management API fills in. Nothing reads a group from a descriptor until
/// <see cref="IdentityGroup.Create(IdentityGroupDescriptor)"/> has taken its snapshot, so a
/// descriptor changed afterwards changes no group.
/// </remarks>
public sealed class IdentityGroupDescriptor
{
    /// <summary>
    /// Gets or sets the group's identifier. Left <see langword="default"/>, it is derived from
    /// <see cref="Name"/> with <see cref="IdentityGroupId.FromName"/>.
    /// </summary>
    /// <remarks>
    /// Set it to keep an identifier across a change of name: a replacement of a stored group
    /// carries the identifier the group was created with, and code that renames a group it
    /// defines names the old identifier here.
    /// </remarks>
    public IdentityGroupId Id { get; set; }

    /// <summary>
    /// Gets or sets the group's name. It is required.
    /// </summary>
    public IdentityGroupName Name { get; set; }

    /// <summary>
    /// Gets or sets the name a person is shown for the group. Left <see langword="null"/> or
    /// empty, it is the text of <see cref="Name"/>.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets what the group is for, in words a person is shown.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Creates a descriptor with the contents of a group, to change and create a replacement
    /// from.
    /// </summary>
    /// <param name="group">The group to copy.</param>
    /// <returns>A descriptor that creates a group equal to <paramref name="group"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    public static IdentityGroupDescriptor From(IdentityGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return new IdentityGroupDescriptor
        {
            Id = group.Id,
            Name = group.Name,
            DisplayName = group.DisplayName,
            Description = group.Description,
        };
    }
}
