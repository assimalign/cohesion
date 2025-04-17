using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Represents an abstraction of a web server.
/// </summary>
public interface IWebApplication 
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StopAsync(CancellationToken cancellationToken);
}
