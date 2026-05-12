using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// 
/// </summary>
public interface IApplication
{
    /// <summary>
    /// 
    /// </summary>
    IApplicationModel Model { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RunAsync(CancellationToken cancellationToken = default);
}
