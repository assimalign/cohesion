using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Routing;

/// <summary>
/// The HTTP logging middleware: captures request/response metadata for one exchange and emits a
/// single structured <see cref="LoggerEntry"/> through the composed <see cref="ILogger"/> when
/// the downstream pipeline completes (or faults). All configuration is builder-time
/// (<see cref="HttpLoggingSnapshot"/>); the middleware performs no service location and no
/// reflection.
/// </summary>
internal sealed class HttpLoggingMiddleware : IWebApplicationMiddleware
{
    private const HttpLoggingFields CaptureFields =
        HttpLoggingFields.RequestBody | HttpLoggingFields.ResponseBody | HttpLoggingFields.BytesTransferred;

    private readonly ILogger _logger;
    private readonly HttpLoggingSnapshot _snapshot;

    public HttpLoggingMiddleware(ILogger logger, HttpLoggingSnapshot snapshot)
    {
        _logger = logger;
        _snapshot = snapshot;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        if (!_logger.IsEnabled(_snapshot.Level))
        {
            // The composed logger would drop the entry anyway: skip timing and capture entirely.
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        long started = _snapshot.TimeProvider.GetTimestamp();
        HttpLoggingFields configured = _snapshot.Fields;

        // Arm body observation before the downstream pipeline runs. Captures tee the first N
        // bytes only - the body itself streams through, so h2 flow-control backpressure and the
        // transport's request-size limits are untouched.
        RequestBodyCaptureStream? requestCapture = null;
        Stream? originalRequestBody = null;
        HttpRequest? concreteRequest = context.Request as HttpRequest;

        bool wantBytes = (configured & HttpLoggingFields.BytesTransferred) != 0;
        bool captureRequestBody = (configured & HttpLoggingFields.RequestBody) != 0
            && concreteRequest is not null
            && _snapshot.RequestBodyLimit > 0
            && _snapshot.IsBodyContentTypeLoggable(context.Request.Headers[HttpHeaderKey.ContentType].Value);

        if (concreteRequest is not null && (captureRequestBody || wantBytes))
        {
            originalRequestBody = concreteRequest.Body;
            requestCapture = new RequestBodyCaptureStream(originalRequestBody, captureRequestBody ? _snapshot.RequestBodyLimit : 0);
            concreteRequest.Body = requestCapture;
        }

        ResponseBodyCaptureStream? responseCapture = null;
        Stream? originalResponseBody = null;
        bool captureResponseBody = (configured & HttpLoggingFields.ResponseBody) != 0 && _snapshot.ResponseBodyLimit > 0;

        if (captureResponseBody || wantBytes)
        {
            originalResponseBody = context.Response.Body;
            responseCapture = new ResponseBodyCaptureStream(
                originalResponseBody,
                captureResponseBody ? _snapshot.ResponseBodyLimit : 0,
                // The response Content-Type is unknown until the application sets it, so the
                // capture decision is deferred to the first body write.
                captureResponseBody ? () => _snapshot.IsBodyContentTypeLoggable(context.Response.Headers[HttpHeaderKey.ContentType].Value) : null);
            context.Response.Body = responseCapture;
        }

        ILogger exchangeLogger = _logger;
        IScopedLogger? scope = null;

        if (_snapshot.LogRequestStart)
        {
            // The start entry seeds a scope; the completion entry is written through it so the
            // two correlate via ILoggerEntry.ParentId.
            scope = _logger.BeginScope(CreateStartEntry(context));
            exchangeLogger = scope;
        }

        Exception? exception = null;
        try
        {
            await next.Invoke(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            exception = ex;
            throw;
        }
        finally
        {
            // Restore the streams we installed - but only when they are still the installed
            // ones. A downstream middleware that swapped in its own wrapper (compression, ...)
            // owns its restoration; clobbering it here would detach that wrapper mid-chain.
            if (requestCapture is not null && ReferenceEquals(concreteRequest!.Body, requestCapture))
            {
                concreteRequest.Body = originalRequestBody!;
            }

            if (responseCapture is not null && ReferenceEquals(context.Response.Body, responseCapture))
            {
                context.Response.Body = originalResponseBody!;
            }

            TimeSpan elapsed = _snapshot.TimeProvider.GetElapsedTime(started);
            HttpLoggingFields effective = ResolveEffectiveFields(context, configured);

            if (effective != HttpLoggingFields.None)
            {
                try
                {
                    Emit(exchangeLogger, context, effective, elapsed, requestCapture, responseCapture, exception);
                }
                catch
                {
                    // The access log must never fail an exchange (or mask the application's own
                    // exception while it unwinds). Sink failures are already isolated by the
                    // logging pipeline; this guards the attribute-building path itself.
                }
            }

            scope?.Dispose();
        }
    }

    /// <summary>
    /// Resolves the field set for the completed exchange: the configured fields unless the
    /// matched endpoint carries an <see cref="HttpLoggingMetadata"/> override (last-wins via the
    /// endpoint metadata bag). Capture fields can only narrow — they were armed (or not) before
    /// routing decided the endpoint.
    /// </summary>
    private static HttpLoggingFields ResolveEffectiveFields(IHttpContext context, HttpLoggingFields configured)
    {
        HttpLoggingMetadata? metadata = context.GetEndpointMetadata<HttpLoggingMetadata>();
        if (metadata is null)
        {
            return configured;
        }

        HttpLoggingFields effective = metadata.Fields;
        effective &= ~(CaptureFields & ~configured);
        return effective;
    }

    private LoggerEntry CreateStartEntry(IHttpContext context)
    {
        HttpLoggingFields fields = _snapshot.Fields;
        var attributes = new Dictionary<string, object?>(3, StringComparer.Ordinal)
        {
            [HttpLoggingAttributes.Event] = HttpLoggingAttributes.EventStart,
        };

        if ((fields & HttpLoggingFields.RequestMethod) != 0)
        {
            attributes[HttpLoggingAttributes.RequestMethod] = context.Request.Method.Value;
        }

        if ((fields & HttpLoggingFields.RequestPath) != 0)
        {
            attributes[HttpLoggingAttributes.RequestPath] = context.Request.Path.Value;
        }

        return new LoggerEntry(
            _snapshot.Level,
            _snapshot.Category,
            BuildMessage(context, fields, status: null, duration: null, faulted: false, started: true),
            attributes: attributes,
            timestamp: _snapshot.TimeProvider.GetUtcNow());
    }

    private void Emit(
        ILogger logger,
        IHttpContext context,
        HttpLoggingFields fields,
        TimeSpan elapsed,
        RequestBodyCaptureStream? requestCapture,
        ResponseBodyCaptureStream? responseCapture,
        Exception? exception)
    {
        IHttpRequest request = context.Request;
        IHttpResponse response = context.Response;

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HttpLoggingAttributes.Event] = HttpLoggingAttributes.EventExchange,
        };

        if ((fields & HttpLoggingFields.RequestMethod) != 0)
        {
            attributes[HttpLoggingAttributes.RequestMethod] = request.Method.Value;
        }

        if ((fields & HttpLoggingFields.RequestScheme) != 0 && request.Scheme != HttpScheme.None)
        {
            attributes[HttpLoggingAttributes.RequestScheme] = request.Scheme == HttpScheme.Https ? "https" : "http";
        }

        if ((fields & HttpLoggingFields.RequestHost) != 0 && !string.IsNullOrEmpty(request.Host.Value))
        {
            attributes[HttpLoggingAttributes.RequestHost] = request.Host.Value;
        }

        if ((fields & HttpLoggingFields.RequestPath) != 0)
        {
            attributes[HttpLoggingAttributes.RequestPath] = request.Path.Value;
        }

        if ((fields & HttpLoggingFields.RequestQuery) != 0 && request.Query.Count > 0)
        {
            attributes[HttpLoggingAttributes.RequestQuery] = SerializeQuery(request.Query);
        }

        if ((fields & HttpLoggingFields.RequestProtocol) != 0 && FormatProtocol(context.Version) is { } protocol)
        {
            attributes[HttpLoggingAttributes.RequestProtocol] = protocol;
        }

        if ((fields & HttpLoggingFields.RequestHeaders) != 0)
        {
            AddHeaders(attributes, request.Headers, HttpLoggingAttributes.RequestHeaderPrefix, _snapshot.AllowedRequestHeaders);
        }

        if ((fields & HttpLoggingFields.RequestBody) != 0 && requestCapture is not null && !requestCapture.Captured.IsEmpty)
        {
            attributes[HttpLoggingAttributes.RequestBody] = Encoding.UTF8.GetString(requestCapture.Captured);
        }

        if ((fields & HttpLoggingFields.ResponseStatusCode) != 0)
        {
            attributes[HttpLoggingAttributes.ResponseStatusCode] = response.StatusCode.Value;
        }

        if ((fields & HttpLoggingFields.ResponseHeaders) != 0)
        {
            AddHeaders(attributes, response.Headers, HttpLoggingAttributes.ResponseHeaderPrefix, _snapshot.AllowedResponseHeaders);
        }

        if ((fields & HttpLoggingFields.ResponseBody) != 0 && responseCapture is not null && !responseCapture.Captured.IsEmpty)
        {
            attributes[HttpLoggingAttributes.ResponseBody] = Encoding.UTF8.GetString(responseCapture.Captured);
        }

        if ((fields & HttpLoggingFields.BytesTransferred) != 0)
        {
            if (requestCapture is not null)
            {
                attributes[HttpLoggingAttributes.RequestBodyBytes] = requestCapture.BytesRead;
            }

            if (responseCapture is not null)
            {
                attributes[HttpLoggingAttributes.ResponseBodyBytes] = responseCapture.BytesWritten;
            }
        }

        if ((fields & HttpLoggingFields.Duration) != 0)
        {
            attributes[HttpLoggingAttributes.Duration] = elapsed.TotalMilliseconds;
        }

        if ((fields & HttpLoggingFields.ClientAddress) != 0)
        {
            if (ResolveClientAddress(context) is { } address)
            {
                attributes[HttpLoggingAttributes.ClientAddress] = address.ToString();
            }

            if (context.ConnectionInfo.RemotePort > 0)
            {
                attributes[HttpLoggingAttributes.ClientPort] = context.ConnectionInfo.RemotePort;
            }
        }

        if ((fields & HttpLoggingFields.TraceContext) != 0
            && request.Headers.TryGetValue(HttpHeaderKey.TraceParent, out HttpHeaderValue traceParent)
            && TraceParent.TryParse(traceParent.Value, out string traceId, out string spanId))
        {
            attributes[HttpLoggingAttributes.TraceId] = traceId;
            attributes[HttpLoggingAttributes.SpanId] = spanId;
        }

        var entry = new LoggerEntry(
            exception is null ? _snapshot.Level : LogLevel.Error,
            _snapshot.Category,
            BuildMessage(context, fields, response.StatusCode.Value, elapsed, faulted: exception is not null, started: false),
            exception,
            attributes,
            timestamp: _snapshot.TimeProvider.GetUtcNow());

        logger.Log(entry);
    }

    private void AddHeaders(
        Dictionary<string, object?> attributes,
        IHttpHeaderCollection headers,
        string prefix,
        FrozenSet<HttpHeaderKey> allowed)
    {
        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            string name = string.Concat(prefix, header.Key.Value.ToLowerInvariant());
            attributes[name] = allowed.Contains(header.Key) ? header.Value.Value : _snapshot.RedactedValue;
        }
    }

    private IPAddress? ResolveClientAddress(IHttpContext context)
    {
        if (_snapshot.ClientAddressResolver is { } resolver)
        {
            try
            {
                return resolver(context);
            }
            catch
            {
                // A faulting resolver must not fail the exchange; fall back to the socket peer.
            }
        }

        return context.ConnectionInfo.RemoteIp;
    }

    private static string BuildMessage(
        IHttpContext context,
        HttpLoggingFields fields,
        int? status,
        TimeSpan? duration,
        bool faulted,
        bool started)
    {
        string method = (fields & HttpLoggingFields.RequestMethod) != 0 ? context.Request.Method.Value : "-";
        string path = (fields & HttpLoggingFields.RequestPath) != 0 ? context.Request.Path.Value : "-";

        if (started)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{method} {path} started");
        }

        string statusText = status is { } s && (fields & HttpLoggingFields.ResponseStatusCode) != 0
            ? s.ToString(CultureInfo.InvariantCulture)
            : "-";
        string durationText = duration is { } d && (fields & HttpLoggingFields.Duration) != 0
            ? string.Create(CultureInfo.InvariantCulture, $" in {d.TotalMilliseconds:F3} ms")
            : string.Empty;
        string faultText = faulted ? " (faulted)" : string.Empty;

        return string.Create(CultureInfo.InvariantCulture, $"{method} {path} -> {statusText}{durationText}{faultText}");
    }

    private static string? FormatProtocol(HttpVersion version) => version switch
    {
        HttpVersion.Http11 => "HTTP/1.1",
        HttpVersion.Http20 => "HTTP/2",
        HttpVersion.Http30 => "HTTP/3",
        _ => null,
    };

    private static string SerializeQuery(IHttpQueryCollection query)
    {
        var builder = new StringBuilder();

        foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> pair in query)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(pair.Key.Value));

            if (!string.IsNullOrEmpty(pair.Value.Value))
            {
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(pair.Value.Value));
            }
        }

        return builder.ToString();
    }
}
