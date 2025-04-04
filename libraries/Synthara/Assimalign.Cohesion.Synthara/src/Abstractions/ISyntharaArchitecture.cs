using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara;

/// <summary>
/// 
/// </summary>
public interface ISyntharaArchitecture
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
    Task ExecuteAsync(CancellationToken cancellationToken);
}
