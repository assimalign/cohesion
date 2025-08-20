using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public interface IApplicationCluster
{
    /// <summary>
    /// 
    /// </summary>
    IEnumerable<IApplicationClusterResource> Resources { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RunAsync(CancellationToken cancellationToken = default);
}
