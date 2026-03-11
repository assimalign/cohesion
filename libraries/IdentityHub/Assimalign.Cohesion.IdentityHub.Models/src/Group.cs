using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.IdentityHub.Models;

public sealed class Group : TenantObject
{
    /// <summary>
    /// The unique identifier for the group.
    /// </summary>
    public GroupId Id { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public GroupInfo? Info { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public override ObjectKind Kind { get; } = ObjectKind.Group;
}
