using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.IdentityHub.Models;

public class Application : ServicePrincipal
{
    /// <summary>
    /// 
    /// </summary>
    public ApplicationId Id { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public ApplicationInfo? Info { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public virtual AccessModel AccessModel { get; } = AccessModel.None;

    /// <summary>
    /// 
    /// </summary>
    public override ObjectKind Kind { get; } = ObjectKind.Application;
}
