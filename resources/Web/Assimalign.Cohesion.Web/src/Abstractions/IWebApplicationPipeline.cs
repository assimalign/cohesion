using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

/// <summary>
/// A web application's composed request pipeline.
/// </summary>
public interface IWebApplicationPipeline
{
    /// <summary>
    /// Runs the pipeline for one HTTP exchange.
    /// </summary>
    /// <param name="context">The exchange to process.</param>
    /// <param name="cancellationToken">
    /// The caller's token for this execution. Middleware delegates take no token of their own and
    /// observe cancellation through <see cref="IHttpContext.RequestCancelled"/>; the Web host's
    /// pipeline does not start an execution whose token is already cancelled.
    /// </param>
    /// <returns>A task that completes when the pipeline has finished with the exchange.</returns>
    Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default);
}