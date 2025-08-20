using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IHttpApplication
{
    /// <summary>
    /// Invokes the application layer for processing the HTTP context.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
