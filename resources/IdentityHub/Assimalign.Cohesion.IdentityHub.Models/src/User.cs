using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub.Models;

/// <summary>
/// Represents an IdentityHub user record within a tenant directory.
/// </summary>
public sealed class User : TenantObject
{
    /// <summary>
    /// Gets or sets the unique identifier for the user object.
    /// </summary>
    public UserId Id { get; set; }

    /// <summary>
    /// Gets or sets the user's directory username.
    /// </summary>
    public Username Username { get; set; }

    /// <summary>
    /// Gets or sets IdentityHub-specific directory information.
    /// </summary>
    public UserInfo? Info { get; set; }

    /// <summary>
    /// Gets or sets the canonical protocol-neutral identity represented by this user.
    /// </summary>
    public IIdentitySubject? Identity { get; set; }

    /// <summary>
    /// Gets the tenant-directory object kind.
    /// </summary>
    public override ObjectKind Kind { get; } = ObjectKind.User;
}
