using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Integration;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// Serves one OpenAPI document representation (one line, one format) at a route.
/// </summary>
/// <remarks>
/// <para>
/// The route table is closed once the application starts, so the document cannot change afterwards. The
/// first request builds and serializes it; every later request is served those bytes. A failed build is
/// not cached: the exception reaches the pipeline's exception boundary and the next request tries again,
/// so a failure that does not recur (a document transformer that throws once) does not leave the endpoint
/// failing until a restart. A composition error recurs on every attempt until the application is fixed.
/// </para>
/// <para>
/// The representation carries a strong entity tag, the SHA-256 of its bytes, and a matching
/// <c>If-None-Match</c> is answered <c>304 Not Modified</c> (RFC 9110 §13.1.2), so a client that keeps the
/// document revalidates without downloading it again. <c>HEAD</c> receives the same header section with
/// no body.
/// </para>
/// </remarks>
internal sealed class OpenApiDocumentEndpoint
{
    private readonly IOpenApiDescriptionProvider _provider;
    private readonly OpenApiSpecVersion _version;
    private readonly OpenApiFormat _format;
    private readonly string _contentType;
    private readonly Lock _gate = new();
    private Representation? _representation;

    public OpenApiDocumentEndpoint(IOpenApiDescriptionProvider provider, OpenApiSpecVersion version, OpenApiFormat format)
    {
        _provider = provider;
        _version = version;
        _format = format;
        _contentType = format == OpenApiFormat.Yaml ? "application/yaml" : "application/json; charset=utf-8";
    }

    /// <summary>
    /// Writes the document, or a <c>304</c> when the client already holds it.
    /// </summary>
    /// <param name="context">The matched request.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public async Task InvokeAsync(IHttpContext context)
    {
        Representation representation = GetRepresentation();
        IHttpResponse response = context.Response;

        response.Headers[HttpHeaderKey.ETag] = representation.ETag.ToString();

        switch (Evaluate(context.Request, representation.ETag))
        {
            case HttpPreconditionOutcome.NotModified:
                response.StatusCode = HttpStatusCode.NotModified;
                return;
            case HttpPreconditionOutcome.PreconditionFailed:
                response.StatusCode = HttpStatusCode.PreconditionFailed;
                return;
        }

        response.StatusCode = HttpStatusCode.Ok;
        response.Headers[HttpHeaderKey.ContentType] = _contentType;
        response.Headers[HttpHeaderKey.ContentLength] = representation.Content.Length.ToString(CultureInfo.InvariantCulture);

        if (context.Request.Method == HttpMethod.Head)
        {
            return; // RFC 9110 §9.3.2: the GET header section, no body
        }

        await response.Body.WriteAsync(representation.Content, context.RequestCancelled).ConfigureAwait(false);
    }

    private Representation GetRepresentation()
    {
        if (Volatile.Read(ref _representation) is { } built)
        {
            return built;
        }

        lock (_gate)
        {
            if (_representation is null)
            {
                OpenApiDocument document = _provider.GetDocument(_version);
                string text = OpenApiIntegration.CreateExporter().Export(document, _format);
                byte[] content = Encoding.UTF8.GetBytes(text);

                // A strong validator: the bytes never change for the life of the application.
                HttpEntityTag etag = HttpEntityTag.Strong(Convert.ToHexStringLower(SHA256.HashData(content)));

                Volatile.Write(ref _representation, new Representation(content, etag));
            }

            return _representation;
        }
    }

    private static HttpPreconditionOutcome Evaluate(IHttpRequest request, HttpEntityTag etag)
    {
        HttpEntityTagCondition? ifMatch = null;
        HttpEntityTagCondition? ifNoneMatch = null;

        if (request.Headers.TryGetValue(HttpHeaderKey.IfMatch, out HttpHeaderValue ifMatchValue)
            && HttpEntityTagCondition.TryParse((string?)ifMatchValue, out HttpEntityTagCondition parsedIfMatch))
        {
            ifMatch = parsedIfMatch;
        }

        if (request.Headers.TryGetValue(HttpHeaderKey.IfNoneMatch, out HttpHeaderValue ifNoneMatchValue)
            && HttpEntityTagCondition.TryParse((string?)ifNoneMatchValue, out HttpEntityTagCondition parsedIfNoneMatch))
        {
            ifNoneMatch = parsedIfNoneMatch;
        }

        return HttpConditionalRequest.Evaluate(new HttpConditionalRequestContext
        {
            Method = request.Method,
            ETag = etag,
            IfMatch = ifMatch,
            IfNoneMatch = ifNoneMatch
        });
    }

    private sealed record Representation(byte[] Content, HttpEntityTag ETag);
}
