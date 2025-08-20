using System.Threading;
using System.Threading.Tasks;

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
public interface IHttpConnection : ITransportConnection
{
    /// <summary>
    /// Opens the inbound point to point connection that allows reading and writing.
    /// </summary>
    /// <returns></returns>
    IHttpConnectionContext Open();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<IHttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);
}