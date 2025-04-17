using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Synthara;

/// <summary>
/// 
/// </summary>
public interface ISyntharaApplication : IDisposable
{
    /// <summary>
    ///
    /// </summary>
    IEnumerable<ISyntharaAccount> Accounts { get;  }

    /// <summary>
    /// 
    /// </summary>
    IEnumerable<ISyntharaResource> Resources { get; }

    /// <summary>
    /// 
    /// </summary>
    IEnumerable<ISyntharaResourceGroup> ResourceGroups { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartAsync(CancellationToken cancellationToken);
}
