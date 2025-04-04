using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Http;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationMiddleware
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken);
}
