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
    IWebApplicationContext Context { get; }

    /// <summary>
    /// 
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
