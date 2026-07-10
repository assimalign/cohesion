using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// Result-writing helpers that render <see cref="ProblemDetails"/> onto an <see cref="IHttpResponse"/>.
/// </summary>
public static class HttpResponseProblemDetailsExtensions
{
    extension(IHttpResponse response)
    {
        /// <summary>
        /// Writes <paramref name="problem"/> to the response as <c>application/problem+json</c>
        /// (RFC 9457): sets the status code from <see cref="ProblemDetails.Status"/> when present,
        /// sets the <c>Content-Type</c> and <c>Content-Length</c> headers, and writes the serialized
        /// body using the AOT-safe default writer.
        /// </summary>
        /// <param name="problem">The problem details to write.</param>
        /// <param name="cancellationToken">A token that cancels the body write.</param>
        /// <returns>A task that completes when the payload has been written to the response body.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
        public async Task WriteProblemDetailsAsync(ProblemDetails problem, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(problem);

            byte[] payload = ProblemDetailsJsonWriter.Instance.WriteToUtf8Bytes(problem);

            if (problem.Status is int status)
            {
                response.StatusCode = status;
            }

            response.Headers[HttpHeaderKey.ContentType] = ProblemDetailsDefaults.MediaType;
            response.Headers[HttpHeaderKey.ContentLength] = payload.Length.ToString(CultureInfo.InvariantCulture);

            await response.Body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }
}
