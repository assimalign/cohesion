using System;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The directory of an identity hub: its users and groups, each a fixed set defined in code,
/// served locked, over the ones kept in an <see cref="IIdentityHubStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The objects defined in code never reach the store.</b> They are held by the directory, are
/// the same on every node that runs the same code, and are gone when the code that defines them
/// is. Stored objects live in one record set per kind (<c>users</c>, <c>groups</c>), which
/// keeps each name unique among them.
/// </para>
/// <para>
/// <b>Code wins, and that is how code adopts an object created at run time.</b> A stored object
/// with the identifier of one defined in code is not served, and a write may not claim a name
/// code holds. An identifier derived from a name is the same wherever the name is used, so an
/// object stored under a name is shadowed by the first deployment whose code defines that name.
/// The stored record is left in the store and is served again if that code is removed.
/// </para>
/// <para>
/// The directory holds no connection of its own, does not own the store, and is safe for
/// concurrent use when the store is.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var descriptor = new IdentityHubDirectoryDescriptor();
/// descriptor.Groups.Add(IdentityGroup.Create(new() { Name = "orders-admins" }));
/// descriptor.Users.Add(IdentityUser.Create(new() { Name = "ada@contoso.example" }));
///
/// IdentityHubDirectory directory = IdentityHubDirectory.Create(store, descriptor);
/// IdentityUser? ada = await directory.Users.FindByNameAsync("ada@contoso.example");
/// </code>
/// </example>
public sealed class IdentityHubDirectory : IIdentityHubDirectory
{
    private IdentityHubDirectory(IIdentityUserRegistry users, IIdentityGroupRegistry groups)
    {
        Users = users;
        Groups = groups;
    }

    /// <inheritdoc />
    public IIdentityUserRegistry Users { get; }

    /// <inheritdoc />
    public IIdentityGroupRegistry Groups { get; }

    /// <summary>
    /// Creates a directory over a store, with the identity objects defined in code.
    /// </summary>
    /// <remarks>
    /// The objects defined in code are checked against each other here, once, so a mistake in
    /// them stops the hub from starting instead of failing a request later: within a kind, an
    /// identifier and a name may each appear once.
    /// </remarks>
    /// <param name="store">The store that holds the objects created at run time.</param>
    /// <param name="descriptor">The objects defined in code, or <see langword="null"/> for none.</param>
    /// <returns>The directory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="descriptor"/> holds a <see langword="null"/> object, or two objects of one
    /// kind with the same identifier or the same name.
    /// </exception>
    public static IdentityHubDirectory Create(IIdentityHubStore store, IdentityHubDirectoryDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        return new IdentityHubDirectory(
            new IdentityUserRegistry(store, descriptor?.Users, nameof(descriptor)),
            new IdentityGroupRegistry(store, descriptor?.Groups, nameof(descriptor)));
    }
}
