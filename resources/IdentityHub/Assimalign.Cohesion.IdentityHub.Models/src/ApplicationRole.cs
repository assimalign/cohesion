using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.IdentityHub.Models;

public sealed class ApplicationRole
{
    public RoleId Id { get; set; }
    public ApplicationId ApplicationId { get; set; }
}
