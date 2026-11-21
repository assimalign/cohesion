using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The identity objects code defines for a directory, as they are collected before
/// <see cref="IdentityHubDirectory.Create(IIdentityHubStore, IdentityHubDirectoryDescriptor)"/>
/// checks them against each other.
/// </summary>
/// <remarks>
/// A descriptor is plain and mutable: it is what an application builder, or code that composes a
/// directory by hand, fills in. The directory takes its snapshot when it is created, so a
/// descriptor changed afterwards changes no directory. Every object listed here is served
/// locked.
/// </remarks>
public sealed class IdentityHubDirectoryDescriptor
{
    /// <summary>
    /// Gets the users code defines.
    /// </summary>
    public IList<IdentityUser> Users { get; } = new List<IdentityUser>();

    /// <summary>
    /// Gets the groups code defines.
    /// </summary>
    public IList<IdentityGroup> Groups { get; } = new List<IdentityGroup>();
}
