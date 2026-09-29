using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub.Models;

/// <summary>
/// Represents a non-human tenant identity used by an application or automated process.
/// </summary>
public abstract class ServicePrincipal : TenantObject
{
    /// <summary>
    /// Gets or sets the canonical protocol-neutral application identity represented by this principal.
    /// </summary>
    public IIdentitySubject? Identity { get; set; }

    /// <summary>
    /// Gets the tenant-directory object kind.
    /// </summary>
    public override ObjectKind Kind { get; } = ObjectKind.ServicePrincipal;
}
