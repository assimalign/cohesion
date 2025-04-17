using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationPipeline
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken);
}
