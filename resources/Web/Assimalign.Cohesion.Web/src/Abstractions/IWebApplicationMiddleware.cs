using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationMiddleware
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next);
}
