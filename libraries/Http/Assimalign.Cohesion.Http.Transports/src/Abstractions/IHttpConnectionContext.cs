using System;
using System.Threading;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

using Transports;

/// <summary>
/// 
/// How to use:
/// <code>
/// await foreach (var received in ReceiveAsync().WithCancellation(cancellationToken))
/// {
///     // TODO: Add HTTP Application code to execute
///     
///     await foreach (var sent in SendAsync(received).WithCancellation(cancellationToken))
///     {
///         await sent.DisposeAsync();
///     }
/// }
/// </code>
/// </summary>
public interface IHttpConnectionContext : ITransportConnectionContext
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
