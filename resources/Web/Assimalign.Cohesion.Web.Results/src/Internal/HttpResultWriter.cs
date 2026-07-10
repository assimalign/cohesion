using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results.Internal;

using Assimalign.Cohesion.Http;

/// <summary>
/// Shared write path for the buffered built-in results: sets the status code and entity headers,
/// then writes a fully materialized payload to the response body. Buffered results know their
/// exact length up front, so <c>Content-Length</c> is always set and the transport never needs to
/// chunk; the streaming results (<c>PushStream</c>, Server-Sent Events) deliberately do not use
/// this path.
/// </summary>
internal static class HttpResultWriter
{
    /// <summary>
    /// Writes a buffered payload: optional status code, <c>Content-Type</c>, <c>Content-Length</c>,
    /// then the body bytes.
    /// </summary>
    /// <param name="context">The exchange to write onto.</param>
    /// <param name="statusCode">The status code to set, or <see langword="null"/> to leave the current one.</param>
    /// <param name="contentType">The <c>Content-Type</c> to set, or <see langword="null"/> to leave the header unset.</param>
    /// <param name="payload">The response body bytes.</param>
    /// <param name="cancellationToken">A token that cancels the body write.</param>
    public static async Task WritePayloadAsync(
        IHttpContext context,
        HttpStatusCode? statusCode,
        string? contentType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        IHttpResponse response = context.Response;

        if (statusCode is HttpStatusCode status)
        {
            response.StatusCode = status;
        }

        if (contentType is not null)
        {
            response.Headers[HttpHeaderKey.ContentType] = contentType;
        }

        response.Headers[HttpHeaderKey.ContentLength] = payload.Length.ToString(CultureInfo.InvariantCulture);

        await response.Body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}
