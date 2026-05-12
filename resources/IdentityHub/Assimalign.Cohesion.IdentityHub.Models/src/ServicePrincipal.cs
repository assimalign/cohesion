using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.IdentityHub.Models;

/// <summary>
/// A service principal acts as a non-human identity that allows applications or automated processes 
/// to authenticate and access resources in a secure and controlled manner.
/// </summary>
public abstract class ServicePrincipal : TenantObject
{
    public override ObjectKind Kind { get; } = ObjectKind.ServicePrincipal;
}
