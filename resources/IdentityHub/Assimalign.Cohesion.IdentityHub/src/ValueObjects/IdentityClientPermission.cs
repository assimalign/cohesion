using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One permission an app registration holds on an API: the registration that exposes the API,
/// the name of the scope or role, and how the permission is exercised.
/// </summary>
/// <remarks>
/// <para>
/// <b>A permission is the most the hub will issue, not a request awaiting approval.</b> The
/// hub never puts a scope or role into a token for a registration that does not hold the
/// matching permission. Where a user's consent is asked for, it narrows a delegated
/// permission for that user; it never widens one.
/// </para>
/// <para>
/// A permission refers to its API by <see cref="Resource"/> and <see cref="Name"/>, not by an
/// opaque identifier, so that code can state it. The reference is resolved when a token is
/// issued: a permission whose API or name does not exist then grants nothing.
/// <see langword="default"/> is not a permission and every registration boundary refuses it.
/// </para>
/// </remarks>
public readonly record struct IdentityClientPermission
{
    /// <summary>
    /// Initializes a permission.
    /// </summary>
    /// <param name="resource">The identifier of the registration that exposes the API.</param>
    /// <param name="name">The name of the scope or role.</param>
    /// <param name="kind">How the permission is exercised.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="resource"/> or <paramref name="name"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a permission kind.</exception>
    public IdentityClientPermission(
        IdentityClientId resource,
        IdentityClientPermissionName name,
        IdentityClientPermissionKind kind)
    {
        if (resource.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ClientIdRequired, nameof(resource));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.PermissionNameRequired, nameof(name));
        }

        if (kind is not (IdentityClientPermissionKind.Delegated or IdentityClientPermissionKind.Application))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, ErrorMessages.PermissionKindInvalid);
        }

        Resource = resource;
        Name = name;
        Kind = kind;
    }

    /// <summary>
    /// Gets the identifier of the registration that exposes the API.
    /// </summary>
    public IdentityClientId Resource { get; }

    /// <summary>
    /// Gets the name of the scope or role.
    /// </summary>
    public IdentityClientPermissionName Name { get; }

    /// <summary>
    /// Gets how the permission is exercised.
    /// </summary>
    public IdentityClientPermissionKind Kind { get; }

    /// <summary>
    /// Creates a permission on a scope of an API, exercised on behalf of a signed-in user.
    /// </summary>
    /// <param name="resource">The identifier of the registration that exposes the API.</param>
    /// <param name="scope">The name of the scope.</param>
    /// <returns>The delegated permission.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="resource"/> or <paramref name="scope"/> is <see langword="default"/>.
    /// </exception>
    public static IdentityClientPermission Delegated(IdentityClientId resource, IdentityClientPermissionName scope)
    {
        return new IdentityClientPermission(resource, scope, IdentityClientPermissionKind.Delegated);
    }

    /// <summary>
    /// Creates a permission on a role of an API, exercised by the application as itself.
    /// </summary>
    /// <param name="resource">The identifier of the registration that exposes the API.</param>
    /// <param name="role">The name of the role, which allows <see cref="IdentityClientRoleMembers.Applications"/>.</param>
    /// <returns>The application permission.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="resource"/> or <paramref name="role"/> is <see langword="default"/>.
    /// </exception>
    public static IdentityClientPermission Application(IdentityClientId resource, IdentityClientPermissionName role)
    {
        return new IdentityClientPermission(resource, role, IdentityClientPermissionKind.Application);
    }

    /// <summary>
    /// Returns the permission as the API, the name, and how it is exercised, such as
    /// <c>orders-api/orders.read (Delegated)</c>.
    /// </summary>
    /// <returns>The permission as text, or an empty string for <see langword="default"/>.</returns>
    public override string ToString()
    {
        return Resource.Value is null ? string.Empty : $"{Resource}/{Name} ({Kind})";
    }
}
