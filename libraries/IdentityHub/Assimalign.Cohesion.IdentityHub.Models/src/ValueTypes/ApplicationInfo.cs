using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.IdentityHub.Models;

public sealed record class ApplicationInfo
{
    /// <summary>
    /// 
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public string? Description { get; set; }
}
