using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Defines the application delegate that processes an HTTP context.
/// </summary>
public interface IHttpApplication
{
    /// <summary>
    /// Processes the supplied HTTP context.
    /// </summary>
    /// <param name="context">The context being processed.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>A task that completes when processing has finished.</returns>
    ValueTask InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
