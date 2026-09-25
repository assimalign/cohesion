using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.IdentityHub.Models;

public abstract class TenantObject
{
    /// <summary>
    /// Represents the tenant Id the object belongs to.
    /// </summary>
    public TenantId TenantId { get; set; }

    /// <summary>
    /// Represents the unique object Id for a given tenant.
    /// </summary>
    public ObjectId ObjectId { get; set; }

    /// <summary>
    /// Represents the object kind in the tenant.
    /// </summary>
    public abstract ObjectKind Kind { get; }

    /// <summary>
    /// 
    /// </summary>
    public AuditEntry? Audit { get; set; }
}