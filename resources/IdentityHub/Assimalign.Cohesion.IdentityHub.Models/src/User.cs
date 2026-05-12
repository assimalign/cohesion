using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.IdentityHub.Models;

public sealed class User : TenantObject
{
    /// <summary>
    /// The unique identifier for the user object.
    /// </summary>
    public UserId Id { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public Username Username { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public UserInfo? Info { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public override ObjectKind Kind { get; } = ObjectKind.User;
}
