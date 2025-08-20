using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A application host.
/// </summary>
public interface IHost : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// A unique identifier for the host.
    /// </summary>
    HostId Id { get; }

    /// <summary>
    /// The Host Context.
    /// </summary>
    IHostContext Context { get; }

    /// <summary>
    /// Starts the host.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}