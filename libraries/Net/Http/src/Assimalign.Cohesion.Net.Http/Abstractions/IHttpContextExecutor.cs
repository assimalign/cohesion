using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

/// <summary>
/// This represents the users implementation to handle the 
/// </summary>
public interface IHttpContextExecutor
{
    /// <summary>
    /// Represents the execution of the middle ware need
    /// </summary>
    /// <remarks>
    /// This method can only be call once within a single request
    /// </remarks>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>A <see cref="bool"/> when a context has been finished processing</returns>
    Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default);
}