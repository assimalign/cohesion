using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// An interface to create a
/// </summary>
public interface ITransportMiddleware
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task InvokeAsync(ITransportContext context, CancellationToken cancellationToken = default);
}
