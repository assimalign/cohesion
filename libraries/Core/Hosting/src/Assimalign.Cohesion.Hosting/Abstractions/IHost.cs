using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A application host.
/// </summary>
public interface IHost : IDisposable
{
    /// <summary>
    /// The Host Context.
    /// </summary>
    IHostContext Context { get; }
    /// <summary>
    /// Starts all the services in the host
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RunAsync(CancellationToken cancellationToken = default);
}