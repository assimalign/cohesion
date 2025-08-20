using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Groups logs into a commitable transaction. With asynchronous logs 
/// </summary>
public interface ITransactionLogger : ILogger
{
    /// <summary>
    /// 
    /// </summary>
    void Commit();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);
}
