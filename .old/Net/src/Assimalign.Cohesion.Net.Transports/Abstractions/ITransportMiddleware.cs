using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

/// <summary>
/// An interface to create a
/// </summary>
public interface ITransportMiddleware
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    Task InvokeAsync(ITransportContext context, TransportMiddlewareHandler next);
}
