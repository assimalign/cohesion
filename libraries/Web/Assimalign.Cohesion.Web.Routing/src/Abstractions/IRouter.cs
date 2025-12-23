using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// 
/// </summary>
public interface IRouter
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RouteAsync(RouteContext context, CancellationToken cancellationToken = default);
}
