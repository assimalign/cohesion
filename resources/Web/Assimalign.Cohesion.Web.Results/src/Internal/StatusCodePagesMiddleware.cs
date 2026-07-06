using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

/// <summary>
/// Translates a bodyless 4xx/5xx terminal response into a response body. A middleware or terminal
/// fallback that sets an error status without writing content (the common shape for
/// <c>context.Response.StatusCode = 404</c>) leaves the client with a status line and no explanation;
/// this middleware fills that gap, defaulting to RFC 9457 problem+json.
/// </summary>
/// <remarks>
/// Composed once at builder time from a captured <see cref="StatusCodePagesOptions"/>; it resolves
/// nothing per request. It only acts when the response is genuinely bodyless, so it never clobbers a
/// response a handler already wrote.
/// </remarks>
internal sealed class StatusCodePagesMiddleware : IWebApplicationMiddleware
{
    private readonly StatusCodePagesOptions _options;

    public StatusCodePagesMiddleware(StatusCodePagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        await next.Invoke(context).ConfigureAwait(false);

        int status = context.Response.StatusCode.Value;
        if (status < 400 || status > 599)
        {
            return;
        }

        if (!IsBodyless(context.Response))
        {
            return;
        }

        if (_options.Responder is { } responder)
        {
            await responder.Invoke(context).ConfigureAwait(false);
        }
        else
        {
            await context.Response
                .WriteProblemDetailsAsync(ProblemDetails.FromStatus(context.Response.StatusCode), context.RequestCancelled)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports whether the response carries no body: no <c>Content-Type</c>, a zero/absent
    /// <c>Content-Length</c>, and no already-buffered content on a seekable body.
    /// </summary>
    private static bool IsBodyless(IHttpResponse response)
    {
        if (response.Headers.ContainsKey(HttpHeaderKey.ContentType))
        {
            return false;
        }

        if (response.Headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue length) &&
            long.TryParse(length.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long count) &&
            count > 0)
        {
            return false;
        }

        Stream body = response.Body;
        return !body.CanSeek || body.Length == 0;
    }
}
