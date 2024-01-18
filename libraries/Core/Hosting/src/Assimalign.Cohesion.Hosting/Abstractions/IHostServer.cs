using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public interface IHostServer
{
    /// <summary>
    /// An <see cref="IAsyncResult"/> which is used as a handle 
    /// </summary>
    /// <remarks>
    /// The property should be updated dynamically. 
    /// </remarks>
    IHostServerState State { get; }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
