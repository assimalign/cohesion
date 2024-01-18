using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public interface IHost : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// A callback function to be used to check the state of the server(s).
    /// </summary>
    /// <remarks>
    /// If set, the callback should be generic enough to handle multiple server checks.
    /// </remarks>
    HostServerStateCallbackAsync StateCallback { get; }
    /// <summary>
    /// Starts all the servers in the host
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask RunAsync(CancellationToken cancellationToken = default);
}
