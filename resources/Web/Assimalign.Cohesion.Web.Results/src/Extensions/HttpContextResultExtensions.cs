using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// The execution glue between the pipeline's handler idiom and <see cref="IResult"/>: a
/// middleware or route handler that produced a result calls
/// <c>context.ExecuteResultAsync(result)</c> to write it onto the exchange.
/// </summary>
public static class HttpContextResultExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Executes <paramref name="result"/> against this exchange, writing its status, headers,
        /// and body onto the response.
        /// </summary>
        /// <param name="result">The result to execute.</param>
        /// <param name="cancellationToken">
        /// A token that cancels the response write. When omitted (or explicitly
        /// <see cref="CancellationToken.None"/>), the exchange's own
        /// <see cref="IHttpContext.RequestCancelled"/> token is used, so a result write never
        /// outlives its request — pass a linked token to tighten that, never to escape it.
        /// </param>
        /// <returns>A task that completes when the result has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="result"/> is <see langword="null"/>.</exception>
        public Task ExecuteResultAsync(IResult result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(result);

            if (cancellationToken == default)
            {
                cancellationToken = context.RequestCancelled;
            }

            return result.ExecuteAsync(context, cancellationToken);
        }
    }
}
