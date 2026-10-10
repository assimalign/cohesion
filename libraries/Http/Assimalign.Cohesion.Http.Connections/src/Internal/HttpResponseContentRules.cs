namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The content and length rules every final response writer applies (#1073): which responses carry
/// content, when a <c>Content-Length</c> is synthesized from a staged body, and when one may be sent at
/// all. HTTP/1.1, HTTP/2, and HTTP/3 apply them identically, on the buffered path and through the raw
/// response body sink.
/// </summary>
/// <remarks>
/// <para>
/// <b>Responses without content.</b> A response to <c>HEAD</c> (RFC 9110 §9.3.2), a <c>204 No Content</c>
/// (§15.3.5), and a <c>304 Not Modified</c> (§15.4.5) end with their header section: no content and no
/// trailers. HTTP/1.1 frames them that way regardless of their fields (RFC 9112 §6.3), so a body written
/// after one is read as the start of the next response on a kept-alive connection. HTTP/2 and HTTP/3 make
/// <c>DATA</c> on one malformed (RFC 9113 §8.1.1, RFC 9114 §4.1.2). The writers therefore discard a staged
/// or streamed body for all three, and send no trailers.
/// </para>
/// <para>
/// <b>Synthesized length.</b> A buffered response the application left without a <c>Content-Length</c>
/// gets one from its staged body, except where that number would not be the length RFC 9110 §8.6 asks
/// for. A <c>HEAD</c> response's length is the one a <c>GET</c> would send, which a staged body states when
/// the handler produced one and nothing states when it produced none. A <c>304</c>'s length is the selected
/// representation's (§15.4.5), which the transport never knows. A <c>204</c> carries none at all.
/// </para>
/// <para>
/// <b>Sent length.</b> RFC 9110 §8.6: a server MUST NOT send <c>Content-Length</c> in a <c>204</c>. Each
/// head encoder skips the field on a <c>204</c>, an application-set one included, as it skips the
/// connection-specific fields on HTTP/2 and HTTP/3 (<see cref="HttpResponseFieldRules"/>): the application's
/// header collection is left as it was, and only the wire loses the field. A <c>304</c> keeps a length the
/// application set.
/// </para>
/// </remarks>
internal static class HttpResponseContentRules
{
    private const int NoContent = 204;
    private const int NotModified = 304;

    /// <summary>
    /// Determines whether a final response with <paramref name="statusCode"/> to a request with
    /// <paramref name="method"/> carries content and trailers.
    /// </summary>
    /// <param name="statusCode">The final response status code.</param>
    /// <param name="method">The request method.</param>
    /// <returns>
    /// <see langword="false"/> for a response to <c>HEAD</c>, a <c>204</c>, and a <c>304</c>; otherwise
    /// <see langword="true"/>.
    /// </returns>
    public static bool CarriesContent(HttpStatusCode statusCode, HttpMethod method)
    {
        return method != HttpMethod.Head && !IsContentlessStatus(statusCode);
    }

    /// <summary>
    /// Determines whether <paramref name="statusCode"/> is a final status whose response never carries
    /// content, whatever the request method: <c>204</c> and <c>304</c>.
    /// </summary>
    /// <param name="statusCode">The final response status code.</param>
    /// <returns><see langword="true"/> for <c>204</c> and <c>304</c>.</returns>
    public static bool IsContentlessStatus(HttpStatusCode statusCode)
    {
        return statusCode.Value is NoContent or NotModified;
    }

    /// <summary>
    /// Determines whether a buffered response the application left without a <c>Content-Length</c> gets
    /// one synthesized from its staged body.
    /// </summary>
    /// <param name="statusCode">The final response status code.</param>
    /// <param name="method">The request method.</param>
    /// <param name="bodyLength">The length of the staged body.</param>
    /// <returns>
    /// <see langword="false"/> for a <c>204</c>, a <c>304</c>, and a <c>HEAD</c> response whose staged body
    /// is empty; otherwise <see langword="true"/>.
    /// </returns>
    public static bool SynthesizesContentLength(HttpStatusCode statusCode, HttpMethod method, int bodyLength)
    {
        if (IsContentlessStatus(statusCode))
        {
            return false;
        }

        return method != HttpMethod.Head || bodyLength > 0;
    }

    /// <summary>
    /// Determines whether a final response head with <paramref name="statusCode"/> may carry a
    /// <c>Content-Length</c> field.
    /// </summary>
    /// <param name="statusCode">The final response status code.</param>
    /// <returns><see langword="false"/> for a <c>204</c>; otherwise <see langword="true"/>.</returns>
    public static bool PermitsContentLength(HttpStatusCode statusCode)
    {
        return statusCode.Value != NoContent;
    }
}
