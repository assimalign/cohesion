using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Net.Http;

/// <summary>
/// 
/// How to use:
/// <code>
/// await foreach (var received in ReceiveAsync().WithCancellation(cancellationToken))
/// {
///     // TODO: Add execution code
///     
///     await foreach (var sent in SendAsync(received).WithCancellation(cancellationToken))
///     {
///         await sent.DisposeAsync();
///     }
/// }
/// </code>
/// 
/// </summary>
public interface IHttpConnection
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