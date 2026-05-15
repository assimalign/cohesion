using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides helpers for working with HTTP contexts.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    /// Deconstructs an abstract HTTP context into its core request-processing components.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="version">The HTTP version.</param>
    /// <param name="request">The current request.</param>
    /// <param name="response">The current response.</param>
    public static void Deconstruct(
        this HttpContext context,
        out HttpVersion version,
        out HttpRequest request,
        out HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(context);

        version = context.Version;
        request = context.Request;
        response = context.Response;
    }

    /// <summary>
    /// Deconstructs a context into its core request-processing components.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="version">The HTTP version.</param>
    /// <param name="request">The current request.</param>
    /// <param name="response">The current response.</param>
    public static void Deconstruct(
        this IHttpContext context,
        out HttpVersion version,
        out IHttpRequest request,
        out IHttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(context);

        version = context.Version;
        request = context.Request;
        response = context.Response;
    }
}
