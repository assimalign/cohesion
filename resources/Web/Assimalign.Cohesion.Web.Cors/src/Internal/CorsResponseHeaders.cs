using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Cors.Internal;

/// <summary>
/// Evaluates a request against a policy and writes the CORS response headers the Fetch standard's CORS
/// protocol defines: for an actual request the allowed origin, credentials and exposed headers; for a
/// preflight the allowed origin, credentials, methods, headers and max age, or none of them.
/// </summary>
internal static class CorsResponseHeaders
{
    private const string anyOrigin = "*";
    private const string credentialsAllowed = "true";
    private const string varyOrigin = "Origin";

    /// <summary>
    /// Gets whether the request is a CORS preflight: an <c>OPTIONS</c> request with an <c>Origin</c> and a
    /// single, well-formed <c>Access-Control-Request-Method</c>. The rule is routing's, so the two
    /// components always agree on which requests are preflights.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="method">The requested method exactly as sent (methods are compared byte for byte).</param>
    internal static bool TryGetPreflightMethod(IHttpRequest request, out string method)
    {
        method = string.Empty;

        if (request.Method != HttpMethod.Options
            || !request.Headers.ContainsKey(HttpHeaderKey.Origin)
            || !request.Headers.TryGetValue(HttpHeaderKey.AccessControlRequestMethod, out HttpHeaderValue requested)
            || requested.Count != 1)
        {
            return false;
        }

        string token = requested.Value.Trim();

        if (token.Length > CorsSyntax.MaximumMethodLength || !CorsSyntax.IsToken(token))
        {
            return false;
        }

        method = token;
        return true;
    }

    /// <summary>
    /// Gets the <c>Access-Control-Allow-Origin</c> value for the request: <c>*</c> for a policy that allows
    /// any origin, the request's own origin when the policy allows it, otherwise <see langword="null"/>
    /// (no request origin, more than one, or one the policy does not allow).
    /// </summary>
    internal static string? ResolveAllowedOrigin(CorsPolicy policy, IHttpRequest request)
    {
        if (policy.AllowsAnyOrigin)
        {
            return anyOrigin;
        }

        if (!request.Headers.TryGetValue(HttpHeaderKey.Origin, out HttpHeaderValue value) || value.Count != 1)
        {
            return null;
        }

        string? origin = value[0];

        return !string.IsNullOrEmpty(origin) && policy.AllowsOrigin(origin) ? origin : null;
    }

    /// <summary>
    /// Writes the headers of an actual (non-preflight) response. A policy that allows any origin writes
    /// them on every response, CORS request or not, because a cache may hand a response to a later CORS
    /// request; any other policy writes them only for an allowed origin, and adds <c>Vary: Origin</c> to
    /// every response, allowed or not, for the same reason (Fetch, "CORS protocol and HTTP caches").
    /// </summary>
    /// <remarks>
    /// The policy is authoritative for the headers it governs: a CORS header the decision does not include
    /// is removed, so a value an earlier component set (a second <c>UseCors</c>, a handler) can never widen
    /// what the policy grants. An endpoint that manages its own CORS headers opts out with <c>DisableCors</c>.
    /// </remarks>
    /// <param name="headers">The response headers.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="allowedOrigin">The value from <see cref="ResolveAllowedOrigin"/>.</param>
    internal static void WriteActual(IHttpHeaderCollection headers, CorsPolicy policy, string? allowedOrigin)
    {
        if (allowedOrigin is null)
        {
            headers.Remove(HttpHeaderKey.AccessControlAllowOrigin);
            headers.Remove(HttpHeaderKey.AccessControlAllowCredentials);
            headers.Remove(HttpHeaderKey.AccessControlExposeHeaders);
        }
        else
        {
            headers[HttpHeaderKey.AccessControlAllowOrigin] = allowedOrigin;
            Set(headers, HttpHeaderKey.AccessControlAllowCredentials, policy.SupportsCredentials ? credentialsAllowed : null);
            Set(headers, HttpHeaderKey.AccessControlExposeHeaders, policy.ExposeHeadersValue);
        }

        if (policy.VariesByOrigin)
        {
            AppendVaryOrigin(headers);
        }
    }

    /// <summary>
    /// Answers a preflight with <c>204 No Content</c>. When the policy allows the origin, the requested
    /// method and every requested header, the answer carries the full grant; otherwise it carries no CORS
    /// header at all, and the browser fails the preflight.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="requestedMethod">The method from <see cref="TryGetPreflightMethod"/>.</param>
    internal static void WritePreflight(IHttpContext context, CorsPolicy policy, string requestedMethod)
    {
        IHttpResponse response = context.Response;
        IHttpHeaderCollection headers = response.Headers;

        response.StatusCode = HttpStatusCode.NoContent;

        // The answer is the policy's alone: whatever CORS header an earlier component set is replaced.
        headers.Remove(HttpHeaderKey.AccessControlAllowOrigin);
        headers.Remove(HttpHeaderKey.AccessControlAllowCredentials);
        headers.Remove(HttpHeaderKey.AccessControlAllowMethods);
        headers.Remove(HttpHeaderKey.AccessControlAllowHeaders);
        headers.Remove(HttpHeaderKey.AccessControlMaxAge);
        headers.Remove(HttpHeaderKey.AccessControlExposeHeaders);

        if (ResolveAllowedOrigin(policy, context.Request) is { } allowedOrigin
            && policy.AllowsMethod(requestedMethod)
            && TryResolveAllowedHeaders(policy, context.Request.Headers, out string? allowedHeaders))
        {
            headers[HttpHeaderKey.AccessControlAllowOrigin] = allowedOrigin;
            Set(headers, HttpHeaderKey.AccessControlAllowCredentials, policy.SupportsCredentials ? credentialsAllowed : null);

            // "Any method" names the requested method itself rather than '*', which a credentialed request
            // would read as a method literally named '*'. A list is sent whole, so the browser caches every
            // listed method from one preflight.
            Set(headers, HttpHeaderKey.AccessControlAllowMethods, policy.AllowsAnyMethod ? requestedMethod : policy.AllowMethodsValue);
            Set(headers, HttpHeaderKey.AccessControlAllowHeaders, allowedHeaders);
            Set(headers, HttpHeaderKey.AccessControlMaxAge, policy.MaxAgeValue);
        }

        if (policy.VariesByOrigin)
        {
            AppendVaryOrigin(headers);
        }
    }

    // Sets the header, or removes it when the decision has no value for it.
    private static void Set(IHttpHeaderCollection headers, HttpHeaderKey key, string? value)
    {
        if (value is null)
        {
            headers.Remove(key);
        }
        else
        {
            headers[key] = value;
        }
    }

    // Every name in Access-Control-Request-Headers needs approval: a browser lists only the headers that
    // are not CORS-safelisted. "Any header" echoes the requested names, so Authorization, which a '*' answer
    // never covers, is approved too; a list is sent whole.
    private static bool TryResolveAllowedHeaders(CorsPolicy policy, IHttpHeaderCollection requestHeaders, out string? allowedHeaders)
    {
        allowedHeaders = policy.AllowsAnyHeader ? null : policy.AllowHeadersValue;

        if (!requestHeaders.TryGetValue(HttpHeaderKey.AccessControlRequestHeaders, out HttpHeaderValue requested)
            || requested.IsEmpty)
        {
            return true;
        }

        bool anyRequested = false;

        for (int i = 0; i < requested.Count; i++)
        {
            string? line = requested[i];

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            ReadOnlySpan<char> remaining = line;

            foreach (Range range in remaining.Split(','))
            {
                ReadOnlySpan<char> name = remaining[range].Trim(" \t");

                if (name.IsEmpty)
                {
                    continue;
                }

                if (!CorsSyntax.IsToken(name) || !policy.AllowsHeader(name))
                {
                    allowedHeaders = null;
                    return false;
                }

                anyRequested = true;
            }
        }

        // The validated value holds only token characters, commas and whitespace, so it is safe to echo.
        if (policy.AllowsAnyHeader && anyRequested)
        {
            allowedHeaders = requested.Value;
        }

        return true;
    }

    // Appends the Origin token to Vary, preserving existing tokens; never duplicates it or overrides
    // 'Vary: *' (the same append Web.Serialization, Web.StaticFiles and Web.Compression perform).
    private static void AppendVaryOrigin(IHttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.Vary, out HttpHeaderValue existing) || existing.IsEmpty)
        {
            headers[HttpHeaderKey.Vary] = varyOrigin;
            return;
        }

        string current = existing.Value;
        ReadOnlySpan<char> tokens = current;

        foreach (Range range in tokens.Split(','))
        {
            ReadOnlySpan<char> token = tokens[range].Trim(" \t");

            if (token is "*" || token.Equals(varyOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        headers[HttpHeaderKey.Vary] = current + ", " + varyOrigin;
    }
}
