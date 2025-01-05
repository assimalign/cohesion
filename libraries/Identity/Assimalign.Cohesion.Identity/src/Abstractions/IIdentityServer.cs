using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Identity;

public interface IIdentityServer
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    Task<IIdentityResult> AuthorizeAsync(IIdentityContext context);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    Task<IIdentityResult> DiscoverAsync(IIdentityContext context);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    Task<IIdentityResult> GetTokenAsync(IIdentityContext context);
}
