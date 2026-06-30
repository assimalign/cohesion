using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationServer
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
