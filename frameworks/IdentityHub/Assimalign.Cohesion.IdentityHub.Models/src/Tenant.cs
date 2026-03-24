using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.IdentityHub.Models;

public sealed class Tenant
{
    public TenantId Id { get; set; }
    public TenantInfo? Info { get; set; }
}
