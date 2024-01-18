using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Identity;

/// <summary>
/// 
/// </summary>
public interface IIdentityProvider
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    ClaimsPrincipal Authenticate(IIdentityContext context);
}