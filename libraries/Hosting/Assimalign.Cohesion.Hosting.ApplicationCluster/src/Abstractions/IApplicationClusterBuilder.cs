using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

public interface IApplicationClusterBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="application"></param>
    /// <returns></returns>
    IApplicationClusterBuilder AddApplication(IApplicationClusterResource application);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IApplicationCluster Build();

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task<IApplicationCluster> BuildAsync(CancellationToken cancellationToken = default);
}
