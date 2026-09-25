using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.LogSpace.Hosting.Internal;

internal static class LogSpaceHttp
{
    private const int maxBodyBytes = 1024 * 1024;

    internal static async Task IngestAsync(IHttpContext context, ResourceContext resource, LogSegmentStore store)
    {
        string path = context.Request.Path.Value;
        if (path is not ("/v1/logs" or "/v1/traces" or "/v1/metrics"))
        { context.Response.StatusCode = HttpStatusCode.NotFound; return; }
        if (context.Request.Method != HttpMethod.Post)
        { context.Response.StatusCode = HttpStatusCode.MethodNotAllowed; context.Response.Headers[HttpHeaderKey.Allow] = "POST"; return; }
        if (!Authorize(context, resource, telemetry: true, out string? emitter)) { return; }
        string contentType = context.Request.Headers[HttpHeaderKey.ContentType].ToString().Split(';')[0].Trim();
        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = HttpStatusCode.UnsupportedMediaType;
            await DetailAsync(context, "This build accepts OTLP/JSON only (application/json).").ConfigureAwait(false);
            return;
        }
        if (path != "/v1/logs")
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
            await DetailAsync(context, "Only OTLP logs are implemented; traces and metrics are deferred.").ConfigureAwait(false);
            return;
        }
        if (!store.IsAvailable) { context.Response.StatusCode = HttpStatusCode.ServiceUnavailable; return; }
        if (long.TryParse(context.Request.Headers[HttpHeaderKey.ContentLength].ToString(), CultureInfo.InvariantCulture, out long contentLength) && contentLength > maxBodyBytes)
        {
            context.Response.StatusCode = HttpStatusCode.RequestEntityTooLarge;
            context.Response.Headers[HttpHeaderKey.Connection] = "close";
            return;
        }
        try
        {
            using var body = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), context.RequestCancelled).ConfigureAwait(false)) != 0)
                {
                    if (body.Length + read > maxBodyBytes)
                    {
                        context.Response.StatusCode = HttpStatusCode.RequestEntityTooLarge;
                        context.Response.Headers[HttpHeaderKey.Connection] = "close";
                        return;
                    }
                    body.Write(buffer, 0, read);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            using JsonDocument document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 32 });
            var records = OtlpLogReader.Read(document.RootElement, emitter);
            long rejected = 0;
            foreach (StoredLog record in records) { if (!store.TryEnqueue(record)) { rejected++; } }
            context.Response.StatusCode = HttpStatusCode.Ok;
            await JsonAsync(context, writer =>
            {
                writer.WriteStartObject(); writer.WriteStartObject("partialSuccess");
                if (rejected != 0)
                {
                    writer.WriteString("rejectedLogRecords", rejected.ToString(CultureInfo.InvariantCulture));
                    writer.WriteString("errorMessage", "The bounded segment queue is full or unavailable.");
                }
                writer.WriteEndObject(); writer.WriteEndObject();
            }).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) { context.Response.StatusCode = HttpStatusCode.Forbidden; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or System.Collections.Generic.KeyNotFoundException)
        { context.Response.StatusCode = HttpStatusCode.BadRequest; }
        catch (IOException) { context.Response.StatusCode = HttpStatusCode.BadRequest; }
    }

    internal static async Task QueryAsync(IHttpContext context, WebApplicationMiddleware next, ResourceContext resource, LogSegmentStore? store)
    {
        string path = context.Request.Path.Value;
        if (path == "/cohesion/v1" || path.StartsWith("/cohesion/v1/", StringComparison.Ordinal))
        {
            // Reject scoped emitter credentials on EVERY management route before the shared control plane.
            if (!Authorize(context, resource, telemetry: false, out _)) { return; }
        }
        if (path != "/cohesion/v1/logs") { await next(context).ConfigureAwait(false); return; }
        if (context.Request.Method != HttpMethod.Get)
        { context.Response.StatusCode = HttpStatusCode.MethodNotAllowed; context.Response.Headers[HttpHeaderKey.Allow] = "GET"; return; }
        try
        {
            string rawLimit = context.Request.Query["limit"].ToString();
            int limit = string.IsNullOrEmpty(rawLimit) ? 100 : int.Parse(rawLimit, CultureInfo.InvariantCulture);
            if (limit < 1 || limit > 1000) { throw new ArgumentException("limit must be between 1 and 1000."); }
            string rawSince = context.Request.Query["since"].ToString();
            DateTimeOffset? since = null;
            if (!string.IsNullOrEmpty(rawSince))
            {
                if (!DateTimeOffset.TryParseExact(rawSince, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset instant) ||
                    !(rawSince.EndsWith('Z') || rawSince.LastIndexOf('+') > 10 || rawSince.LastIndexOf('-') > 10))
                { throw new ArgumentException("since must be an ISO-8601 instant with a timezone."); }
                since = instant;
            }
            var page = store?.Query(context.Request.Query["resource"].ToString(), since, limit, context.Request.Query["cursor"].ToString())
                ?? (Body: string.Empty, Cursor: (string?)null);
            context.Response.StatusCode = HttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/x-ndjson";
            if (page.Cursor is not null) { context.Response.Headers["X-Cohesion-Next-Cursor"] = page.Cursor; }
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(page.Body), context.RequestCancelled).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        { context.Response.StatusCode = HttpStatusCode.BadRequest; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { context.Response.StatusCode = HttpStatusCode.ServiceUnavailable; }
    }

    private static bool Authorize(IHttpContext context, ResourceContext resource, bool telemetry, out string? emitter)
    {
        emitter = null;
        if (resource.GatewayName is null) { return true; }
        string header = context.Request.Headers[HttpHeaderKey.Authorization].ToString();
        LogSpaceTokenStatus status = LogSpaceTokenStatus.Unauthorized;
        if (header.Length < 16384 && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            using var verifier = new LogSpaceTokenVerifier(resource);
            status = verifier.Validate(header[7..].Trim(), resource.ResourceName!, DateTimeOffset.UtcNow, telemetry, out emitter);
        }
        if (status == LogSpaceTokenStatus.Authorized) { return true; }
        context.Response.StatusCode = status == LogSpaceTokenStatus.Forbidden ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        if (status == LogSpaceTokenStatus.Unauthorized) { context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer"; }
        return false;
    }

    private static Task DetailAsync(IHttpContext context, string detail) => JsonAsync(context, writer =>
    { writer.WriteStartObject(); writer.WriteString("detail", detail); writer.WriteEndObject(); });

    private static async Task JsonAsync(IHttpContext context, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) { write(writer); }
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestCancelled).ConfigureAwait(false);
    }
}
